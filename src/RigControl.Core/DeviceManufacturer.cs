using HidSharp;

namespace RigControl.Core;

/// <summary>
/// Fabricante de un mando a partir de su VID: primero una tabla con los del rig (nombres cortos, como se
/// conocen), si no, el que declara el propio USB (cadena "Manufacturer" del HID).
/// </summary>
public static class DeviceManufacturer
{
    private static readonly Dictionary<int, string> Known = new()
    {
        [0x346E] = "MOZA",
        [0x4098] = "WinWing",
        [0x3344] = "Virpil",
        [0x294B] = "Honeycomb",
        [0x044F] = "Thrustmaster",
        [0x06A3] = "Logitech",
        [0x046D] = "Logitech",
        [0x231D] = "VKB",
        [0x0738] = "Mad Catz",
    };

    private static readonly Dictionary<(int, int), string?> Cache = [];

    /// <summary>Nombre del fabricante, o null si no se sabe.</summary>
    public static string? For(int vid, int pid)
    {
        if (Known.TryGetValue(vid, out var name)) return name;
        lock (Cache)
        {
            if (Cache.TryGetValue((vid, pid), out var cached)) return cached;
            string? fromUsb = null;
            try
            {
                fromUsb = DeviceList.Local.GetHidDevices(vid, pid).Select(d => TryManufacturer(d)).FirstOrDefault(m => m is { Length: > 0 });
            }
            catch (Exception) { } // sin permiso o desconectado: sin fabricante
            Cache[(vid, pid)] = fromUsb;
            return fromUsb;
        }
    }

    /// <summary>A partir de la clave "VID:PID[:serie]".</summary>
    public static string? ForKey(string deviceKey)
    {
        var parts = deviceKey.Split(':');
        return parts.Length >= 2 && int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var vid)
                                 && int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var pid)
            ? For(vid, pid) : null;
    }

    /// <summary>"WinWing" + "Throttle F-15EX" -> "WinWing Throttle F-15EX"; sin repetirlo si el nombre ya lo lleva.</summary>
    public static string WithName(string? manufacturer, string name) =>
        manufacturer is null || name.Contains(manufacturer, StringComparison.OrdinalIgnoreCase) ? name : $"{manufacturer} {name}";

    private static string? TryManufacturer(HidDevice d)
    {
        try { return d.GetManufacturer()?.Trim(); }
        catch (Exception) { return null; }
    }
}
