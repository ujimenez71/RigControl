using HidSharp;

namespace RigControl.Core;

/// <summary>
/// Luces del Honeycomb Bravo. Mapeo de bits según el protocolo documentado por la comunidad:
/// feature report de 5 bytes = [reportId 0][byte1][byte2][byte3][byte4].
/// Verificado contra las luces físicas con `bravo-test` (28/09/2026): las 28 coinciden.
/// </summary>
[Flags]
public enum BravoLed : uint
{
    None = 0,

    // Byte 1: autopiloto
    Hdg = 1u << 0, Nav = 1u << 1, Apr = 1u << 2, Rev = 1u << 3,
    Alt = 1u << 4, Vs = 1u << 5, Ias = 1u << 6, Autopilot = 1u << 7,

    // Byte 2: tren y avisos principales
    GearLeftGreen = 1u << 8, GearLeftRed = 1u << 9,
    GearNoseGreen = 1u << 10, GearNoseRed = 1u << 11,
    GearRightGreen = 1u << 12, GearRightRed = 1u << 13,
    MasterWarning = 1u << 14, EngineFire = 1u << 15,

    // Byte 3: anunciadores
    LowOilPressure = 1u << 16, LowFuelPressure = 1u << 17, AntiIce = 1u << 18, StarterEngaged = 1u << 19,
    Apu = 1u << 20, MasterCaution = 1u << 21, Vacuum = 1u << 22, LowHydPressure = 1u << 23,

    // Byte 4: anunciadores
    AuxFuelPump = 1u << 24, ParkingBrake = 1u << 25, LowVolts = 1u << 26, Door = 1u << 27,
}

public sealed class BravoLights : IDisposable
{
    public const int VendorId = 0x294B;
    public const int ProductId = 0x1901;

    private readonly HidStream _stream;

    private BravoLights(HidStream stream) => _stream = stream;

    public static BravoLights? TryOpen()
    {
        // Elegimos la interfaz que expone feature reports (la de las luces).
        var device = DeviceList.Local.GetHidDevices(VendorId, ProductId)
            .FirstOrDefault(d => SafeLen(d) >= 5);

        return device is not null && device.TryOpen(out var stream) ? new BravoLights(stream) : null;
    }

    public void Set(BravoLed leds)
    {
        var v = (uint)leds;
        byte[] report = [0x00, (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
        _stream.SetFeature(report);
    }

    public void Dispose()
    {
        try { Set(BravoLed.None); } catch { }
        _stream.Dispose();
    }

    private static int SafeLen(HidDevice d)
    {
        try { return d.GetMaxFeatureReportLength(); } catch { return 0; }
    }
}
