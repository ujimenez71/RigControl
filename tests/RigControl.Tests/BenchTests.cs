using System.Text.Json.Nodes;
using RigControl.Core.Graphics;

namespace RigControl.Tests;

/// <summary>
/// Medición de DCS con un DCS falso: qué se instala (script con su clave, misión, instrucciones), con qué
/// argumentos se arranca, las cifras, y que options.lua/autoexec.cfg quedan como estaban en todos los casos.
/// </summary>
internal static class BenchTests
{
    private const string Options = "options = {\n\t[\"VR\"] = {\n\t\t[\"pixel_density\"] = 1,\n\t},\n\t[\"graphics\"] = {\n\t\t[\"maxFPS\"] = 90,\n\t\t[\"shadows\"] = 4,\n\t\t[\"sync\"] = false,\n\t},\n}\n";

    /// <summary>Hace de DCS: lee el script y las instrucciones como lo haría el de verdad y escribe un resultado.</summary>
    private sealed class FakeDcs(string savedGames) : IDcsLauncher
    {
        public bool Running;
        public List<string> Args = [];
        public string? OptionsSeen, KeyInHook, KeyInDirective, MissionInDirective;
        public bool MissionInstalled;
        public Func<string>? Result = () => Frames(11.0, 300);   // null = no escribe resultado
        public bool VrOk = true;
        public bool NeverExits;

        public bool IsRunning() => Running;

        public IDcsProcess Start(string exe, IReadOnlyList<string> args, string workingDirectory)
        {
            Args = [.. args];
            var config = Path.Combine(savedGames, "Config");
            OptionsSeen = File.ReadAllText(Path.Combine(config, "options.lua"));
            var hook = File.ReadAllText(Path.Combine(savedGames, "Scripts", "Hooks", "rigcontrol_bench.lua"));
            KeyInHook = System.Text.RegularExpressions.Regex.Match(hook, "local KEY\\s*=\\s*\"([^\"]+)\"").Groups[1].Value;
            var directive = JsonNode.Parse(File.ReadAllText(Path.Combine(config, "rigcontrol_bench_directive.json")))!;
            KeyInDirective = directive["key"]!.GetValue<string>();
            MissionInDirective = directive["mission"]!.GetValue<string>();
            MissionInstalled = File.Exists(MissionInDirective) && new FileInfo(MissionInDirective).Length > 1000;
            // DCS reescribe options.lua al salir (aquí, "a peor") y deja su registro.
            File.WriteAllText(Path.Combine(config, "options.lua"), "options = { [\"graphics\"] = { [\"maxFPS\"] = 1, }, }");
            Directory.CreateDirectory(Path.Combine(savedGames, "Logs"));
            File.WriteAllText(Path.Combine(savedGames, "Logs", "dcs.log"), VrOk ? "INFO VISUALIZER: OpenXR: Creating swapchain for view 0" : "ERROR OpenXR failure: no hay visor");
            if (Result is not null) File.WriteAllText(Path.Combine(config, "rigcontrol_bench_result.json"), Result());
            return new Proc(NeverExits);
        }

        private sealed class Proc(bool never) : IDcsProcess
        {
            public bool Killed;
            public bool WaitForExit(int milliseconds) { if (never && !Killed) { Thread.Sleep(Math.Min(milliseconds, 20)); return false; } return true; }
            public void Kill() => Killed = true;
            public void Dispose() { }
        }
    }

    /// <summary>Resultado como lo escribe el script: tres escenas, ms de cada fotograma.</summary>
    private static string Frames(double ms, int count, double spikeMs = 40, int spikes = 3) =>
        "{\"ok\":true,\"reason\":\"\",\"tag\":\"rigcontrol\",\"clock\":\"DCS.getRealTime\",\"scenes\":[" +
        string.Join(",", DcsBenchmark.Scenes.Select(s =>
            $"{{\"name\":\"{s.Name}\",\"shot\":\"\",\"frame_ms\":[{string.Join(",", Enumerable.Range(0, count).Select(i => (i < spikes ? spikeMs : ms).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)))}]}}")) +
        "],\"trials\":[]}";

    public static int Run()
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        // Cifras.
        var st = FrameStats.From([.. Enumerable.Repeat(10.0, 97), 30, 30, 30], refreshHz: 90);
        Check(st.Frames == 100 && st.Stutters == 3 && Math.Abs(st.MissedPercent - 3) < 0.01 && st.MedianMs == 10,
              $"cifras: tirones (>2,5× la mediana) y fuera de tiempo a 90 Hz ({st})");
        Check(Math.Abs(st.P1LowFps - 33.3) < 0.1, "1 % bajo = FPS en el percentil 99");
        var threw = false;
        try { FrameStats.From([10, 10], 90); } catch (InvalidDataException) { threw = true; }
        Check(threw, "menos de 30 fotogramas: no vale");

