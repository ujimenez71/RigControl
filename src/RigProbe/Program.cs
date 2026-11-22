using HidSharp;
using RigControl.Core;
using RigControl.Core.Ai;
using RigControl.Core.Sheets;
using RigControl.Core.Bindings;
using RigControl.Core.Hangar;
using RigControl.Core.Mods;
using RigControl.Core.Snapshots;
using RigControl.Core.WinWing;
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
        // Core avisa por Log; en consola se imprime tal cual, con la hora delante de cada acción.
        Log.Sink = e => Console.WriteLine(e.Level == LogLevel.Action ? $"{e.Time:HH:mm:ss.fff}  {e.Text}" : e.Text);

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
                return Run(args[1], args.Skip(2).Any(a => a.Equals("--dry", StringComparison.OrdinalIgnoreCase)));
            case "dcs":
                // dcs [segundos] | dcs enviar "CONTROL [VALOR]"
                if (args.Length > 2 && args[1].Equals("enviar", StringComparison.OrdinalIgnoreCase))
                    return DcsSend(string.Join(' ', args.Skip(2)));
                if (args.Length > 2 && args[1].Equals("crudo", StringComparison.OrdinalIgnoreCase))
                {
                    DcsRaw(Convert.ToInt32(args[2], 16), args.Length > 3 ? int.Parse(args[3]) : 10);
                    return 0;
                }
                if (args.Length > 1 && args[1].Equals("vigilar", StringComparison.OrdinalIgnoreCase))
                {
                    DcsWatchControls(args.Length > 2 ? string.Join(' ', args.Skip(2)) : null);
                    return 0;
                }
                if (args.Length > 2 && args[1].Equals("leer", StringComparison.OrdinalIgnoreCase))
                    return DcsSend(args[2], readOnly: true);
                DcsWatch(args.Length > 1 && int.TryParse(args[1], out var dsecs) ? dsecs : 15);
                return 0;
            case "winwing":
                return WinWingCommand([.. args.Skip(1)]);
            case "ia":
                return AiCommand([.. args.Skip(1)], instances);
            case "hoja" when args.Length > 1:
                return SheetCommand(args[1], args.Any(x => x.Equals("--pdf", StringComparison.OrdinalIgnoreCase)));
            case "graficos" or "gráficos":
                return GraphicsCommand([.. args.Skip(1)]);
            case "instantaneas" or "instantáneas":
                return SnapshotsCommand([.. args.Skip(1)]);
            case "mods":
                return ModsCommand([.. args.Skip(1).Where(a => !a.Equals("--dry", StringComparison.OrdinalIgnoreCase))],
                                   args.Any(a => a.Equals("--dry", StringComparison.OrdinalIgnoreCase)));
            case "ventanas" when args.Length > 1:
                return WindowsCommand(string.Join(' ', args.Skip(1)));
            case "hangar":
                return HangarCommand(args.Skip(1).Where(a => !a.Equals("--dry", StringComparison.OrdinalIgnoreCase)).FirstOrDefault(),
                                     args.Any(a => a.Equals("--dry", StringComparison.OrdinalIgnoreCase)));
            case "il2":
                Il2Listen(args.Length > 1 && int.TryParse(args[1], out var il2Secs) ? il2Secs : 30);
                return 0;
            case "sim" when args.Length > 2 && args[1].Equals("enviar", StringComparison.OrdinalIgnoreCase):
                return SimSend(string.Join(' ', args.Skip(2)));
            case "sim" when args.Length > 2 && args[1].Equals("vars", StringComparison.OrdinalIgnoreCase):
                SimVars(args[2], args.Length > 3 && int.TryParse(args[3], out var secs) ? secs : 10);
                return 0;
            case "sim":
                var listEvents = args.Length > 1 && args[1].Equals("eventos", StringComparison.OrdinalIgnoreCase);
                SimStatus(listEvents, listEvents && args.Length > 2 ? args[2] : null);
                return 0;
            default:
                Console.WriteLine("Uso: RigProbe list [all] | monitor | catalog | bravo-test | check <perfil> | run <perfil>|auto [--dry]");
                Console.WriteLine("            | sim | sim eventos [filtro] | sim vars <perfil> [s] | sim enviar ACCIÓN | il2 [s] | dcs [s] | dcs enviar CONTROL [VALOR] | dcs leer CONTROL");
                Console.WriteLine("            | dcs vigilar [filtro] | hangar [nombre [--dry]] | ventanas <proceso>");
                Console.WriteLine("            | mods | mods <conjunto> [--dry] | mods importar <biblioteca> [--dry]");
                Console.WriteLine("            | instantaneas | instantaneas crear <grupo|todo> [nombre] | instantaneas comparar|restaurar <grupo> <n>");
                Console.WriteLine("            | graficos | graficos actual dcs|msfs [--monitor] | graficos guardar dcs|msfs <nombre> [--monitor] | graficos comparar|aplicar <nombre> [--dry] | graficos medir [perfil] [--monitor] | graficos optimizar | graficos mediciones | graficos vuelos");
                Console.WriteLine("            | hoja <perfil> [--pdf] | ia | ia clave [borrar] | ia contexto <avión|actual> | ia <avión|actual> \"lo que quieres\" [--base <perfil>]");
                Console.WriteLine("            | winwing | winwing luces | winwing led <disp> <n> <valor> [--modulo XXXX] | winwing barrido <disp> [desde] [hasta] [--modulo XXXX] [--valor N]");
                Console.WriteLine($"  <perfil> es una ruta a un .json o un nombre en {ProfileFile.ProfilesDir}");
                return 1;
        }
    }

    // ----------------------------------------------------------------- graficos

    /// <summary>
    /// "graficos": perfiles gráficos de DCS y MSFS (graficos.json). "actual" = ajustes de ahora + revisión para VR;
    /// "guardar" = lo actual como perfil; "comparar" = qué cambiaría; "aplicar" = cambiarlo (simulador cerrado, con .bak).
    /// </summary>
    private static int GraphicsCommand(string[] args)
    {
        var monitor = args.Any(a => a.Equals("--monitor", StringComparison.OrdinalIgnoreCase));
        var dry = args.Any(a => a.Equals("--dry", StringComparison.OrdinalIgnoreCase));
        args = [.. args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))];
        var manager = new RigControl.Core.Graphics.GraphicsManager();
        RigControl.Core.Graphics.GraphicsFile file;
        try
        {
            file = RigControl.Core.Graphics.GraphicsFile.Load();
        }
        catch (System.Text.Json.JsonException ex)
        {
            Console.WriteLine($"{RigControl.Core.Graphics.GraphicsFile.DefaultPath} está roto: {ex.Message}");
            return 1;
        }
        static string? Sim(string s) => s.ToLowerInvariant() switch { "dcs" => "dcs", "msfs" => "msfs", _ => null };

        try
        {
            if (args.Length == 0)
            {
                Console.WriteLine($"Visor: {file.Headset.Name}, {file.Headset.RefreshHz} Hz, seguimiento ocular: {(file.Headset.EyeTracking ? "sí" : "no")}");
                Console.WriteLine($"DCS:  {manager.DcsConfigDir ?? "(no encontrado)"}");
                Console.WriteLine($"MSFS: {manager.MsfsUserCfgPath ?? "(no encontrado)"}");
                Console.WriteLine($"Perfiles ({RigControl.Core.Graphics.GraphicsFile.DefaultPath}):");
                foreach (var p in file.Profiles)
                    Console.WriteLine($"  {p.Name,-28} {p.SimName,-10} {(p.Vr ? "VR" : "monitor"),-8} {p.Settings.Count} ajustes · {manager.Diff(p).Count} distintos de lo actual");
                if (file.Profiles.Count == 0) Console.WriteLine("  (ninguno: \"graficos guardar dcs <nombre>\" guarda lo actual)");
                return 0;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "actual" when args.Length > 1 && Sim(args[1]) is { } sim:
                {
                    var current = manager.Current(sim, !monitor);
                    foreach (var (k, v) in current) Console.WriteLine($"  {k,-45} {RigControl.Core.Graphics.LuaOptionsFile.Display(v)}");
                    Console.WriteLine($"{current.Count} ajustes.");
                    if (!monitor) PrintAdvice(RigControl.Core.Graphics.VrAdvisor.Check(sim, current, file.Headset));
                    return 0;
                }
                case "guardar" when args.Length > 2 && Sim(args[1]) is { } sim:
                {
                    var name = string.Join(' ', args.Skip(2));
                    var profile = manager.Capture(sim, name, !monitor);
                    file.Profiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    file.Profiles.Add(profile);
                    file.Save();
                    Console.WriteLine($"Guardado «{name}» ({profile.SimName}, {(profile.Vr ? "VR" : "monitor")}, {profile.Settings.Count} ajustes).");
                    return 0;
                }
                case "medir":
                {
                    RigControl.Core.Graphics.GraphicsProfile? profile = null;
                    if (args.Length > 1)
                    {
                        var name = string.Join(' ', args.Skip(1));
                        if (file.Find(name) is not { } found) { Console.WriteLine($"No existe el perfil «{name}»."); return 1; }
                        if (found.Sim != "dcs") { Console.WriteLine("Por ahora solo se mide DCS (MSFS no se puede arrancar en un vuelo sin intervenir)."); return 1; }
                        profile = found;
                    }
                    var exe = RigControl.Core.Graphics.DcsBenchmark.FindDcsExe();
                    var saved = RigControl.Core.Graphics.DcsBenchmark.FindSavedGames();
                    if (exe is null || saved is null) { Console.WriteLine($"No se encuentra DCS.exe ({exe ?? "?"}) o su carpeta de Saved Games ({saved ?? "?"})."); return 1; }
                    var vr = !monitor;
                    Console.WriteLine($"Medición de DCS {(vr ? $"en VR ({file.Headset.Name}, {file.Headset.RefreshHz} Hz)" : "en el monitor")} con " +
                                      (profile is null ? "los ajustes actuales" : $"el perfil «{profile.Name}»") + ".");
                    Console.WriteLine($"  DCS: {exe}\n  Saved Games: {saved}");
                    Console.WriteLine("  Se abre DCS solo, vuela una misión de prueba (3 escenas, ~25 s cada una) y se cierra. Unos 5 minutos.");
                    if (vr) Console.WriteLine("  El visor tiene que estar encendido y DESPIERTO (sensor de proximidad tapado o puesto, sin moverte).");
                    Console.WriteLine("  Tu options.lua y autoexec.cfg quedan como estaban. Ctrl+C cancela (cierra DCS y restaura).");
                    using var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                    var bench = new RigControl.Core.Graphics.DcsBenchmark(saved, exe);
                    var progress = new Progress<string>(m => Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {m}"));
                    try
                    {
                        var r = bench.Measure(profile, vr, file.Headset.RefreshHz, progress, cts.Token);
                        Console.WriteLine();
                        foreach (var s in r.Scenes) Console.WriteLine($"  {s.Name,-10} {s.Stats}");
                        Console.WriteLine($"  {"TOTAL",-10} {r.Total}");
                        if (vr) Console.WriteLine($"  («fuera de tiempo» = fotogramas que tardan más de {1000.0 / file.Headset.RefreshHz * 1.1:0.0} ms: no llegan a {file.Headset.RefreshHz} Hz)");
                        Console.WriteLine($"Guardada en {RigControl.Core.Graphics.DcsBenchmark.ResultsDir} ({r.Seconds} s).");
                        return 0;
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("Cancelado: DCS cerrado y configuración restaurada.");
                        return 1;
                    }
                    catch (TimeoutException ex)
                    {
                        Console.WriteLine(ex.Message);
                        return 1;
                    }
                }
                case "optimizar":
                {
                    var exe = RigControl.Core.Graphics.DcsBenchmark.FindDcsExe();
                    var saved = RigControl.Core.Graphics.DcsBenchmark.FindSavedGames();
                    if (exe is null || saved is null) { Console.WriteLine($"No se encuentra DCS.exe ({exe ?? "?"}) o su carpeta de Saved Games ({saved ?? "?"})."); return 1; }
                    Console.WriteLine("Optimizar en VR: DCS se abre una o dos veces y prueba a subir o bajar cada ajuste. Unos 15-20 minutos.");
                    Console.WriteLine("  Visor encendido y DESPIERTO. Tu configuración queda como estaba; el resultado es un perfil nuevo SIN aplicar. Ctrl+C cancela.");
                    using var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                    var progress = new Progress<string>(m => Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {m}"));
                    try
                    {
                        var report = new RigControl.Core.Graphics.VrOptimizer(new RigControl.Core.Graphics.DcsBenchmark(saved, exe), file.Headset).Run(progress, cts.Token);
                        Console.WriteLine();
                        foreach (var t in report.Tests)
                            Console.WriteLine($"  {(t.Holds ? "✔" : "✘")} {t.Description,-60} {t.Result.Total.AvgFps,5:0.0} FPS · {t.Result.Total.MissedPercent,4:0.0} % fuera de tiempo");
                        Console.WriteLine();
                        Console.WriteLine(RigControl.Core.Graphics.VrOptimizer.Describe(report));
                        if (report.Profile is { } profile)
                        {
                            file.Profiles.Add(profile);
                            file.Save();
                            Console.WriteLine($"Guardado como perfil «{profile.Name}» (sin aplicar): \"graficos comparar\" / \"graficos aplicar\" con ese nombre.");
                        }
                        else Console.WriteLine("Sin cambios que recomendar: no se guarda ningún perfil.");
                        return 0;
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("Cancelado: DCS cerrado y configuración restaurada.");
                        return 1;
                    }
                    catch (TimeoutException ex)
                    {
                        Console.WriteLine(ex.Message);
                        return 1;
                    }
                }
                case "mediciones":
                {
                    var results = RigControl.Core.Graphics.DcsBenchmark.LoadResults();
                    if (results.Count == 0) { Console.WriteLine("Aún no hay mediciones (\"graficos medir\")."); return 0; }
                    foreach (var r in results)
                        Console.WriteLine($"  {r.When:dd/MM HH:mm}  {(r.Vr ? $"VR {r.RefreshHz} Hz" : "monitor"),-9} {r.Profile,-24} {r.Total}");
                    return 0;
                }
                case "vuelos":
                {
                    // Sesiones del visor según Pimax (también vuelos normales, sin instalar nada), con historial propio.
                    var sessions = RigControl.Core.Graphics.VrSessions.Refresh();
                    if (sessions.Count == 0) { Console.WriteLine("No hay sesiones de Pimax (¿registro de PiService en otra carpeta?)."); return 0; }
                    Console.WriteLine("  Inicio       Juego       Dur.   Hz  SS  Pimax    Mediana  5 % bajo  <50 FPS  GPU   Veredicto / misión");
                    foreach (var s in sessions)
                        Console.WriteLine($"  {s.Start:dd/MM HH:mm}  {s.Game,-10} {s.Seconds / 60,3} min {s.TargetHz,3}  {(s.SmartSmoothing ? "sí" : "no"),-3} {s.PimaxRendering ?? "?",-7} {s.MedianFps,6:0.0} {s.P5Fps,8:0.0} {s.Below50 * 100,7:0} % {s.AvgGpu,4:0} %  " +
                                          $"{s.Verdict}{(s.Mission is null ? "" : $" · {s.Mission}")}");
                    Console.WriteLine($"Historial en {RigControl.Core.Graphics.VrSessions.HistoryPath}");
                    return 0;
                }
                case "comparar" or "aplicar" when args.Length > 1:
                {
                    var name = string.Join(' ', args.Skip(1));
                    if (file.Find(name) is not { } profile) { Console.WriteLine($"No existe el perfil «{name}»."); return 1; }
                    var apply = args[0].Equals("aplicar", StringComparison.OrdinalIgnoreCase);
                    var changes = apply ? manager.Apply(profile, dry) : manager.Diff(profile);
                    foreach (var c in changes) Console.WriteLine($"  {c.Key,-45} {c.CurrentText} -> {c.WantedText}");
                    Console.WriteLine(changes.Count == 0 ? "Ya está así: nada que cambiar."
                        : apply && !dry ? $"Aplicados {changes.Count} cambios (copia .bak al lado del archivo)." : $"{changes.Count} cambios.");
                    if (profile.Vr) PrintAdvice(RigControl.Core.Graphics.VrAdvisor.Check(profile.Sim, profile.Settings, file.Headset));
                    return 0;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
        Console.WriteLine("Uso: graficos | graficos actual dcs|msfs [--monitor] | graficos guardar dcs|msfs <nombre> [--monitor] | graficos comparar|aplicar <nombre> [--dry] | graficos medir [perfil] [--monitor] | graficos optimizar | graficos mediciones | graficos vuelos");
        return 1;

        static void PrintAdvice(List<RigControl.Core.Graphics.GraphicsAdvice> advice)
        {
            if (advice.Count == 0) { Console.WriteLine("Revisión VR: nada que señalar."); return; }
            Console.WriteLine("Revisión VR:");
            foreach (var a in advice) Console.WriteLine("  " + a);
        }
    }

    // ----------------------------------------------------------------- hoja

    /// <summary>"hoja": hoja de mandos del perfil (fotos con números + tabla) en HTML y, con --pdf, en PDF.</summary>
    private static int SheetCommand(string profile, bool pdf)
    {
        var path = ProfileFile.Resolve(profile);
        if (!File.Exists(path)) { Console.WriteLine($"No existe {path}"); return 1; }
        try
        {
            var catalog = DeviceCatalog.Load();
            var (_, devices) = ProfileSheet.Collect(ProfileFile.Load(path), path, catalog);
            foreach (var d in devices)
            {
                var placed = d.Inputs.Count(i => i.Position is not null);
                Console.WriteLine($"{d.Name}: {d.Inputs.Count} entradas · " +
                                  (d.ImagePath is null ? $"sin foto ({DeviceLayout.PathFor(d.Key)})" : $"foto, {placed} colocadas"));
            }
            var output = pdf ? ProfileSheet.WritePdf(path, catalog) : ProfileSheet.WriteHtml(path, catalog);
            Console.WriteLine($"Hoja: {output}");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or TimeoutException)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    // ----------------------------------------------------------------- ia

    /// <summary>
    /// "ia": genera un perfil con Claude. El resultado es un borrador en %APPDATA%\RigControl\borradores
    /// (nunca en profiles\): se revisa y se copia a mano o se abre en la app.
    /// </summary>
    private static int AiCommand(string[] args, List<DeviceInstance> instances)
    {
        if (args.Length == 0)
        {
            var key = ApiKeyStore.Load();
            Console.WriteLine(key is null ? "Sin clave de la API: \"ia clave\" para guardarla (cifrada con DPAPI)."
                : $"Clave: {ApiKeyStore.Mask(key)} ({(ApiKeyStore.HasStoredKey ? "guardada" : "de ANTHROPIC_API_KEY")}).");
            Console.WriteLine($"Notas para la IA: {ProfileGenerator.NotesPath}{(File.Exists(ProfileGenerator.NotesPath) ? "" : " (no existe)")}");
            Console.WriteLine("Uso: ia clave [borrar] | ia contexto <avión|actual> | ia <avión|actual> \"lo que quieres\" [--base <perfil>]");
            Console.WriteLine("  <avión>: \"dcs:ah-64d_blk_ii\" (DCS) o la carpeta del avión de MSFS; \"actual\" = el cargado ahora.");
            return 0;
        }

        if (args[0].Equals("clave", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1 && args[1].Equals("borrar", StringComparison.OrdinalIgnoreCase))
            {
                ApiKeyStore.Delete();
                Console.WriteLine("Clave borrada.");
                return 0;
            }
            Console.Write("Clave de la API de Anthropic (no se ve al escribirla): ");
            var sb = new System.Text.StringBuilder();
            for (ConsoleKeyInfo k; (k = Console.ReadKey(true)).Key != ConsoleKey.Enter;)
                if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
                else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
            Console.WriteLine();
            if (sb.Length == 0) { Console.WriteLine("Nada que guardar."); return 1; }
            ApiKeyStore.Save(sb.ToString());
            Console.WriteLine($"Guardada en {ApiKeyStore.KeyPath} (cifrada para este usuario de Windows).");
            return 0;
        }

        var onlyContext = args[0].Equals("contexto", StringComparison.OrdinalIgnoreCase);
        var rest = onlyContext ? args.Skip(1).ToList() : [.. args];
        string? baseProfile = null;
        var bi = rest.FindIndex(a => a.Equals("--base", StringComparison.OrdinalIgnoreCase));
        if (bi >= 0)
        {
            if (bi + 1 >= rest.Count) { Console.WriteLine("Falta el perfil después de --base."); return 1; }
            var path = ProfileFile.Resolve(rest[bi + 1]);
            if (!File.Exists(path)) { Console.WriteLine($"No existe {path}"); return 1; }
            baseProfile = File.ReadAllText(path);
            rest.RemoveRange(bi, 2);
        }
        if (rest.Count == 0) { Console.WriteLine("Falta el avión (o \"actual\")."); return 1; }

        var aircraft = rest[0];
        IReadOnlyCollection<string>? events = null;
        if (aircraft.Equals("actual", StringComparison.OrdinalIgnoreCase) || !aircraft.StartsWith("dcs:", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Buscando el avión cargado en DCS o MSFS (máx. 8 s)...");
            var snap = SimSnapshot.Read(8000);
            if (aircraft.Equals("actual", StringComparison.OrdinalIgnoreCase))
            {
                if (snap.Aircraft is null) { Console.WriteLine("No hay ningún avión cargado en DCS ni en MSFS."); return 1; }
                aircraft = snap.Aircraft;
            }
            if (snap.Sim == "msfs") events = snap.InputEvents;
            Console.WriteLine($"Avión: {aircraft}" + (events is null ? "" : $" · {events.Count} controles B:"));
        }
        var sim = aircraft.StartsWith("dcs:", StringComparison.OrdinalIgnoreCase) ? "dcs" : "msfs";
        var connected = instances.Select(i => DeviceScanner.FromProductGuid(i.ProductGuid))
            .Select(v => $"{v.Vid:X4}:{v.Pid:X4}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalog = DeviceCatalog.Load();
        var connectedKeys = catalog.Devices.Where(d => connected.Contains($"{d.Vid}:{d.Pid}")).Select(d => d.Key).ToHashSet();

        var request = new GenerationRequest(sim, aircraft, string.Join(' ', rest.Skip(1)), events, baseProfile, connectedKeys);
        if (onlyContext)
        {
            var dcs = sim == "dcs" ? DcsBiosCatalog.ForAircraft(aircraft) : null;
            var text = ProfileGenerator.BuildContext(request, catalog, dcs);
            Console.WriteLine(text);
            Console.WriteLine($"--- {text.Length:N0} caracteres (~{text.Length / 3.5:N0} tokens) + instrucciones; controles del avión: {dcs?.Count ?? events?.Count ?? 0}");
            return 0;
        }

        var apiKey = ApiKeyStore.Load();
        if (apiKey is null) { Console.WriteLine("Sin clave de la API: usa \"ia clave\"."); return 1; }
        if (string.IsNullOrWhiteSpace(request.Preferences) && baseProfile is null)
            Console.WriteLine("(Sin preferencias: la IA hará un perfil con lo que sepa de los mandos.)");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var progress = new Progress<string>(m => Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {m}"));
        GenerationResult result;
        try
        {
            var generator = new ProfileGenerator(apiKey)
            {
                // Claude pide abrir el simulador: Intro = ya está, "n" = seguir sin él.
                AskForSim = (ask, token) => Task.Run(() =>
                {
                    Console.WriteLine();
                    Console.WriteLine($">>> Claude necesita {ask.SimName} abierto con el avión {ask.Aircraft} cargado (en la cabina).");
                    if (ask.Reason.Length > 0) Console.WriteLine($"    Motivo: {ask.Reason}");
                    Console.Write("    Intro cuando esté listo, \"n\" + Intro para seguir sin él: ");
                    var line = Console.ReadLine();
                    token.ThrowIfCancellationRequested();
                    return line is not null && !line.Trim().Equals("n", StringComparison.OrdinalIgnoreCase);
                }, token),
            };
            result = generator.GenerateAsync(request, progress, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Cancelado.");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine(result.Summary);
        Console.WriteLine("\n" + result.UsageText);
        if (result.Json is null) return 1;

        Directory.CreateDirectory(ProfileGenerator.DraftsDir);
        var safe = string.Concat(aircraft.Replace("dcs:", "").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')).ToLowerInvariant();
        var draft = Path.Combine(ProfileGenerator.DraftsDir, $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(draft, result.Json);
        Console.WriteLine($"Borrador: {draft}");
        Console.WriteLine(result.Validation?.Ok == true ? "Validación: OK." : $"Validación con problemas:\n{result.Validation?.Text}");
        return 0;
    }

    // ----------------------------------------------------------------- hangar

    /// <summary>"hangar": estado de todos. "hangar X --dry": lo que haría. "hangar X": abre la secuencia (Ctrl+C cancela).</summary>
    private static int HangarCommand(string? name, bool dryRun)
    {
        var file = HangarFile.Load();
        var runner = new HangarRunner();
        var chosen = name is null ? file.Hangars : file.Hangars.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (chosen.Count == 0)
        {
            Console.WriteLine(name is null
                ? $"No hay hangares en {HangarFile.DefaultPath}"
                : $"No existe el hangar \"{name}\". Hay: {string.Join(", ", file.Hangars.Select(h => h.Name))}");
            return 1;
        }

        if (name is null || dryRun)
        {
            foreach (var hangar in chosen)
            {
                Console.WriteLine($"=== {hangar.Name} ===");
                if (hangar.ModSet is { } setName) ApplyHangarMods(setName, dryRun: true);
                if (hangar.GraphicsProfile is { } gp) Console.WriteLine("  " + new RigControl.Core.Graphics.GraphicsManager().ApplyNamed(gp, dryRun: true));
                foreach (var step in hangar.Steps)
                {
                    var state = !step.Enabled ? "desactivado" : runner.Inspect(step) switch
                    {
                        HangarPresence.Open => "ya abierto: no se lanzaría",
                        HangarPresence.Closed => "se lanzaría",
                        HangarPresence.Missing => "NO EXISTE",
                        _ => "se lanzaría (sin proceso: no se sabe si ya está abierto)",
                    };
                    if (step.Enabled && step.Priority is { } priority)
                        state += HangarRunner.ProcessNameOf(step) is null
                            ? $"; prioridad {HangarRunner.PriorityText(priority)} IMPOSIBLE: falta \"process\""
                            : $"; prioridad {HangarRunner.PriorityText(priority)}";
                    if (step.Enabled && step.Window is { IsEmpty: false } window)
                        state += HangarRunner.ProcessNameOf(step) is null
                            ? $"; ventana {HangarRunner.WindowText(window)} IMPOSIBLE: falta \"process\""
                            : $"; ventana {HangarRunner.WindowText(window)}";
                    if (step.Enabled && step == hangar.Steps.LastOrDefault(s => s.Enabled && s.Focus))
                        state += "; al frente al terminar";
                    Console.WriteLine($"  {step.Name,-34} {state}");
                }
            }
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            var snapshots = SnapshotsFile.Load();
            Console.WriteLine("  " + SnapshotStore.Describe(new SnapshotStore(keepAuto: snapshots.KeepAuto).AutoAll(snapshots, $"al abrir {chosen[0].Name}")));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Instantáneas: {ex.Message}");
        }
        if (chosen[0].ModSet is { } modSet) ApplyHangarMods(modSet, dryRun: false);
        if (chosen[0].GraphicsProfile is { } graphics) Console.WriteLine("  " + new RigControl.Core.Graphics.GraphicsManager().ApplyNamed(graphics));
        var steps = chosen[0].Steps;
        runner.StepChanged += (i, _, detail) => Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {steps[i].Name}: {detail}");
        var outcome = runner.RunAsync(chosen[0], cts.Token).GetAwaiter().GetResult();
        Console.WriteLine(outcome switch
        {
            HangarOutcome.Ok => "Secuencia completa.",
            HangarOutcome.Cancelled => "Secuencia cancelada.",
            _ => "Secuencia terminada con problemas.",
        });
        if (!runner.Watching.IsCompleted)
        {
            Console.WriteLine($"Vigilando prioridad y ventanas (prioridad: hasta {runner.PriorityWatchSeconds:0} s después de cerrarse; " +
                              $"ventanas nuevas: {runner.WindowWatchSeconds:0} s; Ctrl+C para salir)…");
            runner.Watching.GetAwaiter().GetResult();
        }
        return outcome == HangarOutcome.WithProblems ? 1 : 0;
    }

    /// <summary>Conjunto de mods de un hangar, antes de abrir nada. Un fallo se dice y la secuencia sigue.</summary>
    private static void ApplyHangarMods(string setName, bool dryRun)
    {
        try
        {
            var file = ModsFile.Load();
            if (file.FindSet(setName) is not { } set)
            {
                Console.WriteLine($"  Mods: no existe el conjunto «{setName}» en {ModsFile.DefaultPath}; no se tocan.");
                return;
            }
            var changes = new ModManager().ApplySet(file, set, dryRun);
            Console.WriteLine($"  Mods «{set.Name}»: {(changes.Count == 0 ? "ya estaban así" : $"{changes.Count} cambio(s){(dryRun ? " (sin hacer)" : "")}")}");
            foreach (var c in changes) Console.WriteLine($"    {c}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Mods: {ex.Message}");
        }
    }

    /// <summary>
    /// "instantaneas": grupos y sus instantáneas numeradas. "instantaneas crear G|todo [nombre]".
    /// "instantaneas comparar G n": qué cambió desde la n. "instantaneas restaurar G n" (pide confirmación).
    /// </summary>
    private static int SnapshotsCommand(string[] args)
    {
        var file = SnapshotsFile.Load();
        var store = new SnapshotStore(keepAuto: file.KeepAuto);
        if (args.Length == 0)
        {
            foreach (var g in file.Groups)
            {
                var files = SnapshotStore.Collect(g);
                Console.WriteLine($"=== {g.Name}: {files.Count} archivos en {SnapshotStore.ExpandRoot(g)}{(store.IsLocked(g) ? $"  ({g.Process} ABIERTO: no se puede restaurar)" : "")} ===");
                var list = store.List(g.Name);
                for (var i = 0; i < list.Count; i++)
                {
                    var s = list[i];
                    Console.WriteLine($"  {i + 1,3}. {s.Created:dd/MM/yyyy HH:mm:ss}  {(s.Auto ? "auto  " : "manual")}  {s.FileCount,4} arch. {s.Bytes / 1024.0,8:N0} KB  {s.Name}");
                }
                if (list.Count == 0) Console.WriteLine("  (ninguna)");
            }
            Console.WriteLine($"Carpeta: {store.StoreDir}");
            return 0;
        }

        var verb = args[0].ToLowerInvariant();
        if (verb == "crear" && args.Length > 1)
        {
            var groups = args[1].Equals("todo", StringComparison.OrdinalIgnoreCase) ? file.Groups
                       : file.FindGroup(args[1]) is { } one ? [one] : [];
            if (groups.Count == 0) { Console.WriteLine($"No existe el grupo «{args[1]}». Hay: {string.Join(", ", file.Groups.Select(g => g.Name))}, todo"); return 1; }
            var name = string.Join(' ', args.Skip(2));
            var problems = 0;
            foreach (var g in groups)
            {
                try
                {
                    var s = store.Create(g, name, auto: false)!;
                    Console.WriteLine($"  {g.Name}: {s.FileCount} archivos -> {s.Path}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    problems++;
                    Console.WriteLine($"  {g.Name}: {ex.Message}");
                }
            }
            return problems == 0 ? 0 : 1;
        }

        if (verb is "comparar" or "restaurar" && args.Length > 2 && file.FindGroup(args[1]) is { } group && int.TryParse(args[2], out var n))
        {
            var list = store.List(group.Name);
            if (n < 1 || n > list.Count) { Console.WriteLine($"{group.Name} tiene {list.Count} instantáneas (1-{list.Count})."); return 1; }
            var snap = list[n - 1];
            var diff = store.Compare(group, snap);
            Console.WriteLine($"{group.Name}, {snap.Created:dd/MM/yyyy HH:mm:ss} «{snap.Name}» frente a lo actual:");
            foreach (var (rel, state) in diff) Console.WriteLine($"  {state,-34} {rel}");
            if (diff.Count == 0) Console.WriteLine("  igual que ahora");
            if (verb == "comparar") return 0;
            Console.Write("Se sobrescribirán esos archivos (los nuevos se quedan). ¿Restaurar? (s/N) ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "s", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("No se ha tocado nada."); return 1; }
            try
            {
                var r = store.Restore(group, snap);
                Console.WriteLine($"Restaurados {r.Files} archivos.{(r.Before is { } b ? $" Lo de antes está en «{b.Name}»." : "")}");
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                Console.WriteLine($"No se pudo restaurar: {ex.Message}");
                return 1;
            }
        }

        Console.WriteLine("Uso: instantaneas | instantaneas crear <grupo|todo> [nombre] | instantaneas comparar|restaurar <grupo> <n>");
        return 1;
    }

    /// <summary>"mods": estado. "mods X [--dry]": aplica el conjunto X. "mods importar B [--dry]": mete en la biblioteca B lo que hay suelto en su destino.</summary>
    private static int ModsCommand(string[] args, bool dryRun)
    {
        var file = ModsFile.Load();
        var mgr = new ModManager();
        if (args.Length == 0)
        {
            foreach (var lib in file.Libraries)
            {
                var entries = mgr.Scan(lib);
                Console.WriteLine($"=== {lib.Name}{(mgr.IsLocked(lib) ? $"  ({lib.Process} ABIERTO: no se puede cambiar)" : "")} ===");
                Console.WriteLine($"  biblioteca: {ModManager.Expand(lib.Library)}");
                Console.WriteLine($"  destino:    {ModManager.Expand(lib.Target)}");
                foreach (var e in entries)
                {
                    var state = e.State switch
                    {
                        ModState.Active => "activo",
                        ModState.Inactive => "inactivo",
                        ModState.Foreign => "suelto en el destino (sin importar)",
                        ModState.Conflict => "CONFLICTO",
                        _ => "ENLACE ROTO",
                    };
                    var always = lib.Always.Contains(e.Name, StringComparer.OrdinalIgnoreCase) ? " [siempre]" : "";
                    Console.WriteLine($"  {e.Name,-48} {state}{always}{(e.Detail is null ? "" : $": {e.Detail}")}");
                }
            }
            Console.WriteLine($"Conjuntos: {(file.Sets.Count == 0 ? "ninguno" : string.Join(", ", file.Sets.Select(s => s.Name)))}");
            return 0;
        }

        if (args[0].Equals("importar", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2 || file.FindLibrary(string.Join(' ', args.Skip(1))) is not { } lib)
            {
                Console.WriteLine($"Uso: mods importar <biblioteca> [--dry]. Hay: {string.Join(", ", file.Libraries.Select(l => l.Name))}");
                return 1;
            }
            var loose = mgr.Scan(lib).Where(e => e.State == ModState.Foreign && e.Detail is null).ToList();
            var problems = 0;
            foreach (var e in loose)
            {
                if (dryRun) { Console.WriteLine($"  importaría {e.Name}"); continue; }
                try
                {
                    mgr.Import(lib, e.Name);
                    Console.WriteLine($"  importado {e.Name}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    problems++;
                    Console.WriteLine($"  NO se pudo importar {e.Name}: {ex.Message}");
                }
            }
            Console.WriteLine(loose.Count == 0 ? "No hay nada suelto que importar." : $"{loose.Count - problems} de {loose.Count}{(dryRun ? " (sin hacer)" : "")}.");
            return problems == 0 ? 0 : 1;
        }

        var name = string.Join(' ', args);
        if (file.FindSet(name) is not { } set)
        {
            Console.WriteLine($"No existe el conjunto «{name}». Hay: {string.Join(", ", file.Sets.Select(s => s.Name))}");
            return 1;
        }
        var changes = mgr.ApplySet(file, set, dryRun);
        foreach (var c in changes) Console.WriteLine($"  {c}");
        Console.WriteLine(changes.Count == 0 ? "Ya estaba así." : $"{changes.Count} cambio(s){(dryRun ? " (sin hacer)" : "")}.");
        return changes.Any(c => c.Error is not null) ? 1 : 0;
    }

    /// <summary>"ventanas X": ventanas principales del proceso X con su posición, para copiarla a hangar.json.</summary>
    private static int WindowsCommand(string process)
    {
        if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) process = process[..^4];
        var windows = new WindowHost();
        var found = 0;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(process))
            using (p)
                foreach (var hwnd in windows.MainWindows(p.Id))
                {
                    if (windows.Describe(hwnd) is not { } w) continue;
                    found++;
                    var state = w.State switch { HangarWindowState.Minimized => "minimizada", HangarWindowState.Maximized => "maximizada", _ => "normal" };
                    Console.WriteLine($"{process} (pid {p.Id}) \"{p.MainWindowTitle}\": {state}{(w.Topmost ? ", siempre encima" : "")}");
                    Console.WriteLine($"  \"window\": {{ \"x\": {w.X}, \"y\": {w.Y}, \"width\": {w.Width}, \"height\": {w.Height} }}");
                }
        if (found == 0) Console.WriteLine($"No hay ventanas principales visibles de \"{process}\" (¿cerrado o en la bandeja?).");
        return found == 0 ? 1 : 0;
    }

    // ----------------------------------------------------------------- run

    /// <summary>"run perfil": un archivo fijo. "run auto": la carpeta de perfiles, según el avión cargado (MSFS o DCS).</summary>
    private static int Run(string target, bool dryRun)
    {
        using var session = RunSession.Create(target, dryRun);
        if (session is null) return 1;
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

        session.Run(stop.Token);
        stop.Cancel();
        return 0;
    }

    // ----------------------------------------------------------------- sim

    /// <summary>Escucha DCS-BIOS: avión cargado y paquetes por segundo (para comprobar la instalación).</summary>
    private static void DcsWatch(int seconds)
    {
        using var dcs = new DcsBiosOutput();
        dcs.AircraftChanged += name => Console.WriteLine($"  avión: {dcs.Aircraft} (perfiles: \"aircraft\": [\"{name}\"])");
        Console.WriteLine($"Escuchando DCS-BIOS en {DcsBiosOutput.MulticastGroup}:{DcsBiosOutput.ExportPort} durante {seconds} s...");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long next = 1000;
        while (clock.ElapsedMilliseconds < seconds * 1000L)
        {
            dcs.Tick(clock.ElapsedMilliseconds);
            if (clock.ElapsedMilliseconds >= next)
            {
                next += 1000;
                Console.WriteLine($"--- {clock.ElapsedMilliseconds / 1000} s  {(dcs.IsActive ? "llegan datos" : "sin datos")}  avión={dcs.Aircraft ?? "-"}");
            }
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Muestra al momento cada cambio de los mandos de la cabina (controles de DCS-BIOS con entrada y
    /// valor numérico; sin indicadores ni agujas), con su posición y descripción. Sirve para comprobar
    /// un perfil o averiguar qué control es cada interruptor. Filtro opcional por nombre o descripción.
    /// Hasta ESC (o hasta detener el proceso si no hay consola).
    /// </summary>
    private static void DcsWatchControls(string? filter)
    {
        using var dcs = new DcsBiosOutput();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"Escuchando DCS-BIOS{(filter is null ? "" : $" (filtro \"{filter}\")")}. ESC para salir.");

        IReadOnlyList<DcsControl> controls = [];
        int?[] last = [];
        string? aircraft = null;
        var baseline = 0;
        while (!ConsoleInput.EscapePressed())
        {
            dcs.Tick(clock.ElapsedMilliseconds);
            if (dcs.Aircraft is { } a && a != aircraft)
            {
                aircraft = a;
                // Sin filtro, solo los mandos (con entrada); con filtro también las luces y demás salidas.
                controls = [.. DcsBiosCatalog.ForAircraft(a).Where(c => c.Address is not null &&
                    (filter is null ? c.Inputs.Length > 0
                        : c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                          c.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)))];
                last = new int?[controls.Count];
                baseline = 0;
                Console.WriteLine($"Avión: {a}. Vigilando {controls.Count} mandos; los cambios salen en cuanto llega el estado de cada uno.");
            }

            for (var i = 0; i < controls.Count; i++)
            {
                if (dcs.Read(controls[i]) is not { } v || v == last[i]) continue;
                var first = last[i] is null;
                last[i] = v;
                if (first)
                {
                    // Primer valor: es el estado de partida, no un cambio (con filtro se enseña).
                    if (filter is not null)
                        Console.WriteLine($"  inicio  {controls[i].Name,-34} = {v}  {controls[i].Description}");
                    if (++baseline == controls.Count) Console.WriteLine("(ya ha llegado el estado de todos los mandos)");
                    continue;
                }
                var c = controls[i];
                var pos = v < c.Positions.Length ? $" {c.Positions[v]}" : "";
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {c.Name,-34} = {v}{pos,-10}  {c.Description}");
            }
            Thread.Sleep(10);
        }
    }

    /// <summary>Envía una orden a DCS-BIOS ("UFC_1" = pulsar y soltar; "CONTROL VALOR" = ese valor).</summary>
    private static int DcsSend(string text, bool readOnly = false)
    {
        var action = ActionSpec.Parse("D:" + text, out var error);
        if (action is null) { Console.WriteLine(error); return 1; }
        using var dcs = new DcsBiosOutput();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Pump(long untilMs) { while (clock.ElapsedMilliseconds < untilMs) { dcs.Tick(clock.ElapsedMilliseconds); Thread.Sleep(10); } }

        // Estado del control antes y después (si está en las definiciones JSON de DCS-BIOS).
        var output = FindDcsOutput(action.Name);
        string Read() => !dcs.IsActive ? "(sin datos de DCS)"
            : output is { } o
                ? ((dcs.Image[o.Address] | dcs.Image[o.Address + 1] << 8) & o.Mask) >> o.Shift is var v ? $"{v} (de 0 a {o.Max})" : ""
                // Controles de texto (frecuencias, pantallas): entre comillas, tal cual para "icp" del perfil.
                : dcs.Find(action.Name) is { TextAddress: not null } c
                    ? (dcs.ReadText(c) is { } t ? $"\"{t}\" ({t.Length} car.)" : "(texto aún sin llegar)")
                    : "(no encuentro el control en las definiciones de DCS-BIOS)";
        // DCS-BIOS manda los cambios al momento, pero el estado completo lo repite poco a poco (2 palabras
        // por envío, ~30 envíos/s: el AH-64D tarda ~16 s en dar la vuelta). Al arrancar, una dirección
        // vale 0 hasta que llega su turno o cambia. Por eso "enviar" no enseña el "antes".
        if (readOnly)
        {
            const int SyncMs = 18000;
            Console.WriteLine($"Esperando {SyncMs / 1000} s a tener el estado completo de la cabina...");
            Pump(SyncMs);
            Console.WriteLine($"Ahora:   {action.Name} = {Read()}");
            Console.WriteLine("Vigilando 20 s: mueve el control en la cabina...");
            var last = Read();
            while (clock.ElapsedMilliseconds < SyncMs + 20000)
            {
                Pump(clock.ElapsedMilliseconds + 50);
                var now = Read();
                if (now != last) Console.WriteLine($"  {clock.ElapsedMilliseconds / 1000.0:0.0} s  {action.Name} = {now}");
                last = now;
            }
            return 0;
        }
        if (!dcs.Send(action, tap: true)) { Console.WriteLine("ERROR: no se ha podido enviar la orden."); return 1; }
        Console.WriteLine($"Enviado: {System.Text.Encoding.ASCII.GetString(action.DcsMessage!).Trim()}");
        Pump(1200); // suelta el pulsador y deja llegar el estado nuevo (un cambio llega al momento)
        if (output is not null) Console.WriteLine($"Después: {action.Name} = {Read()}  (si no ha cambiado nada, puede ser 0 sin más: ver \"dcs leer\")");
        else Console.WriteLine("(no encuentro el control en las definiciones JSON de DCS-BIOS: no puedo leer su estado)");
        return 0;
    }

    /// <summary>Diagnóstico: muestra cada escritura del flujo de DCS-BIOS que toca una dirección.</summary>
    private static void DcsRaw(int address, int seconds)
    {
        using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        s.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, true);
        s.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, DcsBiosOutput.ExportPort));
        s.SetSocketOption(System.Net.Sockets.SocketOptionLevel.IP, System.Net.Sockets.SocketOptionName.AddMembership,
            new System.Net.Sockets.MulticastOption(DcsBiosOutput.MulticastGroup, System.Net.IPAddress.Any));
        s.ReceiveTimeout = 500;
        var buf = new byte[65536];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int packets = 0, bytes = 0, maxLen = 0;
        Console.WriteLine($"Escrituras que tocan 0x{address:X4} durante {seconds} s:");
        while (clock.ElapsedMilliseconds < seconds * 1000L)
        {
            int n;
            try { n = s.Receive(buf); } catch (System.Net.Sockets.SocketException) { continue; }
            packets++; bytes += n; maxLen = Math.Max(maxLen, n);
            var i = 0;
            while (i + 4 <= n)
            {
                if (buf[i] == 0x55 && buf[i + 1] == 0x55 && buf[i + 2] == 0x55 && buf[i + 3] == 0x55) { i += 4; continue; }
                var a = buf[i] | buf[i + 1] << 8; var len = buf[i + 2] | buf[i + 3] << 8; i += 4;
                if (i + len > n) { Console.WriteLine($"  paquete {packets}: escritura cortada en {a:X4} len {len} (n={n})"); break; }
                if (address >= a && address < a + len)
                {
                    var off = address - a;
                    var word = buf[i + off] | (off + 1 < len ? buf[i + off + 1] << 8 : 0);
                    Console.WriteLine($"  {clock.ElapsedMilliseconds / 1000.0:0.00} s  bloque {a:X4}+{len}  palabra={word:X4}");
                }
                i += len;
            }
        }
        Console.WriteLine($"{packets} paquetes, {bytes} bytes, el mayor de {maxLen} bytes.");
    }

    private sealed record DcsOutput(int Address, int Mask, int Shift, int Max);

    /// <summary>Busca la salida entera de un control en Saved Games\DCS*\Scripts\DCS-BIOS\doc\json\*.json.</summary>
    private static DcsOutput? FindDcsOutput(string control)
    {
        var savedGames = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
        foreach (var dir in Directory.EnumerateDirectories(savedGames, "DCS*"))
        {
            var json = Path.Combine(dir, "Scripts", "DCS-BIOS", "doc", "json");
            if (!Directory.Exists(json)) continue;
            foreach (var file in Directory.EnumerateFiles(json, "*.json"))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                foreach (var category in doc.RootElement.EnumerateObject())
                {
                    if (category.Value.ValueKind != System.Text.Json.JsonValueKind.Object ||
                        !category.Value.TryGetProperty(control, out var c) ||
                        !c.TryGetProperty("outputs", out var outputs)) continue;
                    foreach (var o in outputs.EnumerateArray())
                        if (o.TryGetProperty("type", out var t) && t.GetString() == "integer")
                            return new DcsOutput(o.GetProperty("address").GetInt32(), o.GetProperty("mask").GetInt32(),
                                                 o.GetProperty("shift_by").GetInt32(), o.GetProperty("max_value").GetInt32());
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Muestra cada segundo los valores de las variables del perfil (luces y condiciones "state"):
    /// A:/L: de MSFS y D: de DCS-BIOS, igual que los ve "run".
    /// </summary>
    private static void SimVars(string profileName, int seconds)
    {
        var profile = ProfileLoader.LoadAndCheck(ProfileFile.Resolve(profileName), verbose: false);
        if (profile is null) return;
        using var sim = new SimConnectOutput();
        using var dcs = new DcsBiosOutput();
        using var il2 = new Il2Telemetry();
        using var values = new ActionDispatcher(null, sim, dryRun: true, dcs, il2); // solo para juntar los valores
        values.ProfileLoaded(profile);
        var vars = profile.WatchedVars;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long next = 1000;
        while (clock.ElapsedMilliseconds < seconds * 1000L)
        {
            values.Tick(clock.ElapsedMilliseconds);
            if (clock.ElapsedMilliseconds >= next)
            {
                next += 1000;
                Console.WriteLine($"--- {clock.ElapsedMilliseconds / 1000} s  MSFS={sim.IsConnected}  DCS-BIOS={dcs.IsActive} ({dcs.Aircraft ?? "-"})  IL-2={il2.IsActive} ({il2.Aircraft ?? "-"})");
                for (var i = 0; i < vars.Length; i++)
                    Console.WriteLine($"  {vars[i].Name,-40} {(vars[i].IsMsfs ? vars[i].Unit : ""),-8} {values.Values[i]}");
            }
            Thread.Sleep(20);
        }
    }

    /// <summary>
    /// "sim enviar ACCIÓN": manda a MSFS una acción K:, B:, L: o A: (como la haría un binding) y avisa si la
    /// rechaza. En una A: lee la variable antes y después, para ver si de verdad ha cambiado.
    /// </summary>
    private static int SimSend(string actionText)
    {
        var action = ActionSpec.Parse(actionText, out var error);
        if (action is null || !action.IsSim)
        {
            Console.WriteLine(error ?? "Solo acciones de MSFS: K:EVENTO[=n], B:INPUT_EVENT[=v], L:Var=v o A:VARIABLE[, unidades]=v.");
            return 1;
        }

        // Perfil de un solo binding para que SimConnectOutput registre la acción; en una A:, la misma
        // variable como condición "state" para leerla.
        var binding = new BindingFile { Input = "x:b1", Gesture = "press", Action = actionText };
        if (action.Kind == ActionKind.AVar) binding.When = new ConditionFile { State = $"A:{action.Name}, {action.Unit}" };
        var (profile, errors) = ProfileCompiler.Compile(new ProfileFile
        {
            Name = "sim enviar", Sim = "msfs", Devices = new() { ["x"] = "0000:0000" }, Bindings = [binding],
        });
        if (profile is null)
        {
            foreach (var e in errors) Console.WriteLine(e);
            return 1;
        }

        using var sim = new SimConnectOutput();
        var eventsReady = false;
        sim.InputEventsReady += _ => eventsReady = true;
        sim.Prepare(profile);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Wait(long ms)
        {
            var until = clock.ElapsedMilliseconds + ms;
            while (clock.ElapsedMilliseconds < until) { sim.Tick(clock.ElapsedMilliseconds); Thread.Sleep(20); }
        }
        string Value() => sim.Values.Length > 0 ? sim.Values[0].ToString(System.Globalization.CultureInfo.CurrentCulture) : "";

        Console.WriteLine("Conectando con MSFS (máx. 15 s)...");
        while (clock.ElapsedMilliseconds < 15000 && !(sim.IsConnected && (eventsReady || action.Kind != ActionKind.InputEvent)))
            Wait(50);
        if (!sim.IsConnected)
        {
            Console.WriteLine("No se pudo conectar. ¿Está MSFS abierto?");
            return 1;
        }
        Wait(1000); // registro de la acción y primer valor de la variable
        if (action.Kind == ActionKind.AVar) Console.WriteLine($"Antes:   A:{action.Name} = {Value()} {action.Unit}");

        if (!sim.Send(action))
        {
            Console.WriteLine("No se ha podido enviar (mira los avisos de arriba).");
            return 1;
        }
        Console.WriteLine($"Enviado: {actionText}");
        Wait(1500); // excepciones de MSFS y valor nuevo
        if (action.Kind == ActionKind.AVar)
        {
            Console.WriteLine($"Después: A:{action.Name} = {Value()} {action.Unit}");
            Console.WriteLine("Si no ha cambiado y no hay aviso, la variable no es modificable o el avión la recalcula: usa un B: o un K:.");
        }
        return 0;
    }

    /// <summary>
    /// "il2 [s]": escucha la telemetría de IL-2 (UDP 5678) y enseña cada segundo el avión y todas las variables I:,
    /// como las verá un perfil. Para comprobar que IL-2 emite y qué valores da cada avión.
    /// </summary>
    private static void Il2Listen(int seconds)
    {
        using var il2 = new Il2Telemetry();
        var vars = Il2Telemetry.Vars.Select(v => (v.Name, Var: new Il2Var(v.Kind, -1), v.Indexed)).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long next = 1000;
        Console.WriteLine($"Escuchando IL-2 en UDP {Il2Telemetry.DefaultPort} durante {seconds} s (data\\startup.cfg, bloque telemetrydevice)...");
        string Num(double v) => double.IsNaN(v) ? "-" : v.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        while (clock.ElapsedMilliseconds < seconds * 1000L)
        {
            il2.Tick(clock.ElapsedMilliseconds);
            if (clock.ElapsedMilliseconds >= next)
            {
                next += 1000;
                Console.WriteLine($"--- {clock.ElapsedMilliseconds / 1000} s  datos={(il2.IsActive ? "sí" : "no")}  avión={il2.Aircraft ?? "-"}{(il2.Paused ? "  (pausa/menú)" : "")}");
                if (!il2.HasState) continue;
                var parts = new List<string>();
                foreach (var (name, v, indexed) in vars)
                {
                    parts.Add($"{name}={Num(il2.Read(v))}");
                    if (!indexed) continue;
                    for (var i = 0; i < 8 && !double.IsNaN(il2.Read(v with { Index = i })); i++)
                        if (i > 0 || name == "GEAR") parts.Add($"{name}:{i + 1}={Num(il2.Read(v with { Index = i }))}");
                }
                Console.WriteLine("  " + string.Join("  ", parts));
            }
            Thread.Sleep(20);
        }
    }

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

    // ---------------------------------------------------------------- winwing

    /// <summary>"winwing": lista. "winwing led D N V": pone un LED. "winwing barrido D": hace parpadear cada LED para identificarlo.</summary>
    /// <summary>`winwing pantalla [prueba|borrar]`: dibuja en la pantalla del ICP.</summary>
    private static int IcpScreenCommand(string[] args)
    {
        using var screen = IcpDisplay.TryOpen();
        if (screen is null)
        {
            Console.WriteLine("No se encontró o no se pudo abrir el ICP (BF06).");
            return 1;
        }
        var what = args.Length > 0 ? args[0].ToLowerInvariant() : "prueba";
        screen.Clear();
        if (what == "prueba")
        {
            // Marco, una diagonal y un bloque de 8x8 arriba a la izquierda: para ver orientación y bits.
            for (var x = 0; x < IcpDisplay.Width; x++) { screen.SetPixel(x, 0); screen.SetPixel(x, IcpDisplay.Height - 1); }
            for (var y = 0; y < IcpDisplay.Height; y++) { screen.SetPixel(0, y); screen.SetPixel(IcpDisplay.Width - 1, y); }
            for (var x = 0; x < IcpDisplay.Width; x++) screen.SetPixel(x, x * (IcpDisplay.Height - 1) / (IcpDisplay.Width - 1));
            for (var y = 3; y < 11; y++) for (var x = 3; x < 11; x++) screen.SetPixel(x, y);
        }
        else if (what == "fuente")
        {
            // Todos los caracteres, 25 por fila, y una palabra invertida en la última.
            const string all = "ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890↕()<>[]+-*/=°|~.,!?:;&_'\"%#@{}";
            for (var i = 0; i < all.Length; i += IcpFont.Columns)
                screen.DrawText(0, i / IcpFont.Columns, all[i..Math.Min(all.Length, i + IcpFont.Columns)]);
            screen.DrawText(0, 4, "INVERTIDO", inverted: true);
        }
        else if (what == "texto")
        {
            // Cada argumento es una línea; entre corchetes = invertido: "UHF [242.00]".
            for (var row = 0; row < Math.Min(IcpFont.Rows, args.Length - 1); row++)
            {
                var line = args[row + 1];
                var column = 0;
                foreach (var part in System.Text.RegularExpressions.Regex.Split(line, @"(\[[^\]]*\])"))
                {
                    var inv = part.Length > 1 && part[0] == '[' && part[^1] == ']';
                    var t = inv ? part[1..^1] : part;
                    screen.DrawText(column, row, t, inv);
                    column += t.Length;
                }
            }
        }
        else if (what != "borrar")
        {
            Console.WriteLine("Uso: winwing pantalla [prueba|fuente|borrar|texto \"línea 1\" \"línea 2\" ...]");
            return 1;
        }
        screen.Flush();
        Console.WriteLine(what switch
        {
            "prueba" => "Pantalla del ICP: marco, diagonal de arriba-izquierda a abajo-derecha y un cuadrado arriba a la izquierda.",
            "fuente" => "Pantalla del ICP: todos los caracteres de la fuente y \"INVERTIDO\" en la última fila.",
            "texto" => "Pantalla del ICP: texto escrito.",
            _ => "Pantalla del ICP borrada.",
        });
        return 0;
    }

    private static int WinWingCommand(string[] args)
    {
        string? Option(string name)
        {
            var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var module = Option("--modulo") is { } ms ? Convert.ToInt32(ms, 16) : (int?)null;
        var onValue = Option("--valor") is { } vs ? byte.Parse(vs) : (byte)255;
        var plain = args.Where((a, i) => !a.StartsWith("--") && (i == 0 || !args[i - 1].StartsWith("--"))).ToArray();

        if (plain.Length == 0)
        {
            Console.WriteLine("Dispositivos WinWing con el protocolo de LEDs (output de 14 B; el ICP, 64 B):");
            foreach (var (pid, name) in WinWingPanel.List())
            {
                var alias = WinWingPanel.Aliases.FirstOrDefault(a => a.Value == pid).Key ?? "-";
                Console.WriteLine($"  {pid:X4}  {alias,-9} {name}  (módulo {WinWingPanel.DefaultModule(pid):X4})");
            }
            return 0;
        }
        if (plain[0].Equals("luces", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Luces de WinWing para la sección \"lights\" de un perfil:");
            foreach (var n in WinWingPanel.LightNames) Console.WriteLine($"  {n}");
            return 0;
        }
        if (plain[0].Equals("pantalla", StringComparison.OrdinalIgnoreCase))
            return IcpScreenCommand(plain.Skip(1).ToArray());
        if (plain.Length < 2)
        {
            Console.WriteLine("Falta el dispositivo (alias o PID en hexadecimal).");
            return 1;
        }
        var device = WinWingPanel.Aliases.TryGetValue(plain[1], out var known) ? known : Convert.ToInt32(plain[1], 16);
        using var panel = WinWingPanel.TryOpen(device);
        if (panel is null)
        {
            Console.WriteLine($"No se encontró o no se pudo abrir el WinWing {device:X4}.");
            return 1;
        }
        var mod = module ?? WinWingPanel.DefaultModule(device);

        switch (plain[0].ToLowerInvariant())
        {
            case "led" when plain.Length >= 4:
                panel.Set(byte.Parse(plain[2]), byte.Parse(plain[3]), mod);
                Console.WriteLine($"{panel.Name}: módulo {mod:X4}, LED {plain[2]} = {plain[3]}");
                return 0;
            case "barrido":
                var from = plain.Length > 2 ? int.Parse(plain[2]) : 0;
                var to = plain.Length > 3 ? int.Parse(plain[3]) : 63;
                Console.WriteLine($"{panel.Name}, módulo {mod:X4}: cada LED parpadea; apunta cuál se ve.");
                Console.WriteLine("Intro = siguiente, r = repetir, número = saltar a ese LED, q = salir. Al pasar, cada LED queda apagado (0).\n");
                for (var led = from; led <= to; led++)
                {
                    Console.Write($"  LED {led,3}: ");
                    for (var k = 0; k < 3; k++)
                    {
                        panel.Set((byte)led, onValue, mod);
                        Thread.Sleep(400);
                        panel.Set((byte)led, 0, mod);
                        Thread.Sleep(250);
                    }
                    panel.Set((byte)led, onValue, mod); // se queda encendido mientras decides
                    var answer = Console.ReadLine()?.Trim() ?? "q";
                    panel.Set((byte)led, 0, mod);
                    if (answer.Equals("q", StringComparison.OrdinalIgnoreCase)) break;
                    if (answer.Equals("r", StringComparison.OrdinalIgnoreCase)) led--;
                    else if (int.TryParse(answer, out var jump)) led = jump - 1;
                }
                Console.WriteLine("Fin. La retroiluminación (LED 0) la vuelve a poner SimAppPro, o: winwing led <disp> 0 <0-255>.");
                return 0;
            default:
                Console.WriteLine("Uso: winwing | winwing luces | winwing led <disp> <n> <valor> [--modulo XXXX] | winwing barrido <disp> [desde] [hasta] [--modulo XXXX] [--valor N]");
                return 1;
        }
    }

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
