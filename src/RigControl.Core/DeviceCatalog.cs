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

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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

    public List<CatalogEntry> Devices { get; set; } = [];

    public static DeviceCatalog Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new DeviceCatalog();
        return JsonSerializer.Deserialize<DeviceCatalog>(File.ReadAllText(path), JsonOptions) ?? new DeviceCatalog();
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
            });
            added++;
        }

        return (added, updated);
    }
}
