using System.Globalization;
using System.Text.Json.Nodes;
using RigControl.Core.Graphics;

namespace RigControl.Tests;

/// <summary>
/// Optimizador VR con un DCS falso: el tiempo de fotograma sale de un modelo de coste de los ajustes de cada
/// prueba y se ajusta al ritmo de un visor a 72 Hz (si no llega, cae a 36, como con Smart Smoothing).
/// </summary>
internal static class OptimizerTests
{
    /// <summary>DCS falso con varias pruebas por arranque.</summary>
    private sealed class FakeVrDcs(string savedGames, Func<IReadOnlyDictionary<string, double>, double> renderMs) : IDcsLauncher
    {
        public int Launches;
        /// <summary>Tiempo de fotograma fijo (p. ej. 137 ms = visor apagado), en lugar del modelo.</summary>
        public double? ForceMs;
        public List<int> TrialsPerLaunch = [];

        public bool IsRunning() => false;

        public IDcsProcess Start(string exe, IReadOnlyList<string> args, string workingDirectory)
        {
            Launches++;
            var config = Path.Combine(savedGames, "Config");
            var directive = JsonNode.Parse(File.ReadAllText(Path.Combine(config, "rigcontrol_bench_directive.json")))!;
            var trials = directive["trials"]?.AsArray() ?? [];
            TrialsPerLaunch.Add(trials.Count);
            // Los ajustes de partida (options.lua) + los de cada prueba (como hace el script con optionsEditor).
            var options = new LuaOptionsFile(File.ReadAllText(Path.Combine(config, "options.lua")));
            var parts = new List<string>();
            foreach (var t in trials)
            {
                var values = new Dictionary<string, double>();
                foreach (var key in options.Keys.Where(k => k.StartsWith("graphics.")))
                    if (DcsSettingsCatalog.Find(key) is { } s) values[key] = s.IndexOf(options.Get(key));
                foreach (var (k, v) in t!["set"]!.AsObject())
                {
                    var s = DcsSettingsCatalog.Find("graphics." + k)!;
                    var lua = v!.GetValueKind() == System.Text.Json.JsonValueKind.String ? $"\"{v.GetValue<string>()}\"" : v.ToJsonString();
                    values["graphics." + k] = s.IndexOf(lua);
                }
                var ms = renderMs(values);
                var paced = ForceMs ?? (ms <= 1000.0 / 72 ? 1000.0 / 72 : 1000.0 / 36);   // visor a 72 Hz con Smart Smoothing
                var frames = string.Join(",", Enumerable.Range(0, 200).Select(i => (paced + (i % 7) * 0.01).ToString("0.000", CultureInfo.InvariantCulture)));
                var scenes = string.Join(",", DcsBenchmark.Scenes.Select(sc => $"{{\"name\":\"{sc.Name}\",\"frame_ms\":[{frames}]}}"));
                parts.Add($"{{\"tag\":\"{t["tag"]}\",\"scenes\":[{scenes}]}}");
            }
            Directory.CreateDirectory(Path.Combine(savedGames, "Logs"));
            File.WriteAllText(Path.Combine(savedGames, "Logs", "dcs.log"), "OpenXR: Creating swapchain for view 0");
            File.WriteAllText(Path.Combine(config, "rigcontrol_bench_result.json"),
                $"{{\"ok\":true,\"reason\":\"\",\"scenes\":[],\"trials\":[{string.Join(",", parts)}]}}");
            return new Proc();
        }

        private sealed class Proc : IDcsProcess
        {
            public bool WaitForExit(int milliseconds) => true;
            public void Kill() { }
            public void Dispose() { }
        }
    }

