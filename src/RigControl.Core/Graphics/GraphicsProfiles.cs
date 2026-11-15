using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RigControl.Core.Graphics;

/// <summary>Un juego de ajustes gráficos con nombre para un simulador.</summary>
public sealed class GraphicsProfile
{
    public string Name { get; set; } = "";

    /// <summary>"dcs" o "msfs".</summary>
    public string Sim { get; set; } = "dcs";

    /// <summary>Pensado para VR: se revisa con las reglas de VR (y en MSFS solo toca los ajustes de VR).</summary>
    public bool Vr { get; set; } = true;

    public string? Notes { get; set; }

    /// <summary>
    /// Clave -> valor tal cual va en el archivo. DCS: "graphics.AA" = "\"DLAA\"" (options.lua, texto Lua),
    /// "VR.pixel_density" = "1", "autoexec.DLSS_Preset" = "'K'" (autoexec.cfg). MSFS: "Video.DLSSModeVR" = "QUALITY",
    /// "GraphicsVR.Shadows.Size" = "1536" (UserCfg.opt).
    /// </summary>
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.Ordinal);

    public string SimName => Sim == "msfs" ? "MSFS 2024" : "DCS World";
}

/// <summary>El visor, para las reglas de VR.</summary>
public sealed class HeadsetInfo
{
    public string Name { get; set; } = "Pimax Crystal Light";

    /// <summary>Refresco con el que se vuela (la Crystal Light admite 72, 90 y 120 Hz).</summary>
    public int RefreshHz { get; set; } = 90;

    /// <summary>La Crystal Light no tiene seguimiento ocular (la Crystal "normal" sí).</summary>
    public bool EyeTracking { get; set; }
}

/// <summary>%APPDATA%\RigControl\graficos.json: visor y perfiles gráficos. Editable a mano.</summary>
public sealed class GraphicsFile
{
    public HeadsetInfo Headset { get; set; } = new();
    public List<GraphicsProfile> Profiles { get; set; } = [];

    public static string DefaultPath { get; } = Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "graficos.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Lanza JsonException si está roto (la pantalla lo enseña y no lo sobrescribe).</summary>
    public static GraphicsFile Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new GraphicsFile();
        var file = JsonSerializer.Deserialize<GraphicsFile>(File.ReadAllText(path), Options) ?? new GraphicsFile();
        foreach (var p in file.Profiles) p.Settings = new Dictionary<string, string>(p.Settings, StringComparer.Ordinal);
        return file;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }

    public GraphicsProfile? Find(string name) =>
        Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Un ajuste que cambiaría: valor actual (null = no está) y el del perfil.</summary>
public sealed record GraphicsChange(string Key, string? Current, string Wanted)
{
    public string CurrentText => Current is null ? "(no está)" : LuaOptionsFile.Display(Current);
    public string WantedText => LuaOptionsFile.Display(Wanted);
}

public enum AdviceLevel { Info, Aviso }

/// <summary>Un consejo de las reglas de VR.</summary>
public sealed record GraphicsAdvice(AdviceLevel Level, string Key, string Text)
{
    public override string ToString() => $"{(Level == AdviceLevel.Aviso ? "AVISO" : "INFO ")} {Text}";
}

/// <summary>
/// Reglas para VR que no dependen del gusto: lo que no sirve en el visor o lo que lo limita seguro.
/// Prudentes a propósito (los demás ajustes son cuestión de calidad contra FPS: eso lo dirá la medición).
/// </summary>
public static class VrAdvisor
{
    public static List<GraphicsAdvice> Check(string sim, IReadOnlyDictionary<string, string> s, HeadsetInfo headset)
    {
        List<GraphicsAdvice> list = [];
        double? Num(string key) => s.TryGetValue(key, out var v)
                                   && double.TryParse(LuaOptionsFile.Display(v), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        bool On(string key) => s.TryGetValue(key, out var v) && LuaOptionsFile.Display(v).Trim() is "true" or "1";

        if (sim == "dcs")
        {
            if (Num("graphics.maxFPS") is { } max && max > 0 && max < headset.RefreshHz)
                list.Add(new(AdviceLevel.Aviso, "graphics.maxFPS",
                    $"Límite de FPS de DCS = {max:0}, por debajo del refresco del visor ({headset.RefreshHz} Hz): nunca llegará a {headset.RefreshHz}."));
            if (s.ContainsKey("VR.openxr_quadView") && !On("VR.openxr_quadView"))
                list.Add(new(AdviceLevel.Info, "VR.openxr_quadView",
                    $"Quad views desactivado. Con la {headset.Name} el foveado fijo de quad views suele ahorrar bastante GPU en DCS."));
            if (On("VR.openxr_eyeGaze") && !headset.EyeTracking)
                list.Add(new(AdviceLevel.Aviso, "VR.openxr_eyeGaze",
                    $"«Eye gaze» activado, pero la {headset.Name} no tiene seguimiento ocular: el foveado no seguirá la mirada."));
            if (Num("VR.pixel_density") is { } pd && pd > 1.0)
                list.Add(new(AdviceLevel.Info, "VR.pixel_density",
                    $"Pixel density {pd:0.##}: multiplica los píxeles por {pd * pd:0.##}. Para más nitidez suele salir más barato subir la resolución en Pimax Play."));
            foreach (var (key, name) in new[] { ("graphics.DOF", "profundidad de campo"), ("graphics.motionBlur", "desenfoque de movimiento"), ("graphics.LensEffects", "efectos de lente") })
                if (Num(key) is > 0)
                    list.Add(new(AdviceLevel.Info, key, $"{char.ToUpperInvariant(name[0])}{name[1..]} activado: en VR no aporta y cuesta FPS."));
            if (On("graphics.sync"))
                list.Add(new(AdviceLevel.Info, "graphics.sync", "Sincronización vertical activada: en VR el ritmo lo marca el visor; normalmente se deja desactivada."));
        }
        else
        {
            foreach (var (key, name) in new[] { ("GraphicsVR.DOF.Enabled", "profundidad de campo"), ("GraphicsVR.MotionBlur.Enabled", "desenfoque de movimiento") })
                if (On(key))
                    list.Add(new(AdviceLevel.Info, key, $"{char.ToUpperInvariant(name[0])}{name[1..]} activado en VR: no aporta en el visor y cuesta FPS."));
        }
        return list;
    }
}
