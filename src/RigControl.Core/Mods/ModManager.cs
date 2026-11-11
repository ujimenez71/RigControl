using System.Diagnostics;

namespace RigControl.Core.Mods;

/// <summary>
/// Active = enlace en el destino a su carpeta de la biblioteca. Inactive = está en la biblioteca y
/// no en el destino. Foreign = está en el destino y no es nuestro (carpeta de verdad o enlace de
/// otro programa): no se toca; se puede importar. Conflict = está en la biblioteca, pero en el
/// destino hay otra cosa con su nombre. Broken = enlace nuestro a un mod que ya no está en la biblioteca.
/// </summary>
public enum ModState { Active, Inactive, Foreign, Conflict, Broken }

public sealed record ModEntry(string Name, ModState State, string? Detail = null);

/// <summary>Un cambio hecho (o que se haría) en una biblioteca; Error = no se hizo y por qué.</summary>
public sealed record ModChange(string Library, string Mod, string Action, string? Error = null)
{
    public override string ToString() => Error is null ? $"{Library}: {Action} {Mod}" : $"{Library}: {Action} {Mod} -> NO: {Error}";
}

/// <summary>
/// Activa y desactiva mods con uniones de directorio. Reglas: nunca se borra una carpeta de
/// verdad (solo enlaces que apuntan a la biblioteca); con el simulador abierto no se cambia nada;
/// importar mueve la carpeta a la biblioteca y deja en su sitio el enlace (sigue activa).
/// </summary>
public sealed class ModManager(Func<string, bool>? isRunning = null)
{
    private readonly Func<string, bool> _isRunning = isRunning ?? (name =>
    {
        var found = Process.GetProcessesByName(name);
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    });

