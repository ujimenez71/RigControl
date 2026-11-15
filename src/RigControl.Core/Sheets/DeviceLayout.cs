using System.Text.Json;
using System.Text.Json.Serialization;

namespace RigControl.Core.Sheets;

/// <summary>Posición de una entrada sobre la foto, en fracciones (0..1) del ancho y del alto.</summary>
public sealed record MarkerPosition(double X, double Y);

/// <summary>
/// Foto de un mando y dónde está cada entrada en ella (para la hoja de mandos). Un archivo por modelo
/// de mando (VID:PID): %APPDATA%\RigControl\fotos\VVVV_PPPP.json + la imagen al lado. Editable a mano.
/// Claves de las entradas como en los perfiles, sin el alias: "b12", "hat1.up", "hat1" (todo el hat), "y" (eje).
/// </summary>
public sealed class DeviceLayout
{
    /// <summary>Nombre del archivo de imagen, en la misma carpeta.</summary>
    public string? Image { get; set; }

    public Dictionary<string, MarkerPosition> Markers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string Dir { get; } = Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "fotos");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>"346E:1000" o "346E:1000:serie" -> "346E_1000".</summary>
    public static string IdFor(string deviceKey)
    {
        var parts = deviceKey.Trim().Split(':');
        return parts.Length >= 2 ? $"{parts[0]}_{parts[1]}".ToUpperInvariant() : deviceKey.Replace(':', '_').ToUpperInvariant();
    }

    public static string PathFor(string deviceKey) => Path.Combine(Dir, IdFor(deviceKey) + ".json");

    /// <summary>La del mando, o una vacía si no hay (o si el archivo está roto: no se pierde nada al no guardarla).</summary>
    public static DeviceLayout Load(string deviceKey)
    {
        var path = PathFor(deviceKey);
        if (!File.Exists(path)) return new DeviceLayout();
        try
        {
            var layout = JsonSerializer.Deserialize<DeviceLayout>(File.ReadAllText(path), Options) ?? new DeviceLayout();
            layout.Markers = new Dictionary<string, MarkerPosition>(layout.Markers, StringComparer.OrdinalIgnoreCase);
            return layout;
        }
        catch (JsonException ex)
        {
            Log.Warning($"Foto de {IdFor(deviceKey)}: {Path.GetFileName(path)} no se puede leer ({ex.Message}).");
            return new DeviceLayout();
        }
    }

    public void Save(string deviceKey)
    {
        Directory.CreateDirectory(Dir);
        var path = PathFor(deviceKey);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Ruta completa de la imagen, o null si no hay o no existe.</summary>
    public string? ImagePath => Image is { Length: > 0 } i && File.Exists(Path.Combine(Dir, i)) ? Path.Combine(Dir, i) : null;

    /// <summary>
    /// Posición de una entrada de un perfil: exacta ("hat1.up", "b12") o, si no, la del grupo
    /// ("hat1" para sus direcciones, "y" para las zonas "y>95").
    /// </summary>
    public MarkerPosition? Find(string part)
    {
        part = part.Trim().ToLowerInvariant();
        if (Markers.TryGetValue(part, out var exact)) return exact;
        return Markers.TryGetValue(GroupOf(part), out var group) ? group : null;
    }

    /// <summary>"hat1.up" -> "hat1", "y>95" -> "y", "b12" -> "b12".</summary>
    public static string GroupOf(string part)
    {
        part = part.Trim().ToLowerInvariant();
        var dot = part.IndexOf('.');
        if (part.StartsWith("hat", StringComparison.Ordinal) && dot > 0) return part[..dot];
        var op = part.IndexOfAny(['<', '>', '=']);
        return op > 0 ? part[..op] : part;
    }
}
