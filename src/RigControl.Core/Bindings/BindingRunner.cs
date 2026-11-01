using System.Diagnostics;
using Vortice.DirectInput;

namespace RigControl.Core.Bindings;

/// <summary>
/// Conecta el motor con los dispositivos reales: abre por clave (nunca por InstanceGuid),
/// sondea a ~60 Hz y traduce botones, hats y zonas de eje a cambios de entrada. ESC para salir.
/// El perfil lo da un ProfileSource: un archivo con recarga en caliente, o la carpeta según el avión.
/// </summary>
public static class BindingRunner
{
    /// <summary>Dispositivo abierto. Se reutiliza entre perfiles mientras alguno lo use.</summary>
    private sealed class OpenDevice(string name, IDirectInputDevice8 device, long openedAt)
    {
        public string Name { get; } = name;
        public IDirectInputDevice8 Device { get; } = device;

        /// <summary>Para el calentamiento: las primeras lecturas tras abrir vienen a cero.</summary>
        public long OpenedAt { get; } = openedAt;

        public JoystickState State = new();
    }

    /// <summary>Un dispositivo y los huecos del motor que le corresponden en el perfil activo.</summary>
    private sealed class DeviceSlots(OpenDevice device, int[] slots)
    {
        public OpenDevice Device { get; } = device;
        public int[] Slots { get; } = slots;

        /// <summary>La próxima lectura solo fija el estado base (perfil recién cargado).</summary>
        public bool NeedsPrime = true;
    }

    /// <summary>
    /// Tras abrir un dispositivo, sus primeras lecturas vienen a cero y el estado real llega ~500 ms
    /// después. Durante este tiempo las lecturas solo fijan el estado base, sin generar eventos.
    /// </summary>
    public const int WarmUpMs = 1000;

    /// <summary>Bucle principal a ~60 Hz hasta que se cancele <paramref name="stop"/> (ESC en consola, botón en la app).</summary>
    public static void Run(IDirectInput8 di, ProfileSource source, IActionSink sink, CancellationToken stop, IntPtr window = default)
    {
        var clock = Stopwatch.StartNew();
        var open = new Dictionary<string, OpenDevice>(StringComparer.OrdinalIgnoreCase);
        var profile = source.Initial;

        sink.ProfileLoaded(profile);
        var engine = new BindingEngine(profile, sink);
        var devices = Attach(di, profile, open, clock.ElapsedMilliseconds, window);
        PrintActive(profile);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
        while (timer.WaitForNextTickAsync().AsTask().GetAwaiter().GetResult())
        {
            if (stop.IsCancellationRequested) break;
            var now = clock.ElapsedMilliseconds;

            // Recarga del archivo o cambio de avión. El estado actual se toma como base sin disparar nada.
            if (source.Poll(now) is { } next)
            {
                engine.EndHolds(now);
                profile = next;
                sink.ProfileLoaded(profile);
                engine = new BindingEngine(profile, sink);
                devices = Attach(di, profile, open, now, window);
                PrintActive(profile);
            }

            foreach (var d in devices)
            {
                var dev = d.Device;
                try
                {
                    dev.Device.Poll();
                    dev.Device.GetCurrentJoystickState(ref dev.State);
                }
                catch
                {
                    // Desconectado o se perdió la adquisición: reintentar en el siguiente ciclo.
                    try { dev.Device.Acquire(); } catch { }
                    continue;
                }

                var prime = d.NeedsPrime || now - dev.OpenedAt < WarmUpMs;
                d.NeedsPrime = false;
                foreach (var slot in d.Slots)
                {
                    var value = Read(dev.State, profile.Inputs[slot], engine.IsDown(slot), profile.AxisHysteresis);
                    if (prime) engine.Prime(slot, value);
                    else engine.SetInput(slot, value, now);
                }
            }

            engine.Tick(now);
            sink.Tick(now);
        }

        engine.EndHolds(clock.ElapsedMilliseconds);
        foreach (var dev in open.Values) Close(dev);
    }

