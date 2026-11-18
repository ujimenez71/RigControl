using RigControl.Core.Graphics;

namespace RigControl.Tests;

/// <summary>Sesiones del visor desde los registros de Pimax: lectura, misiones de dcs.log, veredicto e historial.</summary>
internal static class VrSessionTests
{
    private static string Body(string start, string end, int seconds, string process, int target, int asw, double median, double p5, double below50, double r7090)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string N(double d) => d.ToString(inv);
        return BodyText(start, end, seconds, process, target, asw, N(median), N(p5), N(below50), N(r7090));
    }

    private static string BodyText(string start, string end, int seconds, string process, int target, int asw, string median, string p5, string below50, string r7090) =>
        $"{start}.000 I 12856 [rformanceSessionData] Performance body to send: {{\"game_info\":{{\"client_process\":\"{process}\",\"client_resolution\":\"4242x5102\"," +
        $"\"cpu_analysis\":{{\"avg_cpu_usage\":30.2}},\"fps_analysis\":{{\"avg_fps\":{median},\"fps_level_ratio\":{{\"50_70\":0,\"70_90\":{r7090},\"above_90\":0,\"below_50\":{below50}}}," +
        $"\"fps_target\":{target},\"median_fps\":{median},\"p1_fps\":{p5},\"p5_fps\":{p5}}},\"gpu_analysis\":{{\"avg_gpu_usage\":72.7,\"gpu_level_ratio\":{{\"80_100\":0.63}}}}," +
        $"\"pimax_play_config\":{{\"int_configs\":{{\"runtime_dbg_asw_enable\":{asw}}}}},\"session_end_time\":\"{end}\",\"session_info\":{{\"session_duration_seconds\":{seconds}}}," +
        $"\"session_start_time\":\"{start}\"}}}}\n";

    public static int Run()
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        var root = Path.Combine(Path.GetTempPath(), $"rigcontrol-vuelos-{Guid.NewGuid():N}");
        var logs = Path.Combine(root, "pimax");
        var saved = Path.Combine(root, "DCS.openbeta");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(Path.Combine(saved, "Logs"));
        try
        {
            // Dos archivos de PiService: un vuelo de DCS a 36 FPS (Smart Smoothing), uno que llega, uno de MSFS y una sesión corta.
            File.WriteAllText(Path.Combine(logs, "PiService__2026-11-15-16.log"),
                "2026-11-15 16:00:00.000 I 1 [x] otra línea cualquiera\n" +
                Body("2026-11-15 17:58:24", "2026-11-15 18:33M:52S", 2128, "DCS", 72, 1, 36, 35.86, 1, 0) +
                Body("2026-11-15 17:14:59", "2026-11-15 17:15M:47S", 48, "DCS", 72, 1, 0, 0, 1, 0));
            File.WriteAllText(Path.Combine(logs, "PiService__2026-11-17-19.log"),
                Body("2026-11-17 20:29:35", "2026-11-17 20:39M:19S", 584, "DCS", 72, 0, 72, 72, 0, 1) +
                Body("2026-11-17 21:07:31", "2026-11-17 21:13M:57S", 386, "DCS", 72, 0, 72, 0, 0.37, 0.62) +
                Body("2026-10-06 17:48:15", "2026-10-06 18:30M:30S", 2535, "FlightSimulator2024", 90, 0, 40.89, 35, 1, 0) +
                "2026-11-17 21:14:00.000 I 1 [x] Performance body to send: {roto\n");

            var s = VrSessions.ReadPimax(logs);
            Check(s.Count == 5 && s[0].Process == "FlightSimulator2024" && s[^1].Start == new DateTime(2026, 11, 17, 21, 7, 31),
                  $"lee todas las sesiones de todos los archivos, en orden, y salta las líneas rotas ({s.Count})");
            var flight = s.First(x => x.Seconds == 2128);
            Check(flight.End == new DateTime(2026, 11, 15, 18, 33, 52) && flight.TargetHz == 72 && flight.SmartSmoothing
                  && flight.MedianFps == 36 && Math.Abs(flight.GpuOver80 - 0.63) < 1e-9 && flight.Game == "DCS World",
                  "final con el formato raro de Pimax («18:33M:52S»), refresco, Smart Smoothing, mediana y GPU");
            Check(flight.Verdict == "A la mitad (36 FPS), Smart Smoothing", $"veredicto: a la mitad ({flight.Verdict})");
            Check(s.First(x => x.Seconds == 584).Verdict == "Llega a 72 Hz", "veredicto: llega");
            Check(s.First(x => x.Seconds == 386).Verdict == "Llega a 72 Hz el 63 % del tiempo", $"veredicto: llega a ratos ({s.First(x => x.Seconds == 386).Verdict})");
            Check(s.First(x => x.Seconds == 2535).Verdict == "No llega: 41 FPS de mediana", $"veredicto: MSFS a 41 FPS a 90 Hz sin Smart Smoothing = no llega ({s.First(x => x.Seconds == 2535).Verdict})");
            Check(s.First(x => x.Seconds == 48).Verdict == "Sesión corta", "veredicto: sesión corta");

            // Misiones de dcs.log dentro de cada sesión.
            File.WriteAllText(Path.Combine(saved, "Logs", "dcs.log"),
                "=== Log opened UTC 2026-11-17 20:29:30\n" +
                "2026-11-17 20:30:00.123 INFO    WORLDGENERAL (Main): loading mission from: \"C:\\Users\\x\\Saved Games\\DCS.openbeta\\Missions\\Apache Caucaso.miz\"\n" +
                "2026-11-17 20:30:04.000 INFO    WORLDGENERAL (Main): loading mission from: \"C:\\Users\\x\\Saved Games\\DCS.openbeta\\Missions\\Apache Caucaso.miz\"\n" +
                "2026-11-17 21:08:00.000 INFO    WORLDGENERAL (Main): loading mission from: \"C:\\Users\\x\\Saved Games\\DCS.openbeta\\Missions\\rigcontrol_bench.miz\"\n");
            var withMissions = VrSessions.WithMissions(s, VrSessions.DcsMissions(saved));
            Check(withMissions.First(x => x.Seconds == 584).Mission == "Apache Caucaso" && withMissions.First(x => x.Seconds == 386).IsBenchmark
                  && withMissions.First(x => x.Seconds == 2128).Mission is null && withMissions.First(x => x.Seconds == 2535).Mission is null,
                  "misiones de dcs.log en su sesión (una vez, sin .miz); la misión de prueba = medición de RigControl");

            // Historial: Pimax borra el archivo viejo, la sesión sigue; la misión se conserva.
            var history = Path.Combine(root, "sesiones-vr.json");
            var all = VrSessions.Refresh(logs, saved, history);
            File.Delete(Path.Combine(logs, "PiService__2026-11-15-16.log"));
            File.WriteAllText(Path.Combine(saved, "Logs", "dcs.log"), "=== Log opened UTC 2026-11-18 10:00:00\n");
            var again = VrSessions.Refresh(logs, saved, history);
            Check(all.Count == 5 && again.Count == 5 && again[0].Start > again[^1].Start
                  && again.First(x => x.Seconds == 2128).MedianFps == 36 && again.First(x => x.Seconds == 584).Mission == "Apache Caucaso",
                  "historial propio: lo que Pimax borra se conserva, con su misión, de la más reciente a la más antigua");

            // Configuración de Pimax de la sesión (18/11/2026: tras actualizar Pimax Play, DCS sin quad views).
            string WithPimax(int qv, int ffr) => Body("2026-11-18 18:43:55", "2026-11-18 19:48M:38S", 3883, "DCS", 90, 1, 44, 38.67, 1, 0)
                .Replace("\"runtime_dbg_asw_enable\":1", $"\"runtime_dbg_asw_enable\":1,\"runtime_enable_openxr_quadviews_fr\":{qv},\"runtime_enable_foveated_rendering\":{ffr}");
            var stereo = VrSessions.Parse(WithPimax(0, 0)[(WithPimax(0, 0).IndexOf('{'))..]);
            var qvOn = VrSessions.Parse(WithPimax(1, 1)[(WithPimax(1, 1).IndexOf('{'))..]);
            var ffrOn = VrSessions.Parse(WithPimax(0, 1)[(WithPimax(0, 1).IndexOf('{'))..]);
            Check(stereo?.PimaxRendering == "estéreo" && stereo.QuadViews == false && qvOn?.PimaxRendering == "QV" && ffrOn?.PimaxRendering == "FFR"
                  && flight.PimaxRendering is null,
                  $"quad views / foveated de Pimax por sesión; sin el dato (registros viejos) = desconocido ({stereo?.PimaxRendering}, {qvOn?.PimaxRendering}, {ffrOn?.PimaxRendering})");
            var merged = VrSessions.Merge([stereo! with { QuadViews = null, Foveated = null, Mission = "tempMission" }], [stereo!]);
            Check(merged.Count == 1 && merged[0].Mission == "tempMission" && merged[0].PimaxRendering == "estéreo",
                  "historial sin el dato de Pimax: se completa con lo nuevo y conserva la misión");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        return fails;
    }
}