    private const string Options = "options = {\n\t[\"VR\"] = {\n\t\t[\"openxr_quadView\"] = true,\n\t},\n\t[\"graphics\"] = {\n" +
        "\t\t[\"shadows\"] = 2,\n\t\t[\"flatTerrainShadows\"] = 0,\n\t\t[\"clouds\"] = 1,\n\t\t[\"visibRange\"] = \"High\",\n\t\t[\"water\"] = 1,\n" +
        "\t\t[\"SSAO\"] = 0,\n\t\t[\"SSLR\"] = 0,\n\t\t[\"SSS\"] = 1,\n\t\t[\"forestDistanceFactor\"] = 0.5,\n\t\t[\"clutterMaxDistance\"] = 1000,\n" +
        "\t\t[\"textures\"] = 1,\n\t\t[\"terrainTextures\"] = \"max\",\n\t\t[\"Upscaling\"] = \"DLSS\",\n\t\t[\"DLSS_PerfQuality\"] = 1,\n\t},\n}\n";

    public static int Run()
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        // Candidatos.
        var current = new LuaOptionsFile(Options).Keys.ToDictionary(k => k, k => new LuaOptionsFile(Options).Get(k)!);
        var up = VrOptimizer.Candidates(current, up: true);
        Check(up.All(c => !c.Setting.Restart) && up.Any(c => c.Setting.Key == "graphics.SSAO" && c.To == 1)
              && !up.Any(c => c.Setting.Key == "graphics.flatTerrainShadows"),
              "subir: un nivel de cada ajuste sin reinicio (ni texturas ni lo que ya está al máximo)");
        Check(up.Zip(up.Skip(1)).All(p => p.First.Score >= p.Second.Score), "subir: ordenados por calidad ganada / coste");
        var down = VrOptimizer.Candidates(current, up: false);
        Check(down[0].Setting.Key == "graphics.flatTerrainShadows", $"bajar: primero lo que más FPS da por calidad perdida ({down[0].Setting.Key})");
        Check(DcsSettingsCatalog.Find("graphics.forestDistanceFactor")!.IndexOf("0.500") == 1 && DcsSettingsCatalog.Find("graphics.visibRange")!.IndexOf("\"High\"") == 2,
              "nivel actual: números y textos de options.lua");
        Check(VrOptimizer.Holds(new FrameStats(100, 72, 70, 13.9, 15, 0, 0.5), 72) && !VrOptimizer.Holds(new FrameStats(100, 50, 36, 27.8, 28, 0, 90), 72),
              "«llega»: casi todo a tiempo y el 1 % bajo cerca del refresco");

        // Refresco desde el ritmo.
        Check(PimaxInfo.DetectRefresh(14.11, 90, null, DateTime.Now) == 72 && PimaxInfo.DetectRefresh(11.2, 72, null, DateTime.Now) == 90
              && PimaxInfo.DetectRefresh(27.8, 90, null, DateTime.Now) == 72
              && PimaxInfo.DetectRefresh(20, 90, null, DateTime.Now) == 90, "refresco detectado por el ritmo (14,1 ms = 72 Hz)");
        // Como el 17/11/2026 a 90 Hz: una escena a ~87 FPS y dos a 45 (mediana 17 ms ≈ mitad de 120): debe dar 90.
        List<double> mixed = [.. Enumerable.Range(0, 2158).Select(i => 11.1 + (i % 10) * 0.1), .. Enumerable.Range(0, 2266).Select(i => 22.2 + (i % 5) * 0.05)];
        Check(PimaxInfo.DetectRefresh(mixed, 120, null, DateTime.Now) == 90, "refresco por el ritmo con escenas a 90 y a 45 = 90 (no 120)");
        var pimax = new PimaxSession(72, true, "4242x5102", 39.5, 65.8, DateTime.Now);
        Check(PimaxInfo.DetectRefresh(11.1, 90, pimax, DateTime.Now.AddMinutes(-5)) == 72, "refresco: manda el de Pimax si es de esta medición");

