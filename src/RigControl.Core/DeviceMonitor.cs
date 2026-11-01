using Vortice.DirectInput;

namespace RigControl.Core;

/// <summary>Un dispositivo abierto por DeviceMonitor, con su estado más reciente.</summary>
public sealed class MonitoredDevice
{
    public static readonly string[] AxisNames = ["X", "Y", "Z", "RX", "RY", "RZ", "S0", "S1"];

    public required string Key { get; init; }

    /// <summary>Alias del catálogo, o el nombre del producto si no está catalogado.</summary>
    public required string Name { get; init; }
    public required string ProductName { get; init; }

    /// <summary>Ejes que existen en el dispositivo (índices 0..7: X, Y, Z, RX, RY, RZ, S0, S1).</summary>
    public required int[] AxisIndices { get; init; }
    public required int ButtonCount { get; init; }
    public required int PovCount { get; init; }

    /// <summary>Estado actual (0..65535 por eje; centésimas de grado en los hats, -1 = centrado).</summary>
    public int[] Axes { get; } = new int[8];
    public bool[] Buttons { get; init; } = [];
    public int[] Povs { get; init; } = [];

    /// <summary>False si la última lectura falló (desconectado o sin adquirir).</summary>
    public bool Ok { get; internal set; }

    internal IDirectInputDevice8 Device { get; init; } = null!;
    internal JoystickState State = new();
}

/// <summary>
/// Abre todos los mandos en modo compartido y lee su estado completo: base del visor de entradas
/// (la consola "monitor" y la pantalla Monitor de la app). Poll no asigna memoria.
/// </summary>
public sealed class DeviceMonitor : IDisposable
{
    private readonly List<MonitoredDevice> _devices = [];

    public IReadOnlyList<MonitoredDevice> Devices => _devices;

    /// <summary>Errores al abrir (dispositivo y mensaje), para mostrarlos.</summary>
    public List<string> OpenErrors { get; } = [];

    /// <param name="window">Ventana para el nivel cooperativo de DirectInput (la de la app).</param>
    public static DeviceMonitor Open(IDirectInput8 di, IntPtr window)
    {
        var monitor = new DeviceMonitor();
        var catalog = DeviceCatalog.Load();

        foreach (var scanned in DeviceScanner.Scan(di).OrderBy(s => catalog.Find(s.Key)?.Alias ?? s.ProductName))
        {
            var name = catalog.Find(scanned.Key)?.Alias ?? scanned.ProductName;
            try
            {
                var dev = DeviceScanner.OpenShared(di, scanned.Instance.InstanceGuid, window);
                var buttons = dev.GetObjects(DeviceObjectTypeFlags.Button).Count;
                var povs = dev.GetObjects(DeviceObjectTypeFlags.PointOfViewController).Count;

                monitor._devices.Add(new MonitoredDevice
                {
                    Key = scanned.Key,
                    Name = name,
                    ProductName = scanned.ProductName,
                    AxisIndices = PresentAxes(dev),
                    ButtonCount = buttons,
                    PovCount = povs,
                    Buttons = new bool[buttons],
                    Povs = new int[povs],
                    Device = dev,
                });
            }
            catch (Exception ex)
            {
                monitor.OpenErrors.Add($"{name}: {ex.Message}");
            }
        }

        return monitor;
    }

    /// <summary>Lee el estado de todos los dispositivos.</summary>
    public void Poll()
    {
        foreach (var d in _devices)
        {
            try
            {
                d.Device.Poll();
                d.Device.GetCurrentJoystickState(ref d.State);
            }
            catch
            {
                // Desconectado o se perdió la adquisición: reintentar en el siguiente ciclo.
                d.Ok = false;
                try { d.Device.Acquire(); } catch { }
                continue;
            }

            d.Ok = true;
            var s = d.State;
            d.Axes[0] = s.X;
            d.Axes[1] = s.Y;
            d.Axes[2] = s.Z;
            d.Axes[3] = s.RotationX;
            d.Axes[4] = s.RotationY;
            d.Axes[5] = s.RotationZ;
            d.Axes[6] = s.Sliders[0];
            d.Axes[7] = s.Sliders[1];

            var buttons = s.Buttons;
            for (var i = 0; i < d.Buttons.Length && i < buttons.Length; i++) d.Buttons[i] = buttons[i];

            var povs = s.PointOfViewControllers;
            for (var i = 0; i < d.Povs.Length && i < povs.Length; i++)
                d.Povs[i] = (povs[i] & 0xFFFF) == 0xFFFF ? -1 : povs[i];
        }
    }

    public void Dispose()
    {
        foreach (var d in _devices)
        {
            try { d.Device.Unacquire(); } catch { }
            d.Device.Dispose();
        }
        _devices.Clear();
    }

    /// <summary>Qué ejes tiene de verdad el dispositivo, según el tipo de cada objeto.</summary>
    private static int[] PresentAxes(IDirectInputDevice8 dev)
    {
        var present = new SortedSet<int>();
        var sliders = 0;
        foreach (var o in dev.GetObjects(DeviceObjectTypeFlags.AbsoluteAxis))
        {
            var t = o.ObjectType;
            if (t == ObjectGuid.XAxis) present.Add(0);
            else if (t == ObjectGuid.YAxis) present.Add(1);
            else if (t == ObjectGuid.ZAxis) present.Add(2);
            else if (t == ObjectGuid.RxAxis) present.Add(3);
            else if (t == ObjectGuid.RyAxis) present.Add(4);
            else if (t == ObjectGuid.RzAxis) present.Add(5);
            else if (t == ObjectGuid.Slider && sliders < 2) present.Add(6 + sliders++);
        }
        return [.. present];
    }
}
