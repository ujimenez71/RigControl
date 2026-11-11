using System.Diagnostics;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RigControl.Core.Snapshots;

/// <summary>Lo que dice el manifiesto de un .zip, más dónde está y cuánto ocupa.</summary>
public sealed record SnapshotInfo(string Path, string Group, string Name, bool Auto, DateTime Created, string Hash, int FileCount, long Bytes);

/// <summary>Resultado de restaurar: archivos escritos y la instantánea "antes de restaurar" (null si ya había una igual).</summary>
public sealed record RestoreResult(int Files, SnapshotInfo? Before);

/// <summary>
/// Instantáneas = un .zip por grupo y momento en store\&lt;grupo&gt;\, con un manifiesto
/// (rigcontrol-snapshot.json) y los archivos bajo files\. Reglas: restaurar exige el programa
/// del grupo cerrado, hace antes una instantánea automática y solo SOBRESCRIBE (nunca borra lo
/// que no estaba); las automáticas se saltan si nada cambió y se podan a keepAuto; las manuales
/// no se borran nunca solas.
/// </summary>
public sealed class SnapshotStore(string? store = null, Func<string, bool>? isRunning = null, int keepAuto = 10)
{
    private const string Manifest = "rigcontrol-snapshot.json";
    private const string FilesPrefix = "files/";

    private readonly string _store = Path.GetFullPath(store ?? SnapshotsFile.DefaultStore);
    private readonly Func<string, bool> _isRunning = isRunning ?? (name =>
    {
        var found = Process.GetProcessesByName(name);
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    });

    private sealed record ManifestData(string Group, string Name, bool Auto, DateTime Created, string Hash, string Root, List<string> Files);

    public string StoreDir => _store;