        // Registro de Pimax.
        var logDir = Path.Combine(Path.GetTempPath(), $"rigcontrol-pimax-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, "PiService__2026-11-15-16.log"),
            "2026-11-15 16:43:24.997 I  5756 [rformanceSessionData] Performance body to send: {\"game_info\":{\"client_process\":\"DCS\",\"client_resolution\":\"4242x5102\"," +
            "\"fps_analysis\":{\"fps_target\":72},\"gpu_analysis\":{\"avg_gpu_usage\":65.8,\"gpu_level_ratio\":{\"80_100\":0.3952}}," +
            "\"pimax_play_config\":{\"int_configs\":{\"runtime_dbg_asw_enable\":1}}}}\n");
        var s = PimaxInfo.LastSession(logDir: logDir);
        Check(s is { TargetFps: 72, SmartSmoothing: true, Resolution: "4242x5102" } && Math.Abs(s.GpuOver80Percent - 39.5) < 0.1,
              "registro de Pimax: refresco 72, Smart Smoothing, resolución y GPU > 80 %");

        // Pantalla del visor apagada (sensor de proximidad), como el 17/11/2026: se apaga a mitad y no vuelve.
        File.WriteAllText(Path.Combine(logDir, "PiService__2026-11-17-19.log"),
            "2026-11-17 19:24:33.365 I  5712 [::OnScreenOnOffTimer] [LogEventThread] HMD screen state changed to false\n" +
            "2026-11-17 19:24:37.917 I  5712 [::OnScreenOnOffTimer] [LogEventThread] HMD screen state changed to true\n" +
            "2026-11-17 19:27:54.099 I  5712 [::OnScreenOnOffTimer] [LogEventThread] HMD screen state changed to false\n");
        DateTime T(string hms) => DateTime.ParseExact("2026-11-17 " + hms, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Check(PimaxInfo.ScreenOff(T("19:26:17"), T("19:29:40"), logDir) == T("19:27:54").AddMilliseconds(99)
              && PimaxInfo.ScreenOff(T("19:31:10"), T("19:42:00"), logDir) == T("19:31:10")
              && PimaxInfo.ScreenOff(T("19:24:30"), T("19:27:00"), logDir) is null
              && PimaxInfo.ScreenOff(T("19:25:00"), T("19:27:00"), logDir) is null,
              "registro de Pimax: pantalla apagada a mitad o desde antes; el apagón de 4,5 s al arrancar Pimax no cuenta");
        Directory.Delete(logDir, true);

        // Optimizar con un DCS falso.
        var root = Path.Combine(Path.GetTempPath(), $"rigcontrol-opt-{Guid.NewGuid():N}");
        var saved = Path.Combine(root, "DCS.openbeta");
        Directory.CreateDirectory(Path.Combine(saved, "Config"));
        var exe = Path.Combine(root, "DCS.exe");
        File.WriteAllText(exe, "");
        File.WriteAllText(Path.Combine(saved, "Config", "options.lua"), Options);
        try
        {
            // Con margen: base 11 ms + coste de cada nivel; caben las subidas baratas, no todas juntas.
            double Model(IReadOnlyDictionary<string, double> v) => 4.0 + v.Sum(kv =>
                DcsSettingsCatalog.Find(kv.Key) is { } st && kv.Value >= 0 ? st.Levels[(int)kv.Value].Gpu * 20 : 0);
            var dcs = new FakeVrDcs(saved, Model);
            var bench = new DcsBenchmark(saved, exe, dcs) { PollMs = 1, GraceMs = 10, PimaxLogDir = Path.Combine(root, "sin-pimax"), ResultsDirectory = Path.Combine(root, "med") };
            var report = new VrOptimizer(bench, new HeadsetInfo { RefreshHz = 90 }).Run();
            Check(report.RefreshHz == 72, "optimizar: refresco detectado = 72 aunque el configurado sea 90");
            Check(report.BaselineHolds && dcs.Launches == 2 && dcs.TrialsPerLaunch[0] == 8,
                  $"llega: un arranque con la actual + 7 pruebas y otro de combinaciones ({string.Join("+", dcs.TrialsPerLaunch)})");
            var final = report.Profile.Settings.Where(kv => DcsSettingsCatalog.Find(kv.Key) is not null)
                .ToDictionary(kv => kv.Key, kv => (double)DcsSettingsCatalog.Find(kv.Key)!.IndexOf(kv.Value));
            Check(report.Changes.Count > 0 && Model(final) <= 1000.0 / 72, $"recomienda subidas y la combinación cabe a 72 Hz ({Model(final):0.00} ms; {string.Join(", ", report.Changes.Select(c => c.Label + " " + c.To))})");
            Check(report.Tests.Any(t => t.Description.StartsWith("Juntos") && !t.Holds) || report.Changes.Count == report.Tests.Count(t => t.Holds && !t.Description.StartsWith("Juntos")) - 1,
                  "las combinaciones que no caben se descartan");
            Check(report.Profile.Settings["graphics.textures"] == "1" && report.Notes.Any(n => n.StartsWith("Texturas")) && report.Notes.Any(n => n.Contains("DLSS")),
                  "texturas y DLSS: no se tocan, solo consejo");
            Check(File.ReadAllText(Path.Combine(saved, "Config", "options.lua")) == Options, "options.lua queda como estaba");

            // Sin margen: base 15 ms: no llega; recomienda bajar.
            dcs = new FakeVrDcs(saved, v => Model(v) + 3.0);
            bench = new DcsBenchmark(saved, exe, dcs) { PollMs = 1, GraceMs = 10, PimaxLogDir = Path.Combine(root, "sin-pimax"), ResultsDirectory = Path.Combine(root, "med") };
            report = new VrOptimizer(bench, new HeadsetInfo { RefreshHz = 72 }).Run();
            Check(!report.BaselineHolds && report.Changes.Count > 0 && report.Changes.All(c => c.Reason.StartsWith("Libera")),
                  $"no llega: recomienda bajar ({string.Join(", ", report.Changes.Select(c => c.Label + " " + c.From + "→" + c.To))})");

            // Smart Smoothing fija todas las pruebas a la mitad (como a 90 Hz el 17/11/2026): no concluyente, sin cambios.
            dcs = new FakeVrDcs(saved, Model) { ForceMs = 1000.0 / 36 };
            bench = new DcsBenchmark(saved, exe, dcs) { PollMs = 1, GraceMs = 10, PimaxLogDir = Path.Combine(root, "sin-pimax"), ResultsDirectory = Path.Combine(root, "med") };
            report = new VrOptimizer(bench, new HeadsetInfo { RefreshHz = 72 }).Run();
            Check(report.Changes.Count == 0 && report.Notes[0].StartsWith("NO CONCLUYENTE") && report.Profile.Notes.StartsWith("NO CONCLUYENTE") && dcs.Launches == 1,
                  "todas las pruebas fijas a la mitad (Smart Smoothing): no concluyente, sin cambios ni segundo arranque");

            // Visor apagado: ~7 FPS en todas las pruebas = la medición no vale (no un «sin cambios»).
            dcs = new FakeVrDcs(saved, Model) { ForceMs = 137 };
            bench = new DcsBenchmark(saved, exe, dcs) { PollMs = 1, GraceMs = 10, PimaxLogDir = Path.Combine(root, "sin-pimax"), ResultsDirectory = Path.Combine(root, "med") };
            string? error = null;
            try { new VrOptimizer(bench, new HeadsetInfo { RefreshHz = 72 }).Run(); }
            catch (InvalidOperationException ex) { error = ex.Message; }
            Check(error?.Contains("no llegaba al visor") == true && File.ReadAllText(Path.Combine(saved, "Config", "options.lua")) == Options,
                  $"visor apagado (7 FPS): error claro y options.lua intacto ({error})");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        return fails;
    }
}