    public static string Expand(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim())).TrimEnd('\\');

    /// <summary>¿Está abierto el simulador de esta biblioteca? Entonces no se toca.</summary>
    public bool IsLocked(ModLibrary library) => library.Process is { Length: > 0 } p && _isRunning(p);

    public IReadOnlyList<ModEntry> Scan(ModLibrary library)
    {
        var libDir = Expand(library.Library);
        var target = Expand(library.Target);
        var result = new List<ModEntry>();
        var inLibrary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(libDir))
            foreach (var dir in new DirectoryInfo(libDir).EnumerateDirectories())
            {
                if (dir.Name.StartsWith('.')) continue;
                inLibrary.Add(dir.Name);
                var placed = Path.Combine(target, dir.Name);
                if (!Directory.Exists(placed) && !File.Exists(placed)) { result.Add(new ModEntry(dir.Name, ModState.Inactive)); continue; }
                var linkTarget = Junction.TargetOf(placed);
                if (linkTarget is not null && linkTarget.Equals(dir.FullName.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    result.Add(new ModEntry(dir.Name, ModState.Active));
                else
                    result.Add(new ModEntry(dir.Name, ModState.Conflict, linkTarget is null
                        ? "en el destino hay una carpeta de verdad con el mismo nombre"
                        : $"en el destino hay un enlace a otra carpeta ({linkTarget})"));
            }

        if (Directory.Exists(target))
            foreach (var dir in new DirectoryInfo(target).EnumerateDirectories())
            {
                if (inLibrary.Contains(dir.Name)) continue;
                var linkTarget = Junction.TargetOf(dir.FullName);
                if (linkTarget is not null && IsInside(linkTarget, libDir))
                    result.Add(new ModEntry(dir.Name, ModState.Broken, "enlace a un mod que ya no está en la biblioteca"));
                else
                    result.Add(new ModEntry(dir.Name, ModState.Foreign, linkTarget is null ? null : $"enlace de otro programa a {linkTarget}"));
            }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public void Enable(ModLibrary library, string mod)
    {
        CheckUnlocked(library);
        var source = Path.Combine(Expand(library.Library), mod);
        if (!Directory.Exists(source)) throw new InvalidOperationException($"{mod} no está en la biblioteca");
        var target = Expand(library.Target);
        var placed = Path.Combine(target, mod);
        var current = Junction.TargetOf(placed);
        if (current is not null && current.Equals(source, StringComparison.OrdinalIgnoreCase)) return; // ya activo
        if (Path.Exists(placed)) throw new InvalidOperationException("en el destino ya hay otra cosa con ese nombre");
        Directory.CreateDirectory(target);
        Junction.Create(placed, source);
    }

    public void Disable(ModLibrary library, string mod)
    {
        CheckUnlocked(library);
        var libDir = Expand(library.Library);
        var placed = Path.Combine(Expand(library.Target), mod);
        if (!Path.Exists(placed)) return; // ya inactivo
        var current = Junction.TargetOf(placed);
        if (current is null || !IsInside(current, libDir))
            throw new InvalidOperationException("no es un enlace a la biblioteca: no se toca");
        Junction.Remove(placed);
    }

    /// <summary>
    /// Mueve una carpeta de verdad del destino a la biblioteca y deja un enlace en su sitio (sigue
    /// activa). Solo en la misma unidad: así es un renombrado instantáneo, sin copiar nada.
    /// </summary>
    public void Import(ModLibrary library, string mod)
    {
        CheckUnlocked(library);
        var libDir = Expand(library.Library);
        var placed = Path.Combine(Expand(library.Target), mod);
        var dest = Path.Combine(libDir, mod);
        if (!Directory.Exists(placed) || Junction.IsLink(placed)) throw new InvalidOperationException("solo se importan carpetas de verdad del destino");
        if (Path.Exists(dest)) throw new InvalidOperationException("ya hay un mod con ese nombre en la biblioteca");
        if (!string.Equals(Path.GetPathRoot(placed), Path.GetPathRoot(libDir), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"la biblioteca está en otra unidad ({Path.GetPathRoot(libDir)}): habría que copiar; muévelo a mano");
        Directory.CreateDirectory(libDir);
        Directory.Move(placed, dest);
        try
        {
            Junction.Create(placed, dest);
        }
        catch
        {
            Directory.Move(dest, placed); // dejarlo como estaba
            throw;
        }
    }

    /// <summary>Quita un enlace nuestro cuyo mod ya no está en la biblioteca.</summary>
    public void RemoveBroken(ModLibrary library, string mod)
    {
        CheckUnlocked(library);
        var placed = Path.Combine(Expand(library.Target), mod);
        var current = Junction.TargetOf(placed);
        if (current is null || !IsInside(current, Expand(library.Library)) || Directory.Exists(current))
            throw new InvalidOperationException("no es un enlace roto a la biblioteca");
        Junction.Remove(placed);
    }

    /// <summary>
    /// Deja cada biblioteca del conjunto con sus mods (más los "always") activos y el resto
    /// desactivado; quita también los enlaces rotos. dryRun = solo lo dice. Lo que no se pudo
    /// lleva Error; una biblioteca con el simulador abierto no se toca.
    /// </summary>
    public IReadOnlyList<ModChange> ApplySet(ModsFile file, ModSet set, bool dryRun = false)
    {
        var changes = new List<ModChange>();
        foreach (var (libName, mods) in set.Mods)
        {
            var library = file.FindLibrary(libName);
            if (library is null) { changes.Add(new ModChange(libName, "", "usar", "la biblioteca no existe")); continue; }
            if (IsLocked(library))
            {
                changes.Add(new ModChange(library.Name, "", "cambiar", $"{library.Process} está abierto: ciérralo para cambiar los mods"));
                continue;
            }
            var wanted = new HashSet<string>(mods.Concat(library.Always), StringComparer.OrdinalIgnoreCase);
            var entries = Scan(library);
            foreach (var name in wanted.Where(w => !entries.Any(e => e.Name.Equals(w, StringComparison.OrdinalIgnoreCase))))
                changes.Add(new ModChange(library.Name, name, "activar", "no está en la biblioteca"));

            foreach (var e in entries)
            {
                var want = wanted.Contains(e.Name);
                switch (e.State)
                {
                    case ModState.Active when !want:
                        changes.Add(Do(library.Name, e.Name, "desactivar", dryRun, () => Disable(library, e.Name)));
                        break;
                    case ModState.Inactive when want:
                        changes.Add(Do(library.Name, e.Name, "activar", dryRun, () => Enable(library, e.Name)));
                        break;
                    case ModState.Conflict when want:
                        changes.Add(new ModChange(library.Name, e.Name, "activar", e.Detail));
                        break;
                    case ModState.Foreign when !want && e.Detail is null:
                        changes.Add(new ModChange(library.Name, e.Name, "desactivar", "está suelto en el destino (sin importar): sigue activo; impórtalo para poder desactivarlo"));
                        break;
                    case ModState.Broken:
                        changes.Add(Do(library.Name, e.Name, "quitar enlace roto", dryRun, () => RemoveBroken(library, e.Name)));
                        break;
                }
            }
        }
        return changes;
    }

    /// <summary>Mods activos ahora en cada biblioteca, para guardarlos como conjunto (sin los "always").</summary>
    public Dictionary<string, List<string>> Snapshot(IEnumerable<ModLibrary> libraries)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var lib in libraries)
            result[lib.Name] = [.. Scan(lib).Where(e => e.State == ModState.Active && !lib.Always.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                                            .Select(e => e.Name)];
        return result;
    }

    private static ModChange Do(string library, string mod, string action, bool dryRun, Action work)
    {
        if (dryRun) return new ModChange(library, mod, action);
        try
        {
            work();
            return new ModChange(library, mod, action);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ModChange(library, mod, action, ex.Message);
        }
    }

    private void CheckUnlocked(ModLibrary library)
    {
        if (IsLocked(library)) throw new InvalidOperationException($"{library.Process} está abierto: ciérralo para cambiar los mods");
    }

    private static bool IsInside(string path, string dir) =>
        path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