        var root = Path.Combine(Path.GetTempPath(), $"rigcontrol-bench-{Guid.NewGuid():N}");
        var saved = Path.Combine(root, "DCS.openbeta");
        var config = Path.Combine(saved, "Config");
        Directory.CreateDirectory(config);
        var exe = Path.Combine(root, "bin", "DCS.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        File.WriteAllText(Path.Combine(config, "options.lua"), Options);
        File.WriteAllText(Path.Combine(config, "autoexec.cfg"), "DLSS_Preset = 'K'");
        bool Untouched() => File.ReadAllText(Path.Combine(config, "options.lua")) == Options
                            && File.ReadAllText(Path.Combine(config, "autoexec.cfg")) == "DLSS_Preset = 'K'"
                            && !File.Exists(Path.Combine(saved, "Scripts", "Hooks", "rigcontrol_bench.lua"))
                            && !File.Exists(Path.Combine(saved, "Missions", "rigcontrol_bench.miz"))
                            && !Directory.EnumerateFiles(config, "rigcontrol_bench*").Any()
                            && !Directory.EnumerateFiles(config, "*.rigcontrol-bench").Any();
        try
        {
            var dcs = new FakeDcs(saved);
            var bench = new DcsBenchmark(saved, exe, dcs) { PollMs = 5, GraceMs = 50, PimaxLogDir = Path.Combine(root, "sin-pimax"), ResultsDirectory = Path.Combine(root, "mediciones") };

            // VR con los ajustes actuales.
            var r = bench.Measure(null, vr: true, refreshHz: 90);
            Check(dcs.Args.SequenceEqual(["--no-launcher", "--force_enable_VR", "-w", "DCS.openbeta"]), $"arranque: {string.Join(' ', dcs.Args)}");
            Check(dcs.KeyInHook is { Length: 32 } && dcs.KeyInHook == dcs.KeyInDirective, "script con clave aleatoria = la de las instrucciones");
            Check(dcs.MissionInstalled && dcs.MissionInDirective == Path.Combine(saved, "Missions", "rigcontrol_bench.miz"),
                  "misión instalada donde la busca el script (lfs.writedir() + Missions\\...)");
            Check(dcs.OptionsSeen == Options, "VR: options.lua tal cual (sin quitar el límite de FPS)");
            Check(r.Scenes.Count == 3 && r.Total.Frames == 900 && r.Total.Stutters == 9 && r.Vr && r.Settings["graphics.maxFPS"] == "90",
                  $"resultado: 3 escenas, 900 fotogramas, ajustes medidos guardados ({r.Total})");
            Check(Untouched(), "después: options.lua y autoexec.cfg como estaban, sin restos");
            Check(Directory.EnumerateFiles(Path.Combine(root, "mediciones"), "*-dcs-vr-_actual_.json").Count() == 1, "medición guardada para comparar");

            // Monitor y con perfil: se quitan límite y sincronización y se aplica el perfil solo para medir.
            var profile = new GraphicsProfile { Name = "Sombras bajas", Sim = "dcs", Settings = new() { ["graphics.shadows"] = "1" } };
            r = bench.Measure(profile, vr: false, refreshHz: 90);
            Check(dcs.Args.Contains("--force_disable_VR"), "monitor: --force_disable_VR");
            Check(dcs.OptionsSeen!.Contains("[\"shadows\"] = 1") && dcs.OptionsSeen.Contains("[\"maxFPS\"] = 300") && r.Profile == "Sombras bajas",
                  "monitor + perfil: perfil aplicado y sin límite de FPS durante la medición");
            Check(Untouched(), "después: todo restaurado (también lo que reescribió DCS al salir)");

            // Fallos: siempre se restaura.
            dcs.VrOk = false;
            threw = false;
            try { bench.Measure(null, vr: true, refreshHz: 90); } catch (InvalidOperationException e) { threw = e.Message.Contains("visor"); }
            Check(threw && Untouched() && File.Exists(bench.FailureLogPath), "VR que no arrancó: no vale, se guarda su registro y se restaura");
            dcs.VrOk = true;

            dcs.Result = null;
            threw = false;
            try { bench.Measure(null, vr: true, refreshHz: 90); } catch (InvalidOperationException e) { threw = e.Message.Contains("sin escribir"); }
            Check(threw && Untouched(), "DCS se cierra sin resultado: error y restaurado");

            dcs.Result = () => "{\"ok\":false,\"reason\":\"benchmark aircraft never appeared\",\"scenes\":[]}";
            threw = false;
            try { bench.Measure(null, vr: true, refreshHz: 90); } catch (InvalidOperationException e) { threw = e.Message.Contains("never appeared"); }
            Check(threw && Untouched(), "el script se rinde: su motivo y restaurado");

            dcs.Result = null;
            dcs.NeverExits = true;
            using (var cts = new CancellationTokenSource(100))
            {
                threw = false;
                try { bench.Measure(null, vr: true, refreshHz: 90, cancel: cts.Token); } catch (OperationCanceledException) { threw = true; }
                Check(threw && Untouched(), "cancelar (Ctrl+C): se cierra DCS y se restaura");
            }
            dcs.NeverExits = false;
            dcs.Result = () => Frames(11.0, 300);

            dcs.Running = true;
            threw = false;
            try { bench.Measure(null, vr: true, refreshHz: 90); } catch (InvalidOperationException e) { threw = e.Message.Contains("abierto"); }
            Check(threw && Untouched(), "DCS ya abierto: no se toca nada");
            dcs.Running = false;

            // Corte a medias: la siguiente vez se restaura antes de nada.
            File.Copy(Path.Combine(config, "options.lua"), Path.Combine(config, "options.lua.rigcontrol-bench"));
            File.Copy(Path.Combine(config, "autoexec.cfg"), Path.Combine(config, "autoexec.cfg.rigcontrol-bench"));
            File.WriteAllText(Path.Combine(config, "rigcontrol_bench_journal.txt"), "autoexec");
            File.WriteAllText(Path.Combine(config, "options.lua"), "options = { }");
            Directory.CreateDirectory(Path.Combine(saved, "Scripts", "Hooks"));
            File.WriteAllText(Path.Combine(saved, "Scripts", "Hooks", "rigcontrol_bench.lua"), "-- resto");
            Check(bench.RecoverIfInterrupted() && Untouched(), "medición cortada: la siguiente vez se deja todo como estaba");
            Check(!bench.RecoverIfInterrupted(), "sin corte: no hace nada");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        return fails;
    }
}
