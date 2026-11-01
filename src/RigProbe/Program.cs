using HidSharp;
using RigControl.Core;
using RigControl.Core.Bindings;
using Vortice.DirectInput;

namespace RigProbe;

/// <summary>
/// Fase 1: detección de dispositivos y visor de entradas en vivo.
///   RigProbe list      -> enumera DirectInput + interfaces HID de fabricantes conocidos
///   RigProbe list all  -> igual, pero incluye HID de cualquier fabricante
///   RigProbe monitor   -> muestra en vivo cambios de botones, ejes y hats (ESC para salir)
///   RigProbe check X   -> valida el perfil X y pasa el Conflict Guard
///   RigProbe run X     -> ejecuta el perfil X e imprime las acciones disparadas (ESC para salir);
///                         al guardar el JSON se recarga sin reiniciar
/// </summary>
internal static class Program
{
    // VIDs USB conocidos (heurística para etiquetar; verifícalos con "list").
    private static readonly Dictionary<int, string> Vendors = new()
    {
        [0x4098] = "WinWing",
        [0x294B] = "Honeycomb",
        [0x3344] = "Virpil",
        [0x346E] = "Moza",
    };

    private static readonly string[] AxisNames = ["X", "Y", "Z", "RX", "RY", "RZ", "S0", "S1"];
    private const int AxisThreshold = 1300; // ~2% de 0..65535, filtra ruido

    private static int Main(string[] args)
    {
        var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "list";
        var includeAllHid = args.Skip(1).Any(a => a.Equals("all", StringComparison.OrdinalIgnoreCase));

        using var di = DInput.DirectInput8Create();
        var instances = di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly).ToList();

