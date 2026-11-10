using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RigControl.Core.Hangar;

/// <summary>
/// Prioridad de CPU que se pone al proceso de un paso. Sin "tiempo real" a propósito: puede dejar
/// sin CPU al ratón, al audio o a los controladores USB.
/// </summary>
public enum HangarPriority { Idle, BelowNormal, Normal, AboveNormal, High }

/// <summary>Un programa de la secuencia de un hangar.</summary>
public sealed class HangarStep
{
    public string Name { get; set; } = "";

    /// <summary>Ruta de un programa (admite %VARIABLES%) o URI, p. ej. shell:AppsFolder\...!App o steam://rungameid/N.</summary>
    public string Target { get; set; } = "";

    public string? Args { get; set; }

    /// <summary>
    /// Nombre del proceso con el que se sabe que ya está abierto. Si falta se deduce de una ruta a
    /// un .exe. Hace falta en las URI y en los programas que arrancan a través de otro.
    /// </summary>
    public string? Process { get; set; }

    /// <summary>Espera tras aparecer el proceso, antes del paso siguiente. No se espera si ya estaba abierto.</summary>
    public double WaitSeconds { get; set; }

    /// <summary>Máximo esperando a que aparezca el proceso; pasado, se avisa y se sigue.</summary>
    public double TimeoutSeconds { get; set; } = 30;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Prioridad de CPU para todos los procesos con el nombre de "process" (null = no tocarla). Se
    /// pone al aparecer el proceso o, si ya estaba abierto, al pasar por el paso.
    /// </summary>
    public HangarPriority? Priority { get; set; }
}

/// <summary>Un conjunto de programas que se abren en orden para un tipo de vuelo.</summary>
public sealed class HangarProfile
{
    public string Name { get; set; } = "";

    /// <summary>Al terminar la secuencia, iniciar la ejecución automática de perfiles (solo en la app).</summary>
    public bool StartRun { get; set; }

    public List<HangarStep> Steps { get; set; } = [];
}

/// <summary>%APPDATA%\RigControl\hangar.json, editable a mano o desde la pantalla Hangar.</summary>
public sealed class HangarFile
{
    public List<HangarProfile> Hangars { get; set; } = [];

    public static string DefaultPath { get; } =
        Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "hangar.json");

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>Lee el archivo; si no existe, devuelve uno vacío. Un JSON roto lanza excepción (no se debe sobrescribir).</summary>
    public static HangarFile Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new HangarFile();
        return JsonSerializer.Deserialize<HangarFile>(File.ReadAllText(path), ReadOptions)
               ?? throw new InvalidDataException("hangar.json está vacío.");
    }

    /// <summary>Guarda dejando copia en ".bak" y escribiendo en un temporal que luego se renombra. No conserva // comentarios.</summary>
    public void Save(string? path = null)
    {
        var full = Path.GetFullPath(path ?? DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (File.Exists(full)) File.Copy(full, full + ".bak", overwrite: true);
        var tmp = full + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, WriteOptions), new UTF8Encoding(false));
        File.Move(tmp, full, overwrite: true);
    }
}
