using System.Runtime.InteropServices;
using HidSharp;
using Vortice.DirectInput;

namespace RigProbe;

/// <summary>Dispositivo detectado: instancia DirectInput + identidad USB + interfaz HID asociada.</summary>
public sealed record ScannedDevice(DeviceInstance Instance, int VendorId, int ProductId, string? Serial, string? HidPath)
{
    public string Key => DeviceKey.Create(VendorId, ProductId, Serial);
    public string ProductName => Instance.ProductName.Trim();
}

/// <summary>
/// Clave estable de dispositivo: VID:PID:serial (o VID:PID si no hay serial).
/// No usamos InstanceGuid porque Windows puede regenerarlo al cambiar de puerto USB.
/// </summary>
public static class DeviceKey
{
    public static string Create(int vid, int pid, string? serial) =>
        string.IsNullOrWhiteSpace(serial) ? $"{vid:X4}:{pid:X4}" : $"{vid:X4}:{pid:X4}:{serial}";
}

public static class DeviceScanner
{
    public static List<ScannedDevice> Scan(IDirectInput8 di)
    {
        var result = new List<ScannedDevice>();

        foreach (var inst in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
        {
            var (vid, pid) = FromProductGuid(inst.ProductGuid);

            // Emparejamos DirectInput con HID por VID:PID. Es inequívoco en tu equipo porque
            // todos los VID:PID son únicos. Si algún día tienes dos dispositivos idénticos,
            // habrá que usar DIPROP_GUIDANDPATH para obtener la ruta exacta.
            var primary = DeviceList.Local.GetHidDevices(vid, pid)
                .OrderBy(h => IsPrimaryInterface(h.DevicePath) ? 0 : 1)
                .FirstOrDefault();

            var serial = primary is null ? null : Safe(() => primary.GetSerialNumber());
            result.Add(new ScannedDevice(inst, vid, pid, serial, primary?.DevicePath));
        }

        return result;
    }

    /// <summary>
    /// Abre un dispositivo en modo no exclusivo y en segundo plano: convive con SimAppPro,
    /// Pit House, VPC y el simulador. Lanza excepción si no se puede adquirir.
    /// </summary>
    public static IDirectInputDevice8 OpenShared(IDirectInput8 di, Guid instanceGuid)
    {
        var dev = di.CreateDevice(instanceGuid);
        dev.SetDataFormat<RawJoystickState>();
        try
        {
            dev.SetCooperativeLevel(GetConsoleWindow(), CooperativeLevel.NonExclusive | CooperativeLevel.Background);
        }
        catch
        {
            // Algunos terminales no exponen una ventana válida; el nivel por defecto suele bastar.
        }
        dev.Acquire();
        return dev;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    /// <summary>El Data1 del ProductGuid de DirectInput es (PID &lt;&lt; 16) | VID.</summary>
    public static (int Vid, int Pid) FromProductGuid(Guid productGuid)
    {
        var data1 = BitConverter.ToUInt32(productGuid.ToByteArray(), 0);
        return ((int)(data1 & 0xFFFF), (int)(data1 >> 16));
    }

    // Sin "&mi_" (interfaz única) o "&mi_00" (primera interfaz de un dispositivo compuesto).
    private static bool IsPrimaryInterface(string path) =>
        !path.Contains("&mi_", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("&mi_00", StringComparison.OrdinalIgnoreCase);

    private static string? Safe(Func<string> f)
    {
        try { return f(); } catch { return null; }
    }
}