    public static string ExpandRoot(SnapshotGroup group) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(group.Root.Trim())).TrimEnd('\\');

    public bool IsLocked(SnapshotGroup group) => group.Process is { Length: > 0 } p && _isRunning(p);

    /// <summary>Archivos del grupo que existen ahora: ruta relativa (con \) -> ruta completa, ordenados.</summary>
    public static SortedDictionary<string, string> Collect(SnapshotGroup group)
    {
        var root = ExpandRoot(group);
        var files = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // no seguir uniones (p. ej. mods enlazados); sí ocultos y de sistema
        };
        foreach (var include in group.Include)
        {
            var full = Path.GetFullPath(Path.Combine(root, include));
            if (File.Exists(full)) Add(full);
            else if (Directory.Exists(full))
                foreach (var f in Directory.EnumerateFiles(full, "*", options)) Add(f);
        }
        return files;

        void Add(string full)
        {
            var rel = Path.GetRelativePath(root, full);
            if (rel.StartsWith("..", StringComparison.Ordinal)) return;
            var name = Path.GetFileName(full);
            var relSlash = rel.Replace('\\', '/'); // en MatchesSimpleExpression "\" es carácter de escape
            foreach (var pattern in group.Exclude)
            {
                var p = pattern.Replace('\\', '/');
                if (FileSystemName.MatchesSimpleExpression(p, name) || FileSystemName.MatchesSimpleExpression(p, relSlash)) return;
            }
            files[rel] = full;
        }
    }

    /// <summary>Las instantáneas de un grupo (o de todos), la más reciente primero. Un .zip ilegible se salta.</summary>
    public IReadOnlyList<SnapshotInfo> List(string? group = null)
    {
        var result = new List<SnapshotInfo>();
        if (!Directory.Exists(_store)) return result;
        var dirs = group is null ? Directory.EnumerateDirectories(_store) : [Path.Combine(_store, group)];
        foreach (var dir in dirs.Where(Directory.Exists))
            foreach (var zip in Directory.EnumerateFiles(dir, "*.zip"))
                if (Read(zip) is { } info) result.Add(info);
        result.Sort((a, b) => b.Created.CompareTo(a.Created));
        return result;
    }

    /// <summary>
    /// Guarda el grupo. Si "auto" y nada cambió desde la última (manual o automática) devuelve null
    /// sin crear nada. Lanza si no hay ningún archivo que guardar.
    /// </summary>
    public SnapshotInfo? Create(SnapshotGroup group, string name, bool auto)
    {
        var files = Collect(group);
        if (files.Count == 0) throw new InvalidOperationException($"no hay nada que guardar en {ExpandRoot(group)}");

        // Leer todo una vez (son archivos pequeños de configuración): hash y contenido coinciden.
        var contents = new List<(string Rel, byte[] Data, DateTime Time)>(files.Count);
        foreach (var (rel, full) in files)
        {
            byte[] data;
            try
            {
                using var s = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                data = new byte[s.Length];
                s.ReadExactly(data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; } // se borró o está bloqueado
            contents.Add((rel, data, File.GetLastWriteTime(full)));
        }
        var hash = HashOf(contents.Select(c => (c.Rel, c.Data)));

        if (auto && List(group.Name).FirstOrDefault() is { } latest && latest.Hash == hash) return null;

        var created = DateTime.Now;
        var dir = Path.Combine(_store, group.Name);
        Directory.CreateDirectory(dir);
        var baseName = $"{created:yyyy-MM-dd HHmmss}{(auto ? " auto" : "")}{(name.Length > 0 ? " " + Clean(name) : "")}";
        var path = Path.Combine(dir, baseName + ".zip");
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{baseName} ({i}).zip");

        var manifest = new ManifestData(group.Name, name, auto, created, hash, ExpandRoot(group), [.. contents.Select(c => c.Rel)]);
        var tmp = path + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var (rel, data, time) in contents)
            {
                var entry = zip.CreateEntry(FilesPrefix + rel.Replace('\\', '/'), CompressionLevel.Optimal);
                entry.LastWriteTime = time;
                using var s = entry.Open();
                s.Write(data);
            }
            using var m = zip.CreateEntry(Manifest).Open();
            JsonSerializer.Serialize(m, manifest);
        }
        File.Move(tmp, path);
        if (auto) Prune(group.Name);
        return Read(path);
    }

    /// <summary>
    /// Escribe los archivos de la instantánea en su sitio (sobrescribe; no borra lo que no estaba).
    /// Antes guarda el estado actual como automática "antes de restaurar".
    /// </summary>
    public RestoreResult Restore(SnapshotGroup group, SnapshotInfo snapshot)
    {
        if (IsLocked(group)) throw new InvalidOperationException($"{group.Process} está abierto: ciérralo para restaurar");
        if (!snapshot.Group.Equals(group.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"la instantánea es de «{snapshot.Group}», no de «{group.Name}»");

        SnapshotInfo? before = null;
        if (Collect(group).Count > 0) before = Create(group, $"antes de restaurar {snapshot.Created:dd/MM/yyyy HH:mm}", auto: true);

        var root = ExpandRoot(group);
        var count = 0;
        using var zip = ZipFile.OpenRead(snapshot.Path);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(FilesPrefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;
            var rel = entry.FullName[FilesPrefix.Length..].Replace('/', '\\');
            var full = Path.GetFullPath(Path.Combine(root, rel));
            if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"ruta fuera de {root} en la instantánea: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (File.Exists(full)) File.SetAttributes(full, FileAttributes.Normal);
            entry.ExtractToFile(full, overwrite: true);
            count++;
        }
        return new RestoreResult(count, before);
    }

    public void Delete(SnapshotInfo snapshot)
    {
        if (!Path.GetFullPath(snapshot.Path).StartsWith(_store + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("no es una instantánea de RigControl");
        File.Delete(snapshot.Path);
    }

    /// <summary>
    /// Automática de todos los grupos (al abrir un hangar). Por grupo: la nueva, null si nada
    /// cambió o no hay nada que guardar, o el error. Nunca lanza.
    /// </summary>
    public IReadOnlyList<(string Group, SnapshotInfo? Created, string? Error)> AutoAll(SnapshotsFile file, string reason)
    {
        var result = new List<(string, SnapshotInfo?, string?)>();
        foreach (var group in file.Groups)
        {
            try
            {
                result.Add(Collect(group).Count == 0 ? (group.Name, null, null) : (group.Name, Create(group, reason, auto: true), null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                result.Add((group.Name, null, ex.Message));
            }
        }
        return result;
    }

    /// <summary>Resumen de AutoAll para un mensaje, p. ej. "Instantáneas: DCS (nueva); sin cambios: MSFS 2024, RigControl."</summary>
    public static string Describe(IReadOnlyList<(string Group, SnapshotInfo? Created, string? Error)> results)
    {
        var made = results.Where(r => r.Created is not null).Select(r => r.Group).ToList();
        var errors = results.Where(r => r.Error is not null).Select(r => $"{r.Group}: {r.Error}").ToList();
        var parts = new List<string>();
        if (made.Count > 0) parts.Add($"nuevas de {string.Join(", ", made)}");
        if (made.Count < results.Count - errors.Count) parts.Add(made.Count == 0 ? "sin cambios" : "el resto sin cambios");
        if (errors.Count > 0) parts.Add($"no se pudo: {string.Join("; ", errors)}");
        return $"Instantáneas: {string.Join("; ", parts)}.";
    }

    /// <summary>Deja solo las keepAuto automáticas más recientes del grupo.</summary>
    public void Prune(string group)
    {
        foreach (var old in List(group).Where(s => s.Auto).Skip(keepAuto))
            try { File.Delete(old.Path); } catch (IOException) { }
    }

    /// <summary>Archivos de la instantánea comparados con lo que hay ahora: (relativa, estado).</summary>
    public IReadOnlyList<(string Rel, string State)> Compare(SnapshotGroup group, SnapshotInfo snapshot)
    {
        var now = Collect(group);
        var result = new List<(string, string)>();
        using var zip = ZipFile.OpenRead(snapshot.Path);
        var inZip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(FilesPrefix, StringComparison.Ordinal)))
        {
            var rel = entry.FullName[FilesPrefix.Length..].Replace('/', '\\');
            inZip.Add(rel);
            if (!now.TryGetValue(rel, out var full)) { result.Add((rel, "ya no existe")); continue; }
            using var a = entry.Open();
            var same = new FileInfo(full).Length == entry.Length && SameContent(a, full);
            if (!same) result.Add((rel, "cambiado"));
        }
        foreach (var rel in now.Keys.Where(r => !inZip.Contains(r))) result.Add((rel, "nuevo (no está en la instantánea)"));
        result.Sort((x, y) => string.Compare(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static bool SameContent(Stream zipped, string full)
    {
        try
        {
            using var f = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            zipped.CopyTo(ms);
            var data = new byte[f.Length];
            f.ReadExactly(data);
            return ms.GetBuffer().AsSpan(0, (int)ms.Length).SequenceEqual(data);
        }
        catch (IOException) { return false; }
    }

    private static SnapshotInfo? Read(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry(Manifest);
            if (entry is null) return null;
            using var s = entry.Open();
            var m = JsonSerializer.Deserialize<ManifestData>(s);
            if (m is null) return null;
            return new SnapshotInfo(zipPath, m.Group, m.Name, m.Auto, m.Created, m.Hash, m.Files.Count, new FileInfo(zipPath).Length);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string HashOf(IEnumerable<(string Rel, byte[] Data)> files)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (rel, data) in files)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(rel.ToLowerInvariant() + "\n" + data.Length + "\n"));
            sha.AppendData(data);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static string Clean(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        var s = sb.ToString();
        return s.Length > 60 ? s[..60] : s;
    }
}