        switch (mode)
        {
            case "list":
                ListDirectInput(instances);
                ListHid(includeAllHid);
                return 0;
            case "monitor":
                Monitor(di, instances);
                return 0;
            case "catalog":
                Catalog(di);
                return 0;
            case "bravo-test":
                BravoTest();
                return 0;
            case "check" when args.Length > 1:
                return ProfileLoader.LoadAndCheck(ProfileFile.Resolve(args[1])) is null ? 1 : 0;
            case "run" when args.Length > 1:
                return Run(di, args[1], args.Skip(2).Any(a => a.Equals("--dry", StringComparison.OrdinalIgnoreCase)));
            case "sim":
                var listEvents = args.Length > 1 && args[1].Equals("eventos", StringComparison.OrdinalIgnoreCase);
                SimStatus(listEvents, listEvents && args.Length > 2 ? args[2] : null);
                return 0;
            default:
                Console.WriteLine("Uso: RigProbe list [all] | monitor | catalog | bravo-test | check <perfil> | run <perfil>|auto [--dry]");
                Console.WriteLine("            | sim | sim eventos [filtro]");
                Console.WriteLine($"  <perfil> es una ruta a un .json o un nombre en {ProfileFile.ProfilesDir}");
                return 1;
        }
    }

    // ----------------------------------------------------------------- run

    /// <summary>"run perfil": un archivo fijo. "run auto": la carpeta de perfiles, según el avión de MSFS.</summary>
    private static int Run(IDirectInput8 di, string target, bool dryRun)
    {
        using var sim = new SimConnectOutput();
        ProfileSource source;

        if (target.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var dir = ProfileFile.ProfilesDir;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                Console.WriteLine($"Creada la carpeta de perfiles {dir} (está vacía: copia ahí tus .json).");
            }
            source = new AutoProfileSource(dir, sim);
        }
        else
        {
            var path = ProfileFile.Resolve(target);
            var profile = ProfileLoader.LoadAndCheck(path);
            if (profile is null) return 1;
            source = new FileProfileSource(path, profile);
        }

        if (dryRun) Console.WriteLine("Modo --dry: las acciones solo se imprimen, no se envía nada.");
        Console.WriteLine("Pulsa ESC para salir.");

        // El runner (en Core) no lee la consola: aquí se vigila ESC y se le pide parar.
        using var stop = new CancellationTokenSource();
        var escWatcher = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (ConsoleInput.EscapePressed()) stop.Cancel();
                Thread.Sleep(50);
            }
        }) { IsBackground = true };
        escWatcher.Start();

        using (source)
        using (var keyboard = new KeyboardOutput())
        using (var dispatcher = new ActionDispatcher(keyboard, sim, dryRun))
            BindingRunner.Run(di, source, dispatcher, stop.Token);
        stop.Cancel();
        return 0;
    }

    // ----------------------------------------------------------------- sim

    /// <summary>Conecta con MSFS, muestra el avión y, si se pide, sus InputEvents (B:) filtrados.</summary>
    private static void SimStatus(bool listEvents, string? filter)
    {
        using var sim = new SimConnectOutput();
        IReadOnlyDictionary<string, ulong>? events = null;
        sim.InputEventsReady += e => events = e;

        Console.WriteLine("Conectando con MSFS (máx. 15 s)...");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 15000 && events is null)
        {
            sim.Tick(clock.ElapsedMilliseconds);
            Thread.Sleep(50);
        }

        if (!sim.IsConnected)
        {
            Console.WriteLine("No se pudo conectar. ¿Está MSFS abierto (y SimConnect.dll junto al ejecutable)?");
            return;
        }
        if (events is null)
        {
            Console.WriteLine("Conectado, pero sin lista de InputEvents (¿estás ya en un vuelo, no en el menú?).");
            return;
        }
        if (!listEvents) return;

        var names = events.Keys
            .Where(n => filter is null || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"\n{names.Count} InputEvent(s){(filter is null ? "" : $" que contienen \"{filter}\"")}:");
        foreach (var n in names) Console.WriteLine($"  B:{n}");
    }

    // ------------------------------------------------------------- catalog

    private static void Catalog(IDirectInput8 di)
    {
        var scanned = DeviceScanner.Scan(di);
        var catalog = DeviceCatalog.Load();
        var (added, updated) = catalog.Merge(scanned);
        catalog.Save();

        Console.WriteLine($"Catálogo: {added} nuevo(s), {updated} actualizado(s)");
        Console.WriteLine($"Archivo: {DeviceCatalog.DefaultPath}\n");

        foreach (var e in catalog.Devices.OrderBy(e => e.Alias))
        {
            var online = scanned.Any(s => s.Key == e.Key) ? "conectado" : "DESCONECTADO";
            Console.WriteLine($"- {e.Alias,-18} [{string.Join(",", e.Tags)}] {online}");
            Console.WriteLine($"    {e.Key}");
        }
    }

    // ---------------------------------------------------------- bravo-test

    private static void BravoTest()
    {
        using var bravo = BravoLights.TryOpen();
        if (bravo is null)
        {
            Console.WriteLine("No se encontró el Bravo o no se pudo abrir su interfaz de luces.");
            return;
        }

        Console.WriteLine("Encendiendo cada luz por separado. Comprueba que el nombre coincide con la luz física.\n");
        foreach (var led in Enum.GetValues<BravoLed>().Where(l => l != BravoLed.None))
        {
            Console.WriteLine($"  {led}");
            bravo.Set(led);
            Thread.Sleep(700);
        }

        Console.WriteLine("\nTodas encendidas 2 s...");
        bravo.Set(Enum.GetValues<BravoLed>().Aggregate(BravoLed.None, (acc, l) => acc | l));
        Thread.Sleep(2000);
        Console.WriteLine("Apagando.");
    }

    /// <summary>InstanceGuid -> alias del catálogo, para mostrar nombres legibles en monitor.</summary>
    private static Dictionary<Guid, string> AliasMap(IDirectInput8 di)
    {
        var catalog = DeviceCatalog.Load();
        return DeviceScanner.Scan(di)
            .Select(s => (s.Instance.InstanceGuid, Entry: catalog.Find(s.Key)))
            .Where(x => x.Entry is not null)
            .ToDictionary(x => x.InstanceGuid, x => x.Entry!.Alias);
    }

    // ---------------------------------------------------------------- list

    private static void ListDirectInput(List<DeviceInstance> instances)
    {
        Console.WriteLine($"=== DirectInput: {instances.Count} dispositivo(s) ===");
        foreach (var (inst, label) in Label(instances))
        {
            Console.WriteLine($"- {label}");
            Console.WriteLine($"    InstanceGuid: {inst.InstanceGuid}");
            Console.WriteLine($"    ProductGuid:  {inst.ProductGuid}");
            Console.WriteLine($"    Tipo:         {inst.Type}");
        }
        Console.WriteLine();
    }

    private static void ListHid(bool includeAll)
    {
        var hid = DeviceList.Local.GetHidDevices()
            .Where(d => includeAll || Vendors.ContainsKey(d.VendorID))
            .OrderBy(d => d.VendorID).ThenBy(d => d.ProductID)
            .ToList();

        Console.WriteLine($"=== HID: {hid.Count} interfaz(es) ===");
        Console.WriteLine("(un mismo dispositivo físico puede exponer varias interfaces/colecciones)");
        foreach (var d in hid)
        {
            var vendor = Vendors.GetValueOrDefault(d.VendorID, "Otro");
            var name = Safe(() => d.GetProductName(), "?");
            var serial = Safe(() => d.GetSerialNumber(), "(sin serial)");
            var outLen = Safe(() => d.GetMaxOutputReportLength(), 0);
            var featLen = Safe(() => d.GetMaxFeatureReportLength(), 0);

            Console.WriteLine($"- [{vendor}] {name}  VID:{d.VendorID:X4} PID:{d.ProductID:X4}");
            Console.WriteLine($"    Serial: {serial}");
            Console.WriteLine($"    Output report: {outLen} B   Feature report: {featLen} B" +
                              (outLen > 0 || featLen > 0 ? "   <- candidato para LEDs/pantallas" : ""));
            Console.WriteLine($"    Ruta: {d.DevicePath}");
        }
        Console.WriteLine();
    }

    // ------------------------------------------------------------- monitor

    private sealed class Tracked(string label, IDirectInputDevice8 device)
    {
        public string Label { get; } = label;
        public IDirectInputDevice8 Device { get; } = device;
        public int[]? Axes { get; set; }
        public bool[]? Buttons { get; set; }
        public int[]? Povs { get; set; }
    }

    private static void Monitor(IDirectInput8 di, List<DeviceInstance> instances)
    {
        var tracked = new List<Tracked>();
        var aliases = AliasMap(di);

        foreach (var (inst, rawLabel) in Label(instances))
        {
            var label = aliases.GetValueOrDefault(inst.InstanceGuid, rawLabel);
            try
            {
                var dev = DeviceScanner.OpenShared(di, inst.InstanceGuid);
                tracked.Add(new Tracked(label, dev));
                Console.WriteLine($"Abierto: {label}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"No se pudo abrir {label}: {ex.Message}");
            }
        }

        Console.WriteLine($"\nMonitorizando {tracked.Count} dispositivo(s) a ~60 Hz. Pulsa ESC para salir.\n");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
        while (timer.WaitForNextTickAsync().AsTask().GetAwaiter().GetResult())
        {
            if (ConsoleInput.EscapePressed()) break;
            var warmingUp = clock.ElapsedMilliseconds < BindingRunner.WarmUpMs;

            foreach (var t in tracked)
            {
                if (warmingUp) t.Axes = null; // Report tomará esta lectura como estado base
                JoystickState state;
                try
                {
                    t.Device.Poll();
                    state = t.Device.GetCurrentJoystickState();
                }
                catch
                {
                    // Desconectado o se perdió la adquisición: reintentar en el siguiente ciclo.
                    try { t.Device.Acquire(); } catch { }
                    continue;
                }

                Report(t, state);
            }
        }

        foreach (var t in tracked)
        {
            try { t.Device.Unacquire(); } catch { }
            t.Device.Dispose();
        }
    }

    private static void Report(Tracked t, JoystickState s)
    {
        int[] axes = [s.X, s.Y, s.Z, s.RotationX, s.RotationY, s.RotationZ, s.Sliders[0], s.Sliders[1]];
        var buttons = s.Buttons;
        var povs = s.PointOfViewControllers;
        var time = DateTime.Now.ToString("HH:mm:ss.fff");

        // Primera lectura: solo guardamos el estado base.
        if (t.Axes is null || t.Buttons is null || t.Povs is null)
        {
            t.Axes = axes;
            t.Buttons = (bool[])buttons.Clone();
            t.Povs = (int[])povs.Clone();
            return;
        }

        for (var i = 0; i < buttons.Length; i++)
        {
            if (buttons[i] != t.Buttons[i])
                Console.WriteLine($"{time}  {t.Label}  Botón {i + 1,3} {(buttons[i] ? "PULSADO" : "suelto")}");
        }

        for (var i = 0; i < axes.Length; i++)
        {
            if (Math.Abs(axes[i] - t.Axes[i]) >= AxisThreshold)
            {
                Console.WriteLine($"{time}  {t.Label}  Eje {AxisNames[i],-2} {axes[i],6}  ({axes[i] * 100.0 / 65535,5:0.0} %)");
                t.Axes[i] = axes[i]; // solo actualizamos al superar el umbral para no perder movimientos lentos
            }
        }

        for (var i = 0; i < povs.Length; i++)
        {
            if (povs[i] != t.Povs[i])
            {
                var dir = povs[i] < 0 || povs[i] == 0xFFFF ? "centro" : $"{povs[i] / 100}°";
                Console.WriteLine($"{time}  {t.Label}  Hat {i + 1} {dir}");
            }
        }

        t.Buttons = (bool[])buttons.Clone();
        t.Povs = (int[])povs.Clone();
    }

    // ------------------------------------------------------------- helpers

    /// <summary>Etiqueta dispositivos; los de nombre repetido (p. ej. los 3 MFD) reciben #1, #2, #3.</summary>
    private static IEnumerable<(DeviceInstance Inst, string Label)> Label(List<DeviceInstance> instances)
    {
        var counts = instances.GroupBy(i => i.ProductName.Trim()).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, int>();

        foreach (var inst in instances)
        {
            var name = inst.ProductName.Trim();
            if (counts[name] > 1)
            {
                seen[name] = seen.GetValueOrDefault(name) + 1;
                yield return (inst, $"{name} #{seen[name]}");
            }
            else
            {
                yield return (inst, name);
            }
        }
    }

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }
}
