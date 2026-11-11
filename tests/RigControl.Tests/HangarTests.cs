using System.ComponentModel;
using System.Diagnostics;
using RigControl.Core.Hangar;

namespace RigControl.Tests;

/// <summary>Secuencias del Hangar con un Windows falso, más un proceso real oculto (ping).</summary>
internal static class HangarTests
{
    /// <summary>Lanzar un destino hace aparecer su proceso (o no, si no está en la tabla).</summary>
    private sealed class FakeHost : IProcessHost
    {
        private sealed class Proc(string name) { public string Name = name; public ProcessPriorityClass Priority = ProcessPriorityClass.Normal; }

        private readonly Dictionary<int, Proc> _procs = [];
        private int _nextId = 100;
        public readonly List<string> Started = [];
        public readonly Dictionary<string, string> Spawns = [];
        public readonly Dictionary<string, Exception> Errors = [];
        public string? LastWorkingDir;
        /// <summary>Lo que ha puesto el lanzador, por nombre (el último).</summary>
        public readonly Dictionary<string, ProcessPriorityClass> Priorities = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Elevated = new(StringComparer.OrdinalIgnoreCase);

        public FakeHost(params string[] alreadyRunning) { foreach (var n in alreadyRunning) Add(n); }

        public int Add(string name) { lock (_procs) { _procs[++_nextId] = new Proc(name); return _nextId; } }

