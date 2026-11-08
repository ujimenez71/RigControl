using System.Diagnostics;
using RigControl.Core.Hangar;

namespace RigControl.Tests;

/// <summary>Secuencias del Hangar con un Windows falso, más un proceso real oculto (ping).</summary>
internal static class HangarTests
{
    /// <summary>Lanzar un destino hace aparecer su proceso (o no, si no está en la tabla).</summary>
    private sealed class FakeHost(params string[] alreadyRunning) : IProcessHost
    {
        private readonly HashSet<string> _running = new(alreadyRunning, StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Started = [];
        public readonly Dictionary<string, string> Spawns = [];
        public readonly Dictionary<string, Exception> Errors = [];
        public string? LastWorkingDir;

        public bool IsRunning(string name) { lock (_running) return _running.Contains(name); }

        public void Start(string target, string? args, string? workingDirectory)
        {
            if (Errors.TryGetValue(target, out var error)) throw error;
            Started.Add(args is null ? target : $"{target} {args}");
            LastWorkingDir = workingDirectory;
            if (Spawns.TryGetValue(target, out var process)) lock (_running) _running.Add(process);
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
            var file = new HangarFile { Hangars = [new HangarProfile { Name = "Ñu", StartRun = true, Steps = [new HangarStep { Name = "x", Target = @"C:\a b\x.exe", WaitSeconds = 2.5, Enabled = false }] }] };
            file.Save(path);
            file.Save(path);
            var again = HangarFile.Load(path);
            var s = again.Hangars[0].Steps[0];
            Check(again.Hangars[0].Name == "Ñu" && again.Hangars[0].StartRun && s.Target == @"C:\a b\x.exe" && s.WaitSeconds == 2.5 && !s.Enabled && s.TimeoutSeconds == 30,
                  "guardar y volver a leer da lo mismo");
            Check(File.Exists(path + ".bak") && !File.Exists(path + ".tmp"), "deja copia .bak y no deja el temporal");
            Check(File.ReadAllText(path).Contains("Ñu"), "acentos tal cual en el archivo");
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

        // Un proceso real, oculto: ping a sí mismo.
        {
            var runner = new HangarRunner(new ProcessHost(ProcessWindowStyle.Hidden), pollMs: 20);
            var state = HangarStepState.Failed;
            runner.StepChanged += (_, s, _) => state = s;
            var ping = new HangarStep { Name = "ping", Target = @"%SystemRoot%\System32\PING.EXE", Args = "-n 2 127.0.0.1", TimeoutSeconds = 5 };
            var outcome = runner.RunAsync(new HangarProfile { Steps = [ping] }).GetAwaiter().GetResult();
            Check(outcome == HangarOutcome.Ok && state == HangarStepState.Ready, "proceso real: se lanza y se detecta");
        }

        return fails;
    }
}
