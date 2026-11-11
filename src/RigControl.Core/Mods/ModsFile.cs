using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RigControl.Core.Mods;

/// <summary>
/// Una biblioteca de mods: cada subcarpeta de "library" es un mod; activarlo = crear en "target"
/// una unión con su nombre que apunta a ella. Lo que haya en "target" que no sea un enlace a la
/// biblioteca (carpetas de verdad, enlaces de otros programas) nunca se toca salvo al importar.
/// </summary>
public sealed class ModLibrary
{
    public string Name { get; set; } = "";

    /// <summary>Carpeta con los mods (admite %VARIABLES%). Mejor en la misma unidad que "target": importar es renombrar.</summary>
    public string Library { get; set; } = "";

    /// <summary>Carpeta donde los busca el simulador, p. ej. Community de MSFS (admite %VARIABLES%).</summary>
    public string Target { get; set; } = "";

    /// <summary>Proceso del simulador: con él abierto no se cambia nada (tiene los archivos abiertos o ya los leyó).</summary>
    public string? Process { get; set; }

    /// <summary>Mods que todo conjunto deja activos (p. ej. Navigraph, el módulo de WinWing).</summary>
    public List<string> Always { get; set; } = [];
}

/// <summary>
/// Conjunto con nombre: por biblioteca, los mods que quedan activos (más los "always"); el resto
/// de esa biblioteca se desactiva. Las bibliotecas que no aparecen no se tocan.
/// </summary>
public sealed class ModSet
{
    public string Name { get; set; } = "";
    public Dictionary<string, List<string>> Mods { get; set; } = [];
}

/// <summary>%APPDATA%\RigControl\mods.json, editable a mano o desde la pantalla Mods.</summary>
public sealed class ModsFile
{
    public List<ModLibrary> Libraries { get; set; } = [];
    public List<ModSet> Sets { get; set; } = [];

    public static string DefaultPath { get; } =
        Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "mods.json");

    /// <summary>MSFS 2024 (Store) y las carpetas de mods de DCS openbeta, con la biblioteca en C:\RigControl\Mods.</summary>
    public static ModsFile CreateDefault() => new()
    {
        Libraries =
        [
            new ModLibrary
            {
                Name = "MSFS 2024",
                Library = @"C:\RigControl\Mods\MSFS 2024",
                Target = @"%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\Packages\Community",
                Process = "FlightSimulator2024",
            },
            new ModLibrary
            {
                Name = "DCS aircraft",
                Library = @"C:\RigControl\Mods\DCS aircraft",
                Target = @"%USERPROFILE%\Saved Games\DCS.openbeta\Mods\aircraft",
                Process = "DCS",
            },
            new ModLibrary
            {
                Name = "DCS tech",
                Library = @"C:\RigControl\Mods\DCS tech",
                Target = @"%USERPROFILE%\Saved Games\DCS.openbeta\Mods\tech",
                Process = "DCS",
            },
        ],
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>Sin archivo = el de por defecto. Un JSON roto o incoherente lanza excepción (no se debe sobrescribir).</summary>
    public static ModsFile Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return CreateDefault();
        var file = JsonSerializer.Deserialize<ModsFile>(File.ReadAllText(path), ReadOptions)
                   ?? throw new InvalidDataException("mods.json está vacío.");
        file.Check();
        return file;
    }

    public void Save(string? path = null)
    {
        Check();
        var full = Path.GetFullPath(path ?? DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (File.Exists(full)) File.Copy(full, full + ".bak", overwrite: true);
        var tmp = full + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, WriteOptions), new UTF8Encoding(false));
        File.Move(tmp, full, overwrite: true);
    }

    public ModLibrary? FindLibrary(string name) =>
        Libraries.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public ModSet? FindSet(string name) =>
        Sets.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Nombres únicos, rutas presentes, biblioteca distinta del destino y conjuntos que nombran bibliotecas que existen.</summary>
    public void Check()
    {
        foreach (var group in Libraries.GroupBy(l => l.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0) throw new InvalidDataException("mods.json: hay una biblioteca sin nombre.");
            if (group.Count() > 1) throw new InvalidDataException($"mods.json: la biblioteca «{group.Key}» está repetida.");
        }
        foreach (var lib in Libraries)
        {
            if (string.IsNullOrWhiteSpace(lib.Library) || string.IsNullOrWhiteSpace(lib.Target))
                throw new InvalidDataException($"mods.json: «{lib.Name}» necesita \"library\" y \"target\".");
            var a = ModManager.Expand(lib.Library);
            var b = ModManager.Expand(lib.Target);
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase)
                || b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"mods.json: en «{lib.Name}» la biblioteca y el destino no pueden ser la misma carpeta ni estar una dentro de otra.");
        }
        foreach (var group in Sets.GroupBy(s => s.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0) throw new InvalidDataException("mods.json: hay un conjunto sin nombre.");
            if (group.Count() > 1) throw new InvalidDataException($"mods.json: el conjunto «{group.Key}» está repetido.");
        }
        foreach (var set in Sets)
            foreach (var lib in set.Mods.Keys)
                if (FindLibrary(lib) is null)
                    throw new InvalidDataException($"mods.json: el conjunto «{set.Name}» usa la biblioteca «{lib}», que no existe.");
    }
}