        /// <summary>Como DCS: el proceso se cierra y arranca otro con el mismo nombre, en Normal.</summary>
        public int Restart(string name)
        {
            lock (_procs)
            {
                foreach (var id in _procs.Where(kv => kv.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
                    _procs.Remove(id);
                return Add(name);
            }
        }

        public void SetByHand(int id, ProcessPriorityClass priority) { lock (_procs) _procs[id].Priority = priority; }

        public ProcessPriorityClass? PriorityOf(int id) { lock (_procs) return _procs.TryGetValue(id, out var p) ? p.Priority : null; }

        public IReadOnlyList<int> FindProcesses(string name)
        {
            lock (_procs) return [.. _procs.Where(kv => kv.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key)];
        }

        public bool SetPriority(int id, ProcessPriorityClass priority)
        {
            lock (_procs)
            {
                if (!_procs.TryGetValue(id, out var p)) throw new InvalidOperationException("ya no está");
                if (Elevated.Contains(p.Name)) throw new Win32Exception(5);
                Priorities[p.Name] = priority;
                if (p.Priority == priority) return false;
                p.Priority = priority;
                return true;
            }
        }

        public void Close(string name)
        {
            lock (_procs)
                foreach (var id in _procs.Where(kv => kv.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
                    _procs.Remove(id);
        }

        public bool IsRunning(string name) => FindProcesses(name).Count > 0;

        public void Start(string target, string? args, string? workingDirectory)
        {
            if (Errors.TryGetValue(target, out var error)) throw error;
            Started.Add(args is null ? target : $"{target} {args}");
            LastWorkingDir = workingDirectory;
            if (Spawns.TryGetValue(target, out var process))
            {
                var id = Add(process);
                OnSpawn?.Invoke(process, id);
            }
        }

        /// <summary>Nombre e identificador de cada proceso que aparece al lanzar.</summary>
        public Action<string, int>? OnSpawn;
    }

    /// <summary>Ventanas falsas por proceso; el estado de cada una es lo último que se le aplicó.</summary>
    private sealed class FakeWindows : IWindowHost
    {
        public sealed class Win
        {
            public int Pid;
            public int X, Y, Width = 800, Height = 600;
            public HangarWindowState State = HangarWindowState.Normal;
            public bool Topmost;
            public int Applied; // veces que hubo que cambiar algo
        }

        private readonly Dictionary<nint, Win> _wins = [];
        private nint _next = 1000;
        public readonly HashSet<int> ElevatedPids = [];
        public readonly List<nint> Fronted = [];

        public nint Open(int pid) { lock (_wins) { _wins[++_next] = new Win { Pid = pid }; return _next; } }
        public void Close(nint hwnd) { lock (_wins) _wins.Remove(hwnd); }
        public Win Get(nint hwnd) { lock (_wins) return _wins[hwnd]; }

        public IReadOnlyList<nint> MainWindows(int processId)
        {
            lock (_wins) return [.. _wins.Where(kv => kv.Value.Pid == processId).Select(kv => kv.Key)];
        }

        public WindowInfo? Describe(nint window)
        {
            lock (_wins) return _wins.TryGetValue(window, out var w) ? new WindowInfo(w.X, w.Y, w.Width, w.Height, w.State, w.Topmost) : null;
        }

        public WindowApplyResult Apply(nint window, HangarWindow s)
        {
            lock (_wins)
            {
                if (!_wins.TryGetValue(window, out var w)) throw new InvalidOperationException("ya no está");
                if (ElevatedPids.Contains(w.Pid)) throw new Win32Exception(5);
                var before = (w.X, w.Y, w.Width, w.Height, w.State, w.Topmost);
                var off = s.X is < -10000 or > 10000;
                if (!off) { w.X = s.X ?? w.X; w.Y = s.Y ?? w.Y; }
                w.Width = s.Width ?? w.Width; w.Height = s.Height ?? w.Height;
                w.State = s.State ?? w.State; w.Topmost = s.Topmost ?? w.Topmost;
                var changed = before != (w.X, w.Y, w.Width, w.Height, w.State, w.Topmost);
                if (changed) w.Applied++;
                return off ? WindowApplyResult.OffScreen : changed ? WindowApplyResult.Changed : WindowApplyResult.Unchanged;
            }
        }

        public bool BringToFront(nint window)
        {
            lock (_wins)
            {
                if (!_wins.TryGetValue(window, out var w)) return false;
                if (w.State == HangarWindowState.Minimized) w.State = HangarWindowState.Normal;
                Fronted.Add(window);
                return true;
            }
        }
    }

    private static HangarStep Uri(string name, string? process = null, double timeout = 0.05) =>
        new() { Name = name, Target = $"app://{name}", Process = process, TimeoutSeconds = timeout };

    private static (HangarOutcome, List<HangarStepState>) Execute(FakeHost host, params HangarStep[] steps)
    {
        var runner = new HangarRunner(host, pollMs: 5);
        var last = new HangarStepState[steps.Length];
        runner.StepChanged += (i, s, _) => last[i] = s;
        var outcome = runner.RunAsync(new HangarProfile { Steps = [.. steps] }).GetAwaiter().GetResult();
        return (outcome, [.. last]);
    }

    public static int Run()
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        // Orden, y lo que ya está abierto no se vuelve a lanzar.
        {
            var host = new FakeHost("B");
            host.Spawns["app://a"] = "A";
            host.Spawns["app://c"] = "C";
            var (outcome, states) = Execute(host, Uri("a", "A"), Uri("b", "B"), Uri("c", "C"));
            Check(outcome == HangarOutcome.Ok, "secuencia completa sin problemas");
            Check(host.Started.SequenceEqual(["app://a", "app://c"]), "lanza en orden y se salta el ya abierto");
            Check(states[1] == HangarStepState.AlreadyOpen && states[0] == HangarStepState.Ready, "estados: abierto y ya abierto");
        }

        // Un paso que falla no detiene los siguientes.
        {
            var host = new FakeHost();
            host.Spawns["app://fin"] = "FIN";
            host.Errors["app://roto"] = new InvalidOperationException("no arranca");
            var off = Uri("apagado");
            off.Enabled = false;
            var (outcome, states) = Execute(host,
                off,
                new HangarStep { Name = "falta", Target = @"C:\no\existe\nada.exe" },
                Uri("roto"),
                new HangarStep { Name = "vacio", Target = "  " },
                Uri("nunca", "NUNCA"),
                Uri("fin", "FIN"));
            Check(outcome == HangarOutcome.WithProblems, "con fallos el resultado lo dice");
            Check(states[0] == HangarStepState.Skipped && !host.Started.Contains("app://apagado"), "un paso desactivado no se lanza");
            Check(states[1] == HangarStepState.Failed && states[2] == HangarStepState.Failed && states[3] == HangarStepState.Failed,
                  "ruta inexistente, error al lanzar y ruta vacía = fallo");
            Check(states[4] == HangarStepState.TimedOut, "el proceso que no aparece agota el tiempo");
            Check(states[5] == HangarStepState.Ready, "el último se lanza pese a los fallos anteriores");
        }

        // URI sin proceso: se lanza y se sigue sin esperar a nadie; los argumentos llegan.
        {
            var host = new FakeHost();
            var step = Uri("uri");
            step.Args = "--min";
            var (outcome, states) = Execute(host, step);
            Check(outcome == HangarOutcome.Ok && states[0] == HangarStepState.Ready && host.Started[0] == "app://uri --min",
                  "URI sin proceso: se lanza una vez con sus argumentos");
        }

        // Cancelar en plena espera: no se lanza el siguiente y queda dicho.
        {
            var host = new FakeHost();
            host.Spawns["app://a"] = "A";
            var slow = Uri("a", "A");
            slow.WaitSeconds = 30;
            var runner = new HangarRunner(host, pollMs: 5);
            var last = new HangarStepState[2];
            using var cts = new CancellationTokenSource();
            runner.StepChanged += (i, s, _) => { last[i] = s; if (s == HangarStepState.Waiting) cts.CancelAfter(20); };
            var clock = Stopwatch.StartNew();
            var outcome = runner.RunAsync(new HangarProfile { Steps = [slow, Uri("b")] }, cts.Token).GetAwaiter().GetResult();
            Check(outcome == HangarOutcome.Cancelled && clock.Elapsed.TotalSeconds < 5, "cancelar corta la espera");
            Check(last[0] == HangarStepState.Cancelled && last[1] == HangarStepState.Cancelled && host.Started.Count == 1,
                  "tras cancelar no se lanza nada más");
        }

        // Prioridad de CPU: al aparecer, en lo ya abierto, y sin tocar los pasos que no la piden.
        {
            var host = new FakeHost("YA", "ADMIN");
            host.Spawns["app://nuevo"] = "NUEVO";
            host.Spawns["app://otro"] = "OTRO";
            host.Elevated.Add("ADMIN");
            var nuevo = Uri("nuevo", "NUEVO");
            nuevo.Priority = HangarPriority.High;
            nuevo.WaitSeconds = 0.01;
            var ya = Uri("ya", "YA");
            ya.Priority = HangarPriority.Idle;
            var otro = Uri("otro", "OTRO");
            var admin = Uri("admin", "ADMIN");
            admin.Priority = HangarPriority.AboveNormal;
            var uri = Uri("uri");
            uri.Priority = HangarPriority.BelowNormal;
            var runner = new HangarRunner(host, pollMs: 5, priorityWatchSeconds: 0.1);
            var last = new HangarStepState[5];
            var details = new string[5];
            var waiting = "";
            runner.StepChanged += (i, st, d) => { last[i] = st; details[i] = d; if (st == HangarStepState.Waiting) waiting = d; };
            var outcome = runner.RunAsync(new HangarProfile { Steps = [nuevo, ya, otro, admin, uri] }).GetAwaiter().GetResult();
            Check(host.Priorities.GetValueOrDefault("NUEVO") == ProcessPriorityClass.High && last[0] == HangarStepState.Ready
                  && details[0].Contains("prioridad Alta") && waiting.Contains("prioridad Alta"),
                  "prioridad: se pone al aparecer el proceso y se dice");
            Check(host.Priorities.GetValueOrDefault("YA") == ProcessPriorityClass.Idle && last[1] == HangarStepState.AlreadyOpen
                  && !host.Started.Contains("app://ya"),
                  "prioridad: también a lo que ya estaba abierto (sin relanzarlo)");
            Check(!host.Priorities.ContainsKey("OTRO") && last[2] == HangarStepState.Ready, "sin prioridad en el paso no se toca");
            Check(last[3] == HangarStepState.Warning && details[3].Contains("acceso denegado"), "proceso elevado: aviso de acceso denegado");
            Check(last[4] == HangarStepState.Warning && host.Started.Contains("app://uri"), "URI sin proceso: se lanza igual y avisa de que no hay prioridad");
            Check(outcome == HangarOutcome.WithProblems, "un aviso de prioridad cuenta como problema");
            foreach (var n in new[] { "NUEVO", "YA", "OTRO", "ADMIN" }) host.Close(n);
            Check(runner.Watching.Wait(5000), "la vigilancia de prioridad termina sola al cerrarse los programas");
        }

        // DCS: launcher que se relanza ("--restarted") y que luego fija él mismo su prioridad. Se
        // mantiene en todos los procesos mientras haya alguno; uno que aparece tarde, también.
        {
            var host = new FakeHost();
            host.Spawns["app://dcs"] = "DCS";
            var dcs = Uri("dcs", "DCS");
            dcs.Priority = HangarPriority.AboveNormal;
            var tarde = Uri("tarde", "TARDE");
            tarde.Priority = HangarPriority.High;
            var runner = new HangarRunner(host, pollMs: 5, priorityWatchSeconds: 0.6);
            var details = new List<string>();
            runner.StepChanged += (i, st, d) => { lock (details) details.Add($"{i}:{st}:{d}"); };
            var outcome = runner.RunAsync(new HangarProfile { Steps = [dcs, tarde] }).GetAwaiter().GetResult();
            var first = host.FindProcesses("DCS")[0];
            Check(host.PriorityOf(first) == ProcessPriorityClass.AboveNormal, "reinicio: el primer proceso la recibe");
            Thread.Sleep(100);
            var second = host.Restart("DCS");
            var late = host.Add("TARDE");
            Thread.Sleep(150);
            Check(host.PriorityOf(second) == ProcessPriorityClass.AboveNormal
                  && details.Any(d => d.StartsWith("0:Ready") && d.Contains("se relanzó")),
                  "relanzado: el proceso nuevo la recibe y se dice");
            Check(outcome == HangarOutcome.WithProblems && host.PriorityOf(late) == ProcessPriorityClass.High
                  && details.Any(d => d.StartsWith("1:Ready") && d.Contains("tarde")),
                  "un proceso que aparece tras el tiempo máximo la recibe igual");
            host.SetByHand(second, ProcessPriorityClass.Normal); // DCS la fija al arrancar
            Thread.Sleep(150);
            Check(host.PriorityOf(second) == ProcessPriorityClass.AboveNormal
                  && details.Count(d => d.Contains("la cambió; se mantiene")) == 1,
                  "si el programa la cambia, se vuelve a poner (y se avisa una vez)");
            Thread.Sleep(1000); // más que priorityWatchSeconds, pero con los procesos abiertos
            Check(!runner.Watching.IsCompleted, "mientras siga abierto se sigue vigilando");
            host.Close("DCS");
            host.Close("TARDE");
            Check(runner.Watching.Wait(5000), "cerrado el programa, la vigilancia acaba tras el margen");
            var after = host.Add("DCS");
            Thread.Sleep(80);
            Check(host.PriorityOf(after) == ProcessPriorityClass.Normal, "acabada la vigilancia ya no se toca nada");
        }

        // Parar la vigilancia (cerrar la app, otra secuencia) la termina enseguida.
        {
            var host = new FakeHost();
            host.Spawns["app://a"] = "A";
            var a = Uri("a", "A");
            a.Priority = HangarPriority.High;
            var runner = new HangarRunner(host, pollMs: 5, priorityWatchSeconds: 60);
            runner.RunAsync(new HangarProfile { Steps = [a] }).GetAwaiter().GetResult();
            var watching = !runner.Watching.IsCompleted;
            runner.StopWatching();
            Check(watching && runner.Watching.Wait(2000), "StopWatching termina la vigilancia");
        }

        // Ventanas: estado/posición al aparecer, también en lo ya abierto; nada en los pasos sin "window".
        {
            var host = new FakeHost("YA", "ADMIN");
            var wins = new FakeWindows();
            host.Spawns["app://sh"] = "SH";
            host.Spawns["app://otro"] = "OTRO";
            host.OnSpawn = (_, pid) => wins.Open(pid);
            var ya = wins.Open(host.FindProcesses("YA")[0]);
            wins.Get(ya).State = HangarWindowState.Maximized;
            var admin = wins.Open(host.FindProcesses("ADMIN")[0]);
            wins.ElevatedPids.Add(host.FindProcesses("ADMIN")[0]);
            var sh = Uri("sh", "SH");
            sh.Window = new HangarWindow { State = HangarWindowState.Minimized };
            var yaStep = Uri("ya", "YA");
            yaStep.Window = new HangarWindow { X = -1920, Y = 100, Width = 1280, Height = 720, State = HangarWindowState.Normal, Topmost = true };
            var otro = Uri("otro", "OTRO");
            var adminStep = Uri("admin", "ADMIN");
            adminStep.Window = new HangarWindow { State = HangarWindowState.Minimized };
            var fuera = Uri("fuera");
            fuera.Window = new HangarWindow { State = HangarWindowState.Minimized };
            var runner = new HangarRunner(host, pollMs: 5, priorityWatchSeconds: 0.1, windows: wins, windowWatchSeconds: 0.3);
            var last = new HangarStepState[5];
            var details = new string[5];
            runner.StepChanged += (i, st, d) => { lock (details) { last[i] = st; details[i] = d; } };
            var outcome = runner.RunAsync(new HangarProfile { Steps = [sh, yaStep, otro, adminStep, fuera] }).GetAwaiter().GetResult();
            Check(runner.Watching.Wait(5000), "ventanas: la vigilancia acaba sola tras el plazo");
            var shWin = wins.Get(wins.MainWindows(host.FindProcesses("SH")[0])[0]);
            Check(shWin.State == HangarWindowState.Minimized && last[0] == HangarStepState.Ready && details[0].Contains("ventana minimizada"),
                  $"ventanas: la del programa lanzado queda minimizada y se dice ({details[0]})");
            var y = wins.Get(ya);
            Check(y.X == -1920 && y.Y == 100 && y.Width == 1280 && y.Height == 720 && y.State == HangarWindowState.Normal && y.Topmost
                  && details[1].Contains("en -1920,100 de 1280×720") && details[1].Contains("siempre encima"),
                  "ventanas: lo ya abierto también se coloca (monitor a la izquierda, sin maximizar, siempre encima)");
            Check(wins.Get(wins.MainWindows(host.FindProcesses("OTRO")[0])[0]).Applied == 0, "ventanas: sin \"window\" no se toca");
            Check(last[3] == HangarStepState.Warning && details[3].Contains("acceso denegado"), "ventanas: programa elevado = aviso");
            Check(last[4] == HangarStepState.Warning && details[4].Contains("colocar la ventana"), "ventanas: sin \"process\" = aviso");
            Check(outcome == HangarOutcome.WithProblems, "ventanas: los avisos cuentan como problema");
        }

        // Ventanas: una que aparece tarde (relanzado) se coloca; pasados unos segundos ya no se pelea
        // con lo que se cambie a mano; una posición fuera de las pantallas no se aplica.
        {
            var host = new FakeHost();
            var wins = new FakeWindows();
            host.Spawns["app://dcs"] = "DCS";
            host.Spawns["app://lejos"] = "LEJOS";
            host.OnSpawn = (_, pid) => wins.Open(pid);
            var dcs = Uri("dcs", "DCS");
            dcs.Window = new HangarWindow { X = 10, Y = 20 };
            var lejos = Uri("lejos", "LEJOS");
            lejos.Window = new HangarWindow { X = 50000, Y = 0, Topmost = true };
            var runner = new HangarRunner(host, pollMs: 5, priorityWatchSeconds: 0.1, windows: wins, windowWatchSeconds: 1.5);
            var details = new List<string>();
            runner.StepChanged += (i, st, d) => { lock (details) details.Add($"{i}:{st}:{d}"); };
            runner.RunAsync(new HangarProfile { Steps = [dcs, lejos] }).GetAwaiter().GetResult();
            Thread.Sleep(150); // > settle (12 × 5 ms)
            var first = wins.MainWindows(host.FindProcesses("DCS")[0])[0];
            Check(wins.Get(first).X == 10 && wins.Get(first).Y == 20, "ventanas: posición sin tamaño (se conserva el tamaño)");
            wins.Get(first).X = 500; // a mano
            var second = wins.Open(host.Restart("DCS"));
            Thread.Sleep(150);
            Check(wins.Get(second).X == 10, "ventanas: la del programa relanzado también se coloca");
            Check(wins.Get(first).X == 500, "ventanas: pasado el arranque no se corrige lo que se cambia a mano");
            var far = wins.Get(wins.MainWindows(host.FindProcesses("LEJOS")[0])[0]);
            Check(far.X == 0 && far.Topmost && details.Any(d => d.StartsWith("1:Warning") && d.Contains("ninguna pantalla")),
                  "ventanas: fuera de las pantallas no se mueve (lo demás sí) y se avisa");
            runner.StopWatching();
            Check(runner.Watching.Wait(2000), "ventanas: StopWatching termina la vigilancia");
        }

        // Foco: el último paso marcado, al acabar la secuencia, la ventana más reciente, una vez.
        {
            var host = new FakeHost();
            var wins = new FakeWindows();
            host.Spawns["app://a"] = "A";
            host.Spawns["app://sim"] = "SIM";
            host.Spawns["app://aux"] = "AUX";
            host.OnSpawn = (_, pid) => wins.Open(pid);
            var a = Uri("a", "A");
            a.Focus = true; // no cuenta: hay otro marcado después
            var sim = Uri("sim", "SIM");
            sim.Focus = true;
            sim.Window = new HangarWindow { State = HangarWindowState.Minimized }; // al traerlo al frente se restaura
            var aux = Uri("aux", "AUX");
            aux.WaitSeconds = 0.1;
            var runner = new HangarRunner(host, pollMs: 5, priorityWatchSeconds: 0.1, windows: wins, windowWatchSeconds: 0.3);
            var details = new string[3];
            var frontedBeforeEnd = -1;
            runner.StepChanged += (i, st, d) => { lock (details) details[i] = d; };
            var task = runner.RunAsync(new HangarProfile { Steps = [a, sim, aux] });
            while (!task.IsCompleted) { lock (wins.Fronted) if (wins.Fronted.Count > 0 && frontedBeforeEnd < 0) frontedBeforeEnd = wins.Fronted.Count; Thread.Sleep(2); }
            Check(runner.Watching.Wait(5000), "foco: la vigilancia acaba sola");
            var simWin = wins.MainWindows(host.FindProcesses("SIM")[0])[0];
            Check(frontedBeforeEnd < 0 && wins.Fronted.SequenceEqual([simWin]) && details[1].Contains("traído al frente"),
                  "foco: solo el último marcado, al acabar la secuencia y una vez");
        }

        // Foco: si su ventana aún no existe al acabar, se trae al frente cuando aparece; cancelar = nada.
        {
            var host = new FakeHost();
            var wins = new FakeWindows();
            host.Spawns["app://sim"] = "SIM"; // sin ventana todavía
            var sim = Uri("sim", "SIM");
            sim.Focus = true;
            var runner = new HangarRunner(host, pollMs: 5, priorityWatchSeconds: 0.1, windows: wins, windowWatchSeconds: 2);
            runner.RunAsync(new HangarProfile { Steps = [sim] }).GetAwaiter().GetResult();
            Thread.Sleep(50);
            var none = wins.Fronted.Count == 0;
            var late = wins.Open(host.FindProcesses("SIM")[0]);
            Thread.Sleep(100);
            Check(none && wins.Fronted.SequenceEqual([late]), "foco: la ventana que aparece tras la secuencia se trae al frente");
            runner.StopWatching();

            var host2 = new FakeHost();
            var wins2 = new FakeWindows();
            host2.Spawns["app://sim"] = "SIM";
            host2.OnSpawn = (_, pid) => wins2.Open(pid);
            var sim2 = Uri("sim", "SIM");
            sim2.Focus = true;
            var slow = Uri("lento", "LENTO");
            host2.Spawns["app://lento"] = "LENTO";
            slow.WaitSeconds = 30;
            var runner2 = new HangarRunner(host2, pollMs: 5, priorityWatchSeconds: 0.1, windows: wins2, windowWatchSeconds: 2);
            using var cts = new CancellationTokenSource();
            runner2.StepChanged += (_, st, _) => { if (st == HangarStepState.Waiting) cts.CancelAfter(20); };
            runner2.RunAsync(new HangarProfile { Steps = [sim2, slow] }, cts.Token).GetAwaiter().GetResult();
            Check(runner2.Watching.Wait(2000) && wins2.Fronted.Count == 0, "foco: tras cancelar no se trae nada al frente");
        }

        // Nombre del proceso.
        Check(HangarRunner.ProcessNameOf(new HangarStep { Target = @"C:\x\MOZA Cockpit.exe" }) == "MOZA Cockpit", "proceso deducido de la ruta");
        Check(HangarRunner.ProcessNameOf(new HangarStep { Target = "steam://rungameid/1" }) is null, "una URI no da proceso");
        Check(HangarRunner.ProcessNameOf(new HangarStep { Target = "steam://x", Process = "Juego.exe" }) == "Juego", "el proceso indicado manda, sin .exe");

        // Inspeccionar no lanza nada.
        {
            var host = new FakeHost("A");
            var runner = new HangarRunner(host);
            Check(runner.Inspect(Uri("a", "A")) == HangarPresence.Open, "inspección: abierto");
            Check(runner.Inspect(Uri("b", "B")) == HangarPresence.Closed, "inspección: cerrado");
            Check(runner.Inspect(Uri("c")) == HangarPresence.Unknown, "inspección: URI sin proceso = no se sabe");
            Check(runner.Inspect(new HangarStep { Target = @"C:\no\existe.exe" }) == HangarPresence.Missing, "inspección: no existe");
            Check(host.Started.Count == 0, "inspeccionar no lanza nada");
        }

        // Directorio de trabajo = carpeta del programa; %VARIABLES% expandidas.
        {
            var host = new FakeHost();
            var cmd = new HangarStep { Target = @"%SystemRoot%\System32\cmd.exe", Process = "proceso-que-no-existe", TimeoutSeconds = 0 };
            Execute(host, cmd);
            Check(host.LastWorkingDir?.EndsWith(@"\System32", StringComparison.OrdinalIgnoreCase) == true && !host.Started[0].Contains('%'),
                  "expande variables y usa la carpeta del programa");
        }

        // Archivo: ida y vuelta.
        {
            var dir = Directory.CreateTempSubdirectory().FullName;
            var path = Path.Combine(dir, "hangar.json");
            Check(HangarFile.Load(path).Hangars.Count == 0, "sin archivo = vacío");
            var file = new HangarFile { Hangars = [new HangarProfile { Name = "Ñu", StartRun = true, Steps = [new HangarStep { Name = "x", Target = @"C:\a b\x.exe", WaitSeconds = 2.5, Enabled = false, Priority = HangarPriority.AboveNormal }, new HangarStep { Name = "y", Target = "y" }] }] };
            file.Save(path);
            file.Save(path);
            var again = HangarFile.Load(path);
            var s = again.Hangars[0].Steps[0];
            Check(again.Hangars[0].Name == "Ñu" && again.Hangars[0].StartRun && s.Target == @"C:\a b\x.exe" && s.WaitSeconds == 2.5 && !s.Enabled && s.TimeoutSeconds == 30,
                  "guardar y volver a leer da lo mismo");
            Check(File.Exists(path + ".bak") && !File.Exists(path + ".tmp"), "deja copia .bak y no deja el temporal");
            Check(File.ReadAllText(path).Contains("Ñu"), "acentos tal cual en el archivo");
            Check(s.Priority == HangarPriority.AboveNormal && again.Hangars[0].Steps[1].Priority is null
                  && File.ReadAllText(path).Contains("\"priority\": \"aboveNormal\"") && File.ReadAllText(path).Split("priority").Length == 2,
                  "prioridad: se guarda como texto y solo en los pasos que la tienen");
            File.WriteAllText(path, """{ "hangars": [ { "name": "a", "steps": [ { "name": "x", "target": "x", "priority": "HIGH" } ] } ] }""");
            Check(HangarFile.Load(path).Hangars[0].Steps[0].Priority == HangarPriority.High, "prioridad escrita a mano: sin importar mayúsculas");
            foreach (var bad in new[] { "\"realtime\"", "4" })
            {
                File.WriteAllText(path, $$"""{ "hangars": [ { "name": "a", "steps": [ { "name": "x", "target": "x", "priority": {{bad}} } ] } ] }""");
                var rejected = false;
                try { HangarFile.Load(path); } catch (Exception) { rejected = true; }
                Check(rejected, $"prioridad {bad} no se acepta (tiempo real y números fuera)");
            }
            var withWindow = new HangarFile { Hangars = [new HangarProfile { Name = "v", Steps = [
                new HangarStep { Name = "x", Target = "x", Focus = true, Window = new HangarWindow { State = HangarWindowState.Maximized, X = -1920, Y = 0, Width = 1920, Height = 1080, Topmost = false } },
                new HangarStep { Name = "y", Target = "y" }] }] };
            withWindow.Save(path);
            var text = File.ReadAllText(path);
            var w = HangarFile.Load(path).Hangars[0].Steps;
            Check(w[0].Focus && w[0].Window is { State: HangarWindowState.Maximized, X: -1920, Y: 0, Width: 1920, Height: 1080, Topmost: false }
                  && !w[1].Focus && w[1].Window is null && text.Contains("\"state\": \"maximized\"")
                  && text.Split("\"focus\"").Length == 2 && text.Split("\"window\"").Length == 2,
                  "ventana y foco: se guardan y se leen, y solo en los pasos que los tienen");
            foreach (var bad in new[] { """{ "x": 1 }""", """{ "width": 100 }""", """{ "width": 0, "height": 10 }""", """{ "state": "hidden" }""" })
            {
                File.WriteAllText(path, $$"""{ "hangars": [ { "name": "a", "steps": [ { "name": "x", "target": "x", "window": {{bad}} } ] } ] }""");
                var rejected = false;
                try { HangarFile.Load(path); } catch (Exception) { rejected = true; }
                Check(rejected, $"ventana {bad} no se acepta");
            }
            File.WriteAllText(path, "{ roto");
            var threw = false;
            try { HangarFile.Load(path); } catch (Exception) { threw = true; }
            Check(threw, "un JSON roto da error (no se trata como vacío)");
            Directory.Delete(dir, true);
        }

        // El hangar de ejemplo del repo es válido.
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "RigControl.slnx"))) d = d.Parent;
            var sample = HangarFile.Load(Path.Combine(d!.FullName, "profiles", "hangar.json"));
            Check(sample.Hangars.Count >= 2 && sample.Hangars.All(h => h.Steps.Count > 0 && h.Steps.All(s => s.Name != "" && s.Target != "" && HangarRunner.ProcessNameOf(s) is not null)),
                  $"profiles/hangar.json: {sample.Hangars.Count} hangares, todos los pasos con proceso detectable");
        }

