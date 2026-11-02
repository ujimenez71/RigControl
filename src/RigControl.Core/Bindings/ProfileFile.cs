using System.Text.Json;

namespace RigControl.Core.Bindings;

/// <summary>
/// Perfil tal como está escrito en el JSON (editable a mano). ProfileCompiler lo valida y lo
/// convierte en un CompiledProfile listo para el motor.
///
/// Formato de entrada: "alias:b12" (botón 12, base 1, como en monitor) o "alias:hat1.up"
/// (up/right/down/left; las diagonales activan las dos direcciones).
/// Zonas de eje (en %, ejes x, y, z, rx, ry, rz, s0, s1 como en monitor): "alias:z>90",
/// "alias:z<10" o "alias:z=40..60". Se comportan como un botón pulsado mientras el eje está dentro.
/// </summary>
public sealed class ProfileFile
{
    public string Name { get; set; } = "";
    public TimingFile Timing { get; set; } = new();

    /// <summary>
    /// Aviones a los que aplica (para "run auto"): trozos de la ruta del aircraft.cfg que da MSFS,
    /// p. ej. "uh1h" o "c172". "*" = comodín para cualquier avión sin perfil propio.
    /// </summary>
    public List<string> Aircraft { get; set; } = [];

    /// <summary>
    /// "msfs", "dcs" o "general". Opcional: si falta, se deduce de "aircraft" (todos "dcs:..." = DCS,
    /// alguno de MSFS = MSFS, ninguno o solo "*" = general).
    /// </summary>
    public string? Sim { get; set; }

    /// <summary>Histéresis de las zonas de eje, en % del recorrido.</summary>
    public double AxisHysteresis { get; set; } = 2;

    /// <summary>Alias local del perfil -> clave de dispositivo (VID:PID:serial, ver "catalog").</summary>
    public Dictionary<string, string> Devices { get; set; } = [];

    /// <summary>Capas con nombre: condiciones reutilizables (típicamente un modificador mantenido).</summary>
    public Dictionary<string, ConditionFile> Layers { get; set; } = [];

    public List<BindingFile> Bindings { get; set; } = [];

    /// <summary>
    /// Luces del Bravo según el simulador: nombre de la luz (ver BravoLed) -> condición o lista de
    /// condiciones que deben cumplirse todas, p. ej. "Autopilot": "A:AUTOPILOT MASTER".
    /// </summary>
    public Dictionary<string, JsonElement> Lights { get; set; } = [];

    public static string ProfilesDir { get; } =
        Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "profiles");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Acepta una ruta a un .json o el nombre de un perfil en %APPDATA%\RigControl\profiles.</summary>
    public static string Resolve(string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return Path.GetFullPath(nameOrPath);
        var fileName = nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? nameOrPath : nameOrPath + ".json";
        return Path.Combine(ProfilesDir, fileName);
    }

    public static ProfileFile Load(string path) =>
        JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException("El perfil está vacío.");
}

public sealed class TimingFile
{
    /// <summary>A partir de este tiempo mantenido, la pulsación es larga.</summary>
    public int LongMs { get; set; } = 500;

    /// <summary>Máximo entre soltar y volver a pulsar para contar como doble toque.</summary>
    public int DoubleMs { get; set; } = 300;

    /// <summary>Máximo entre dos pasos consecutivos de una secuencia.</summary>
    public int SequenceMs { get; set; } = 1000;
}

public sealed class BindingFile
{
    public string? Input { get; set; }

    /// <summary>press | release | short | long | hold | double. Se omite en las secuencias.</summary>
    public string? Gesture { get; set; }

    /// <summary>Entradas que hay que pulsar en orden. Excluyente con Input.</summary>
    public List<string>? Sequence { get; set; }

    public ConditionFile? When { get; set; }
    public string Action { get; set; } = "";

    /// <summary>Descripción para mostrar (opcional; si falta, se genera a partir de la acción).</summary>
    public string? Desc { get; set; }

    /// <summary>Solo en "hold" con K:/B:/L:: reenvía la acción cada tantos ms mientras se mantiene (p. ej. trim).</summary>
    public int? RepeatMs { get; set; }
}

/// <summary>Exactamente una de las propiedades debe tener valor.</summary>
public sealed class ConditionFile
{
    /// <summary>Verdadera mientras esa entrada está pulsada.</summary>
    public string? Input { get; set; }

    public string? Layer { get; set; }
    public List<ConditionFile>? All { get; set; }
    public List<ConditionFile>? Any { get; set; }
    public ConditionFile? Not { get; set; }
}
