using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RigControl.Core.Snapshots;

/// <summary>
/// Un grupo de configuración que se guarda junto: archivos y carpetas (relativos a "root") menos
/// lo excluido. "process" = programa con el que no se restaura (lee o reescribe esos archivos).
/// </summary>
public sealed class SnapshotGroup
{
    public string Name { get; set; } = "";

    /// <summary>Carpeta base (admite %VARIABLES%).</summary>
    public string Root { get; set; } = "";

    /// <summary>Archivos o carpetas relativos a root; lo que no existe se salta.</summary>
    public List<string> Include { get; set; } = [];

    /// <summary>Comodines (* ?) sobre el nombre del archivo o su ruta relativa, p. ej. "authdata.bin", "*.bak", "snapshots\*".</summary>
    public List<string> Exclude { get; set; } = [];

    public string? Process { get; set; }
}

/// <summary>%APPDATA%\RigControl\snapshots.json: grupos y cuántas automáticas se conservan.</summary>
public sealed class SnapshotsFile
{
    public List<SnapshotGroup> Groups { get; set; } = [];

    /// <summary>Instantáneas automáticas que se conservan por grupo (las manuales no se borran nunca solas).</summary>
    public int KeepAuto { get; set; } = 10;

    public static string DefaultPath { get; } =
        Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "snapshots.json");

    /// <summary>Carpeta de los .zip, una subcarpeta por grupo.</summary>
    public static string DefaultStore { get; } =
        Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "snapshots");

    /// <summary>DCS (sin credenciales), MSFS 2024 (sin los mandos de la nube) y la propia RigControl.</summary>
    public static SnapshotsFile CreateDefault() => new()
    {
        Groups =
        [
            new SnapshotGroup
            {
                Name = "DCS",
                Root = @"%USERPROFILE%\Saved Games\DCS.openbeta",
                Include = [@"Config", @"Scripts\Export.lua", @"Scripts\Hooks"],
                Exclude = ["authdata.bin", "network.vault"],
                Process = "DCS",
            },
            new SnapshotGroup
            {
                Name = "MSFS 2024",
                Root = @"%LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache",
                Include = ["UserCfg.opt", "FlightSimulator2024.CFG", "Cameras.CFG", "EXE.xml", "SimConnect.xml"],
                Process = "FlightSimulator2024",
            },
            new SnapshotGroup
            {
                Name = "RigControl",
                Root = @"%APPDATA%\RigControl",
                Include = ["."],
                Exclude = [@"snapshots\*", "*.bak", "*.tmp"],
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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>Sin archivo = el de por defecto. Un JSON roto o incoherente lanza excepción.</summary>
    public static SnapshotsFile Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return CreateDefault();
        var file = JsonSerializer.Deserialize<SnapshotsFile>(File.ReadAllText(path), ReadOptions)
                   ?? throw new InvalidDataException("snapshots.json está vacío.");
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

    public SnapshotGroup? FindGroup(string name) =>
        Groups.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void Check()
    {
        if (KeepAuto < 1) throw new InvalidDataException("snapshots.json: \"keepAuto\" debe ser al menos 1.");
        foreach (var group in Groups.GroupBy(g => g.Name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0) throw new InvalidDataException("snapshots.json: hay un grupo sin nombre.");
            if (group.Key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"snapshots.json: «{group.Key}» no vale como nombre de carpeta.");
            if (group.Count() > 1) throw new InvalidDataException($"snapshots.json: el grupo «{group.Key}» está repetido.");
        }
        foreach (var g in Groups)
        {
            if (string.IsNullOrWhiteSpace(g.Root)) throw new InvalidDataException($"snapshots.json: «{g.Name}» necesita \"root\".");
            if (g.Include.Count == 0) throw new InvalidDataException($"snapshots.json: «{g.Name}» no incluye nada.");
            foreach (var inc in g.Include)
                if (Path.IsPathRooted(inc) || inc.Split('\\', '/').Contains(".."))
                    throw new InvalidDataException($"snapshots.json: en «{g.Name}», \"{inc}\" debe ser relativo a root y sin «..».");
        }
    }
}
