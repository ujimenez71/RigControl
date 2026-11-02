using System.Text.Json;

namespace RigControl.Core;

public sealed class CatalogEntry
{
    public required string Key { get; set; }
    public required string Alias { get; set; }
    public List<string> Tags { get; set; } = [];
    public required string ProductName { get; set; }
    public string Vid { get; set; } = "";
    public string Pid { get; set; } = "";
    public string? Serial { get; set; }
    public DateTime LastSeenUtc { get; set; }

    /// <summary>
    /// Nombres de las entradas para mostrar: "b13" -> "ALT ON", "hat1" (o "hat1.up"), ejes "x".."s1".
    /// Editable a mano; "catalog" solo los rellena si faltan.
    /// </summary>
    public Dictionary<string, string>? Inputs { get; set; }

    /// <summary>
    /// Nombre de una entrada ("b13", "hat1.up", "y>95") o null si no tiene. Hats: "hat1.up" o
    /// "hat1" + flecha. Zonas de eje: nombre del eje + la zona ("Palanca 1 >95 %").
    /// </summary>
    public string? InputLabel(string part)
    {
        if (Inputs is null) return null;
        part = part.ToLowerInvariant();
        if (Inputs.TryGetValue(part, out var exact)) return exact;

        var dot = part.IndexOf('.');
        if (part.StartsWith("hat") && dot > 0 && Inputs.TryGetValue(part[..dot], out var hat))
        {
            var arrow = part[(dot + 1)..] switch { "up" => "↑", "right" => "→", "down" => "↓", "left" => "←", var x => x };
            return $"{hat} {arrow}";
        }

        var op = part.IndexOfAny(['<', '>', '=']);
        if (op > 0 && Inputs.TryGetValue(part[..op], out var axis))
            return $"{axis} {part[op..]} %";
        return null;
    }
}

/// <summary>
/// devices.json: nombres legibles y etiquetas de uso (jet / helo / ga) por dispositivo.
/// Los bindings de la fase 2 se referirán a la clave estable, nunca al InstanceGuid.
/// El archivo es editable a mano: los alias y etiquetas se conservan al volver a escanear.
/// </summary>
public sealed class DeviceCatalog
{
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RigControl", "devices.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Sin escapar "·", flechas ni acentos: el archivo se edita a mano.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // Valores iniciales para tu equipo, por VID:PID. Solo se aplican a dispositivos nuevos.
    private static readonly Dictionary<string, (string Alias, string[] Tags)> Defaults = new()
    {
        ["346E:1000"] = ("Moza AB9 + MH16", ["jet", "helo"]),
        ["4098:BD64"] = ("Throttle F-15EX", ["jet"]),
        ["4098:BEF0"] = ("Pedales WinWing", ["jet", "helo", "ga"]),
        ["4098:BEE1"] = ("MFD izquierdo", ["jet"]),
        ["4098:BEE2"] = ("MFD derecho", ["jet"]),
        ["4098:BF05"] = ("PTO 2", ["jet"]),
        ["4098:BF06"] = ("ICP", ["jet"]),
        ["3344:832E"] = ("Colectivo AH-64", ["helo"]),
        ["294B:1900"] = ("Yoke Alpha", ["ga"]),
        ["294B:1901"] = ("Bravo", ["ga"]),
    };