        // Una ventana real, transparente y sin activar (no se ve ni roba el foco), con el WindowHost de verdad.
        {
            var hwnd = CreateWindowEx(WsExLayered | WsExNoActivate | WsExAppWindow, "STATIC", "RigControl prueba de ventana",
                                      WsPopup | WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox, 200, 200, 400, 300, 0, 0, 0, 0);
            try
            {
                SetLayeredWindowAttributes(hwnd, 0, 0, 2); // alfa 0
                ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
                var real = new WindowHost();
                var mine = real.MainWindows(Environment.ProcessId);
                Check(mine.Contains(hwnd), "ventana real: aparece entre las ventanas principales del proceso");
                var moved = real.Apply(hwnd, new HangarWindow { X = 50, Y = 60, Width = 500, Height = 350 });
                var d = real.Describe(hwnd);
                Check(moved == WindowApplyResult.Changed && d is { X: 50, Y: 60, Width: 500, Height: 350, State: HangarWindowState.Normal },
                      $"ventana real: se mueve y redimensiona ({d})");
                Check(real.Apply(hwnd, new HangarWindow { X = 50, Y = 60, Width = 500, Height = 350 }) == WindowApplyResult.Unchanged,
                      "ventana real: aplicar otra vez no cambia nada");
                real.Apply(hwnd, new HangarWindow { State = HangarWindowState.Minimized });
                d = real.Describe(hwnd);
                Check(d is { State: HangarWindowState.Minimized, Width: 500, Height: 350 }, $"ventana real: minimizada conserva su tamaño normal ({d})");
                real.Apply(hwnd, new HangarWindow { X = 70, Y = 80, State = HangarWindowState.Minimized });
                d = real.Describe(hwnd);
                Check(d is { State: HangarWindowState.Minimized, X: 70, Y: 80 }, $"ventana real: se mueve estando minimizada y sigue minimizada ({d})");
                real.Apply(hwnd, new HangarWindow { State = HangarWindowState.Maximized });
                Check(real.Describe(hwnd)?.State == HangarWindowState.Maximized, "ventana real: maximizada");
                real.Apply(hwnd, new HangarWindow { State = HangarWindowState.Normal, Topmost = true });
                d = real.Describe(hwnd);
                Check(d is { State: HangarWindowState.Normal, Topmost: true, X: 70, Y: 80 }, $"ventana real: normal y siempre encima ({d})");
                real.Apply(hwnd, new HangarWindow { Topmost = false });
                Check(real.Describe(hwnd)?.Topmost == false, "ventana real: se le quita «siempre encima»");
                Check(real.Apply(hwnd, new HangarWindow { X = 90000, Y = 90000 }) == WindowApplyResult.OffScreen && real.Describe(hwnd)?.X == 70,
                      "ventana real: fuera de las pantallas no se mueve");
            }
            finally
            {
                DestroyWindow(hwnd);
            }
            Check(new WindowHost().Describe(hwnd) is null, "ventana real: cerrada, Describe da null");
        }

