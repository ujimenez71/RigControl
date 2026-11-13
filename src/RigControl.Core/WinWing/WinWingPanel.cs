using HidSharp;

namespace RigControl.Core.WinWing;

/// <summary>
/// LEDs de un dispositivo WinWing (protocolo de SimAppPro, output report de 14 bytes):
///   02 &lt;módulo lo&gt; &lt;módulo hi&gt; 00 00 03 49 &lt;LED&gt; &lt;valor&gt; 00 00 00 00 00
/// Fuente: driver hid-winwing de Linux (Orion 2, capturado con usbmon de la app del fabricante):
/// módulo 0xBE60 = base Orion 2 (LED 0 = retroiluminación 0-255, 1 = A-A, 2 = A-G, 0/1);
/// 0xBF01 / 0xBF03 = motores de vibración de las empuñaduras F-15 (LED 0 = intensidad).
/// En los demás dispositivos el módulo es, por hipótesis, su propio PID (como en los paneles Airbus).
/// Solo escribe la orden 0x49 (LED); nunca firmware ni nada más.
/// </summary>
public sealed class WinWingPanel : IDisposable
{
    public const int VendorId = 0x4098;
    public const int ReportLength = 14;

    /// <summary>Módulo conocido por PID cuando no coincide con el PID (base Orion 2 con empuñaduras F-15EX).</summary>
    private static readonly Dictionary<int, int> KnownModules = new()
    {
        [0xBD64] = 0xBE60, // Orion Throttle Base II + F-15EX
        [0xBD65] = 0xBE60, // ídem F-15E
        [0xBE68] = 0xBE60, // ídem F-16EX
        [0xBE62] = 0xBE60, // ídem F-18
        // MFD CarrierAce: módulo "MFD1" de la tabla PIDByName de SimAppPro (app.asar); el izquierdo
        // verificado por el usuario el 13/11/2026 (solo tienen retroiluminación, LED 0).
        [0xBEE1] = 0xBE0D,
        [0xBEE2] = 0xBE0D,
    };

    /// <summary>
    /// LEDs identificados con `RigProbe winwing barrido` (número -> nombre). PTO 2 visto por el usuario
    /// el 13/11/2026 (módulo = PID BF05, confirmado): 2 y 3 no encienden nada.
    /// </summary>
    public static readonly Dictionary<int, Dictionary<string, byte>> KnownLeds = new()
    {
        [0xBF05] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["BACKLIGHT"] = 0, ["GEAR_HANDLE"] = 1, ["MASTER_CAUTION"] = 4, ["JETTISON"] = 5,
            ["STA_CTR"] = 6, ["STA_LI"] = 7, ["STA_LO"] = 8, ["STA_RO"] = 9, ["STA_RI"] = 10,
            ["FLAPS"] = 11, ["GEAR_NOSE"] = 12, ["FLAPS_FULL"] = 13, ["GEAR_RIGHT"] = 14,
            ["GEAR_LEFT"] = 15, ["FLAPS_HALF"] = 16, ["HOOK"] = 17,
        },
        // Orion Throttle Base II + F-15EX (módulo BE60): igual que el driver de Linux (13/11/2026).
        [0xBD64] = new(StringComparer.OrdinalIgnoreCase) { ["BACKLIGHT"] = 0, ["A_A"] = 1, ["A_G"] = 2 },
        // MFD CarrierAce (módulo BE0D): solo retroiluminación (13/11/2026).
        [0xBEE1] = new(StringComparer.OrdinalIgnoreCase) { ["BACKLIGHT"] = 0 },
        [0xBEE2] = new(StringComparer.OrdinalIgnoreCase) { ["BACKLIGHT"] = 0 },
        // Pedales Orion metal (módulo = PID): LED 0 = pedal izquierdo, 1 = pedal derecho (13/11/2026).
        [0xBEF0] = new(StringComparer.OrdinalIgnoreCase) { ["LEFT_PEDAL"] = 0, ["RIGHT_PEDAL"] = 1 },
    };

    private readonly HidStream _stream;
    private readonly byte[] _report = new byte[ReportLength];

    public int ProductId { get; }
    public string Name { get; }

    private WinWingPanel(HidStream stream, int productId, string name)
    {
        _stream = stream;
        ProductId = productId;
        Name = name;
    }

    /// <summary>Módulo por defecto de un PID: el conocido o el propio PID.</summary>
    public static int DefaultModule(int productId) => KnownModules.TryGetValue(productId, out var m) ? m : productId;

    /// <summary>Dispositivos WinWing con output report de 14 bytes (los de este protocolo).</summary>
    public static IEnumerable<(int ProductId, string Name)> List() =>
        DeviceList.Local.GetHidDevices(VendorId)
            .Where(d => SafeOutputLength(d) == ReportLength)
            .Select(d => (d.ProductID, SafeName(d)))
            .DistinctBy(x => x.ProductID);

    public static WinWingPanel? TryOpen(int productId)
    {
        var device = DeviceList.Local.GetHidDevices(VendorId, productId)
            .FirstOrDefault(d => SafeOutputLength(d) == ReportLength);
        // HidSharp abre en modo compartido: SimAppPro sigue funcionando a la vez.
        return device is not null && device.TryOpen(out var stream) ? new WinWingPanel(stream, productId, SafeName(device)) : null;
    }

    /// <summary>Pone un LED (o la retroiluminación) al valor dado en el módulo indicado (por defecto el del PID).</summary>
    public void Set(byte led, byte value, int? module = null)
    {
        var m = module ?? DefaultModule(ProductId);
        Array.Clear(_report);
        _report[0] = 0x02;
        _report[1] = (byte)m;
        _report[2] = (byte)(m >> 8);
        _report[5] = 0x03;
        _report[6] = 0x49;
        _report[7] = led;
        _report[8] = value;
        _stream.Write(_report);
    }

    public void Dispose() => _stream.Dispose();

    private static int SafeOutputLength(HidDevice d)
    {
        try { return d.GetMaxOutputReportLength(); } catch { return 0; }
    }

    private static string SafeName(HidDevice d)
    {
        try { return d.GetProductName(); } catch { return $"PID {d.ProductID:X4}"; }
    }
}
