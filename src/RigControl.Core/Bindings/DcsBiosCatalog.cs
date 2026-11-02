using System.Text.Json;

namespace RigControl.Core.Bindings;

/// <summary>Un control de DCS-BIOS: nombre (para "D:"), descripción y cómo se lee su estado.</summary>
public sealed record DcsControl(string Name, string Category, string Description, string[] Inputs,
                                int? Address, int Mask, int Shift, int MaxValue, string[] Positions)
{
    /// <summary>Valor actual en la imagen de la cabina (null si el control no tiene salida entera).</summary>
    public int? Read(ReadOnlySpan<byte> image) =>
        Address is { } a && a + 1 < image.Length ? ((image[a] | image[a + 1] << 8) & Mask) >> Shift : null;

    /// <summary>Texto para el editor: "PLT_EXTL_ACOL_L_SW · Pilot Anti-Collision Lights Switch".</summary>
    public override string ToString() => $"{Name} · {Description}";
}

/// <summary>
/// Definiciones de controles de DCS-BIOS (Scripts\DCS-BIOS\doc\json\*.json), por avión. El índice
/// AircraftAliases.json dice qué archivos usa cada avión ("AH-64D_BLK_II" -> CommonData + AH-64D).
/// Busca la instalación en la carpeta de Saved Games de DCS que se use de verdad (la de log más
/// reciente: DCS.openbeta en este equipo). Solo para la interfaz y la carga de perfiles.
/// </summary>
public static class DcsBiosCatalog
{
    private static readonly Dictionary<string, IReadOnlyList<DcsControl>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    /// <summary>Carpeta doc\json de DCS-BIOS en la instalación de DCS activa, o null si no está instalado.</summary>
    public static string? JsonDir()
    {
        var savedGames = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
        if (!Directory.Exists(savedGames)) return null;
        return Directory.EnumerateDirectories(savedGames, "DCS*")
            .Select(d => (Dir: Path.Combine(d, "Scripts", "DCS-BIOS", "doc", "json"), Log: Path.Combine(d, "Logs", "dcs.log")))
            .Where(x => Directory.Exists(x.Dir))
            .OrderByDescending(x => File.Exists(x.Log) ? File.GetLastWriteTimeUtc(x.Log) : DateTime.MinValue)
            .Select(x => x.Dir)
            .FirstOrDefault();
    }

    /// <summary>
    /// Controles de un avión, por su nombre en DCS ("AH-64D_BLK_II") o en un perfil ("dcs:ah-64d").
    /// Lista vacía si no se encuentra. Se guarda en caché.
    /// </summary>
    public static IReadOnlyList<DcsControl> ForAircraft(string aircraft)
    {
        var name = aircraft.StartsWith("dcs:", StringComparison.OrdinalIgnoreCase) ? aircraft[4..] : aircraft;
        lock (Gate)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            var result = Load(name);
            Cache[name] = result;
            return result;
        }
    }

    private static IReadOnlyList<DcsControl> Load(string aircraft)
    {
        var dir = JsonDir();
        if (dir is null) return [];
        var modules = ModulesFor(dir, aircraft);
        var controls = new List<DcsControl>();
        foreach (var module in modules.Where(m => m != "CommonData")) // CommonData: altitud, rumbo... no son mandos
        {
            var file = Path.Combine(dir, module + ".json");
            if (!File.Exists(file)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var category in doc.RootElement.EnumerateObject())
                    foreach (var c in category.Value.EnumerateObject())
                        controls.Add(Parse(c.Name, category.Name, c.Value));
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
            {
                Log.Warning($"DCS-BIOS: no se pudo leer {Path.GetFileName(file)} ({ex.Message}).");
            }
        }
        return controls;
    }

    /// <summary>Módulos de DCS-BIOS de un avión según AircraftAliases.json (sin distinguir mayúsculas; "dcs:ah-64d" encaja con "AH-64D_BLK_II").</summary>
    private static List<string> ModulesFor(string dir, string aircraft)
    {
        var aliases = Path.Combine(dir, "AircraftAliases.json");
        if (!File.Exists(aliases)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(aliases));
        JsonElement? best = null;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Name.Length == 0) continue;
            if (p.Name.Equals(aircraft, StringComparison.OrdinalIgnoreCase)) { best = p.Value; break; }
            // Un trozo, como en los perfiles: "ah-64d" encaja con "AH-64D_BLK_II".
            if (best is null && p.Name.Contains(aircraft, StringComparison.OrdinalIgnoreCase)) best = p.Value;
        }
        return best is { } b ? [.. b.EnumerateArray().Select(e => e.GetString()!)] : [];
    }

    private static DcsControl Parse(string name, string category, JsonElement c)
    {
        var description = c.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var inputs = c.TryGetProperty("inputs", out var ins)
            ? ins.EnumerateArray().Select(i => i.TryGetProperty("interface", out var f) ? f.GetString() ?? "" : "").ToArray()
            : [];
        var positions = c.TryGetProperty("positions", out var pos) && pos.ValueKind == JsonValueKind.Array
            ? pos.EnumerateArray().Select(p => p.GetString() ?? "").ToArray()
            : [];
        if (c.TryGetProperty("outputs", out var outs))
            foreach (var o in outs.EnumerateArray())
                if (o.TryGetProperty("type", out var t) && t.GetString() == "integer")
                    return new DcsControl(name, category, description, inputs, o.GetProperty("address").GetInt32(),
                        o.GetProperty("mask").GetInt32(), o.GetProperty("shift_by").GetInt32(),
                        o.TryGetProperty("max_value", out var mv) ? mv.GetInt32() : 0, positions);
        return new DcsControl(name, category, description, inputs, null, 0, 0, 0, positions);
    }
}