        // Un proceso real, oculto: ping a sí mismo.
        {
            var runner = new HangarRunner(new ProcessHost(ProcessWindowStyle.Hidden), pollMs: 20);
            var state = HangarStepState.Failed;
            runner.StepChanged += (_, s, _) => state = s;
            var ping = new HangarStep { Name = "ping", Target = @"%SystemRoot%\System32\PING.EXE", Args = "-n 2 127.0.0.1", TimeoutSeconds = 5 };
            var outcome = runner.RunAsync(new HangarProfile { Steps = [ping] }).GetAwaiter().GetResult();
            Check(outcome == HangarOutcome.Ok && state == HangarStepState.Ready, "proceso real: se lanza y se detecta");
        }

        // Un proceso real con prioridad: ping a sí mismo durante ~3 s, en Baja.
        {
            var runner = new HangarRunner(new ProcessHost(ProcessWindowStyle.Hidden), pollMs: 20, priorityWatchSeconds: 0.5);
            var state = HangarStepState.Failed;
            var detail = "";
            runner.StepChanged += (_, s, d) => { state = s; detail = d; };
            foreach (var p in Process.GetProcessesByName("PING")) using (p) p.WaitForExit(5000); // el ping de la prueba anterior
            var before = Process.GetProcessesByName("PING").Select(p => p.Id).ToHashSet();
            var ping = new HangarStep { Name = "ping", Target = @"%SystemRoot%\System32\PING.EXE", Args = "-n 4 127.0.0.1", TimeoutSeconds = 5, Priority = HangarPriority.Idle };
            var outcome = runner.RunAsync(new HangarProfile { Steps = [ping] }).GetAwaiter().GetResult();
            var mine = Process.GetProcessesByName("PING").Where(p => !before.Contains(p.Id)).ToList();
            var real = mine.Count > 0 ? mine[0].PriorityClass : (ProcessPriorityClass?)null;
            foreach (var p in mine) p.Dispose();
            Check(outcome == HangarOutcome.Ok && state == HangarStepState.Ready && real == ProcessPriorityClass.Idle,
                  $"proceso real: prioridad puesta de verdad ({real}; {detail})");
        }

        return fails;
    }

    private const uint WsPopup = 0x80000000, WsCaption = 0xC00000, WsThickFrame = 0x40000, WsMinimizeBox = 0x20000, WsMaximizeBox = 0x10000;
    private const uint WsExLayered = 0x80000, WsExNoActivate = 0x8000000, WsExAppWindow = 0x40000;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int w, int h,
                                              nint parent, nint menu, nint instance, nint param);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int cmd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);
}