    // Nombres de entradas por VID:PID (mapeo verificado con "monitor"). Se copian al catálogo
    // cuando un dispositivo no tiene ninguno; después manda lo que haya en devices.json.
    private static readonly Dictionary<string, Dictionary<string, string>> DefaultInputs = new()
    {
        ["294B:1901"] = new()
        {
            ["b1"] = "HDG", ["b2"] = "NAV", ["b3"] = "APR", ["b4"] = "REV", ["b5"] = "ALT", ["b6"] = "VS",
            ["b7"] = "IAS", ["b8"] = "AP", ["b22"] = "Trim morro abajo", ["b23"] = "Trim morro arriba",
            ["b24"] = "Palanca 1 bajo el tope", ["b25"] = "Palanca 2 bajo el tope", ["b26"] = "Palanca 3 bajo el tope",
            ["b27"] = "Palanca 4 bajo el tope", ["b28"] = "Palanca 5 bajo el tope", ["b33"] = "Palanca 6 bajo el tope",
            ["b31"] = "Tren arriba", ["b32"] = "Tren abajo",
            ["y"] = "Palanca 1", ["x"] = "Palanca 2", ["rz"] = "Palanca 3", ["ry"] = "Palanca 4", ["rx"] = "Palanca 5",
            ["z"] = "Palanca 6",
        },
        ["294B:1900"] = new()
        {
            ["b1"] = "Empuñ. izq. · botón 1", ["b2"] = "Empuñ. izq. · botón 2",
            ["b3"] = "Empuñ. der. · botón 1", ["b4"] = "Empuñ. der. · botón 2",
            ["b5"] = "Empuñ. izq. · basculante izq. ↓", ["b6"] = "Empuñ. izq. · basculante izq. ↑",
            ["b7"] = "Empuñ. izq. · basculante der. ↓", ["b8"] = "Empuñ. izq. · basculante der. ↑",
            ["b9"] = "Empuñ. der. · basculante izq. ←", ["b10"] = "Empuñ. der. · basculante izq. →",
            ["b11"] = "Empuñ. der. · basculante der. ←", ["b12"] = "Empuñ. der. · basculante der. →",
            ["b13"] = "ALT ON", ["b14"] = "ALT OFF", ["b15"] = "BAT ON", ["b16"] = "BAT OFF",
            ["b17"] = "BUS1 ON", ["b18"] = "BUS1 OFF", ["b19"] = "BUS2 ON", ["b20"] = "BUS2 OFF",
            ["b21"] = "BCN ON", ["b22"] = "BCN OFF", ["b23"] = "LAND ON", ["b24"] = "LAND OFF",
            ["b25"] = "TAXI ON", ["b26"] = "TAXI OFF", ["b27"] = "NAV ON", ["b28"] = "NAV OFF",
            ["b29"] = "STROBE ON", ["b30"] = "STROBE OFF",
            ["b31"] = "Magnetos OFF", ["b32"] = "Magnetos R", ["b33"] = "Magnetos L", ["b34"] = "Magnetos BOTH",
            ["b35"] = "Magnetos START", ["hat1"] = "Empuñ. izq. · hat",
        },
    };

    public List<CatalogEntry> Devices { get; set; } = [];

    public static DeviceCatalog Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new DeviceCatalog();
        var catalog = JsonSerializer.Deserialize<DeviceCatalog>(File.ReadAllText(path), JsonOptions) ?? new DeviceCatalog();
        // Dispositivos sin nombres de entradas: los de fábrica (aunque no estén conectados ahora).
        foreach (var e in catalog.Devices)
            if (e.Inputs is null && int.TryParse(e.Vid, System.Globalization.NumberStyles.HexNumber, null, out var vid)
                                 && int.TryParse(e.Pid, System.Globalization.NumberStyles.HexNumber, null, out var pid))
                e.Inputs = DefaultInputsFor(vid, pid);
        return catalog;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public CatalogEntry? Find(string key) => Devices.FirstOrDefault(d => d.Key == key);

    /// <summary>Añade dispositivos nuevos y actualiza los conocidos sin tocar alias ni etiquetas.</summary>
    public (int Added, int Updated) Merge(IEnumerable<ScannedDevice> scanned)
    {
        int added = 0, updated = 0;
        var now = DateTime.UtcNow;

        foreach (var s in scanned)
        {
            var existing = Find(s.Key);
            if (existing is not null)
            {
                existing.ProductName = s.ProductName;
                existing.LastSeenUtc = now;
                existing.Inputs ??= DefaultInputsFor(s.VendorId, s.ProductId);
                updated++;
                continue;
            }

            var vidPid = $"{s.VendorId:X4}:{s.ProductId:X4}";
            var (alias, tags) = Defaults.TryGetValue(vidPid, out var d) ? d : (s.ProductName, Array.Empty<string>());

            Devices.Add(new CatalogEntry
            {
                Key = s.Key,
                Alias = alias,
                Tags = [.. tags],
                ProductName = s.ProductName,
                Vid = $"{s.VendorId:X4}",
                Pid = $"{s.ProductId:X4}",
                Serial = s.Serial,
                LastSeenUtc = now,
                Inputs = DefaultInputsFor(s.VendorId, s.ProductId),
            });
            added++;
        }

        return (added, updated);
    }

    private static Dictionary<string, string>? DefaultInputsFor(int vid, int pid) =>
        DefaultInputs.TryGetValue($"{vid:X4}:{pid:X4}", out var d) ? new Dictionary<string, string>(d) : null;
}
