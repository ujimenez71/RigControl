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

public enum HangarWindowState { Normal, Minimized, Maximized }

/// <summary>
/// Cómo dejar las ventanas principales de un programa al aparecer. Lo que falta no se toca.
/// Coordenadas en píxeles reales del escritorio virtual (el monitor principal empieza en 0,0;
/// los demás pueden tener coordenadas negativas), como las da "Usar la posición actual".
/// </summary>
public sealed class HangarWindow
{
    public HangarWindowState? State { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>true = siempre encima; false = quitárselo; null = no tocarlo.</summary>
    public bool? Topmost { get; set; }

    [JsonIgnore] public bool HasPosition => X is not null;
    [JsonIgnore] public bool HasSize => Width is not null;
    [JsonIgnore] public bool IsEmpty => State is null && !HasPosition && !HasSize && Topmost is null;

    /// <summary>x/y y width/height van por parejas; el tamaño, positivo.</summary>
    public string? Problem()
    {
        if (X is null != Y is null) return "\"x\" e \"y\" van juntos";
        if (Width is null != Height is null) return "\"width\" y \"height\" van juntos";
        if (Width <= 0 || Height <= 0) return "el tamaño debe ser mayor que 0";
        return null;
    }
}

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

    /// <summary>Estado, posición y tamaño de sus ventanas al aparecer (null = no tocarlas). Necesita el proceso.</summary>
    public HangarWindow? Window { get; set; }

    /// <summary>
    /// Traer su ventana al frente al terminar la secuencia (o cuando aparezca, si aún no tiene).
    /// Si lo llevan varios pasos, vale el último.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Focus { get; set; }
}

/// <summary>Un conjunto de programas que se abren en orden para un tipo de vuelo.</summary>
public sealed class HangarProfile
{
    public string Name { get; set; } = "";

    /// <summary>Al terminar la secuencia, iniciar la ejecución automática de perfiles (solo en la app).</summary>
    public bool StartRun { get; set; }

    /// <summary>Conjunto de mods.json que se aplica antes de abrir nada (null = no tocar los mods).</summary>
    public string? ModSet { get; set; }

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
        var file = JsonSerializer.Deserialize<HangarFile>(File.ReadAllText(path), ReadOptions)
                   ?? throw new InvalidDataException("hangar.json está vacío.");
        foreach (var hangar in file.Hangars)
            foreach (var step in hangar.Steps)
                if (step.Window?.Problem() is { } problem)
                    throw new InvalidDataException($"{hangar.Name} / {step.Name}: en \"window\", {problem}.");
        return file;
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