    /// <summary>
    /// Prepara los dispositivos para un perfil: reutiliza los ya abiertos, abre los nuevos y cierra
    /// los que ya no se usan. Base para el cambio automático de perfil de la fase 3.
    /// </summary>
    private static List<DeviceSlots> Attach(IDirectInput8 di, CompiledProfile profile, Dictionary<string, OpenDevice> open, long now, IntPtr window)
    {
        List<ScannedDevice>? scanned = null;
        var result = new List<DeviceSlots>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var d = 0; d < profile.DeviceKeys.Length; d++)
        {
            var key = profile.DeviceKeys[d];
            var alias = profile.DeviceAliases[d];
            used.Add(key);

            if (!open.TryGetValue(key, out var dev))
            {
                scanned ??= DeviceScanner.Scan(di);
                var found = scanned.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (found is null)
                {
                    Log.Warning($"\"{alias}\" ({key}) no está conectado; sus bindings no funcionarán.");
                    continue;
                }

                try
                {
                    dev = new OpenDevice(found.ProductName, DeviceScanner.OpenShared(di, found.Instance.InstanceGuid, window), now);
                    open[key] = dev;
                    Log.Info($"Abierto: {alias} ({found.ProductName})");
                }
                catch (Exception ex)
                {
                    Log.Error($"No se pudo abrir {alias}: {ex.Message}");
                    continue;
                }
            }

            var device = d;
            int[] slots = [.. Enumerable.Range(0, profile.Inputs.Length).Where(i => profile.Inputs[i].Device == device)];
            result.Add(new DeviceSlots(dev, slots));
        }

        foreach (var key in open.Keys.Where(k => !used.Contains(k)).ToList())
        {
            Log.Info($"Cerrado: {open[key].Name} (el perfil ya no lo usa)");
            Close(open[key]);
            open.Remove(key);
        }

        return result;
    }

    private static void PrintActive(CompiledProfile profile) =>
        Log.Info($"\nPerfil \"{profile.Name}\": {profile.Bindings.Length} binding(s). " +
                          "Se recarga al guardar.\n");

    private static void Close(OpenDevice dev)
    {
        try { dev.Device.Unacquire(); } catch { }
        dev.Device.Dispose();
    }

    /// <param name="current">Estado actual de la entrada en el motor (las zonas de eje lo necesitan para la histéresis).</param>
    private static bool Read(JoystickState s, InputRef input, bool current, int hysteresis)
    {
        if (input.Kind == InputKind.Button)
        {
            var buttons = s.Buttons;
            return input.Index < buttons.Length && buttons[input.Index];
        }

        if (input.Kind == InputKind.AxisZone)
            return AxisZone.Next(current, ReadAxis(s, input.Index), input.Lo, input.Hi, hysteresis);

        var povs = s.PointOfViewControllers;
        if (input.Index >= povs.Length) return false;

        // Centésimas de grado, 0 = arriba, sentido horario; centrado = 0xFFFF en la palabra baja.
        var pov = povs[input.Index];
        if ((pov & 0xFFFF) == 0xFFFF) return false;

        // Cada dirección cubre ±67,5°, así que las diagonales activan las dos direcciones vecinas.
        return input.Kind switch
        {
            InputKind.HatUp => pov >= 29250 || pov <= 6750,
            InputKind.HatRight => pov is >= 2250 and <= 15750,
            InputKind.HatDown => pov is >= 11250 and <= 24750,
            InputKind.HatLeft => pov is >= 20250 and <= 33750,
            _ => false,
        };
    }

    /// <summary>Mismo orden que en monitor: X, Y, Z, RX, RY, RZ, S0, S1.</summary>
    private static int ReadAxis(JoystickState s, int axis) => axis switch
    {
        0 => s.X,
        1 => s.Y,
        2 => s.Z,
        3 => s.RotationX,
        4 => s.RotationY,
        5 => s.RotationZ,
        6 => s.Sliders[0],
        7 => s.Sliders[1],
        _ => 0,
    };
}

/// <summary>Sin salidas reales todavía (fase 3): imprime cada acción disparada.</summary>
public sealed class ConsoleActionSink : IActionSink
{
    public void OnAction(Binding binding, ActionPhase phase, long timeMs) => Print(binding, phase, "");

    public static void Print(Binding binding, ActionPhase phase, string result)
    {
        var what = phase switch
        {
            ActionPhase.Start => "INICIO ",
            ActionPhase.End => "FIN    ",
            _ => "",
        };
        var suffix = result.Length == 0 ? "" : $"  {result}";
        Log.Action($"{what}{binding.Action,-30}  <- {binding}{suffix}");
    }
}
