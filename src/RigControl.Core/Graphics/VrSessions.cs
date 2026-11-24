using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RigControl.Core.Graphics;

/// <summary>
/// Una sesión de juego en el visor tal como la resume Pimax Play al cerrarla ("Performance body to send" en los
/// registros de PiService): duración, refresco, Smart Smoothing, FPS (media, mediana, 1 % y 5 % bajos, reparto por
/// tramos) y uso de GPU/CPU. Pimax muestrea una vez por segundo y cuenta también los segundos a 0 FPS (menús,
/// cargas, visor dormido), así que la media puede salir muy baja: la mediana y los tramos dicen más.
/// <see cref="Mission"/> = misiones de DCS cargadas durante la sesión (de dcs.log, si aún las tiene).
/// </summary>
public sealed record VrSession
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public int Seconds { get; init; }
    public string Process { get; init; } = "";
    public int TargetHz { get; init; }
    public bool SmartSmoothing { get; init; }
    public string? Resolution { get; init; }
    public double AvgFps { get; init; }
    public double MedianFps { get; init; }
    public double P1Fps { get; init; }
    public double P5Fps { get; init; }
    /// <summary>Fracción de muestras (0..1) por debajo de 50 FPS, de 50 a 70, de 70 a 90 y por encima de 90.</summary>
    public double Below50 { get; init; }
    public double From50To70 { get; init; }
    public double From70To90 { get; init; }
    public double Above90 { get; init; }
    public double AvgGpu { get; init; }
    /// <summary>Fracción del tiempo (0..1) con la GPU por encima del 80 %.</summary>
    public double GpuOver80 { get; init; }
    public double AvgCpu { get; init; }
    public string? Mission { get; init; }
    /// <summary>
    /// Quad views de Pimax (runtime_enable_openxr_quadviews_fr) y foveated rendering fijo (runtime_enable_foveated_rendering)
    /// en esa sesión; null = sesión guardada antes de apuntarlo (18/11/2026). Sin quad views en el runtime, DCS pinta los dos
    /// ojos enteros aunque tenga quad views marcado: así fue el vuelo del 18/11/2026 tras actualizar Pimax Play.
    /// </summary>
    public bool? QuadViews { get; init; }
    public bool? Foveated { get; init; }

    /// <summary>
    /// Visor de la sesión: el último "HMD connected" del mismo registro de PiService antes de cerrarla. Null = no consta
    /// (las sesiones anteriores al 23/11/2026 eran todas con la Crystal Light).
    /// </summary>
    public string? Headset { get; init; }

    /// <summary>Lo que hizo Pimax con la imagen: "QV" (quad views), "FFR" (foveated fijo), "estéreo" o null si no consta.</summary>
    public string? PimaxRendering => QuadViews is null && Foveated is null ? null
        : QuadViews == true ? "QV" : Foveated == true ? "FFR" : "estéreo";

    public string Game => Process switch
    {
        "DCS" => "DCS World",
        "FlightSimulator2024" => "MSFS 2024",
        "FlightSimulator" => "MSFS 2020",
        _ => Process,
    };

    /// <summary>Medición o optimización de RigControl (la misión de prueba), no un vuelo.</summary>
    public bool IsBenchmark => Mission?.Contains("rigcontrol_bench", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Veredicto en pocas palabras: si llegó al refresco, si estuvo fija a la mitad (Smart Smoothing) o no llegó.
    /// Con la mediana: la media la hunden los segundos a 0 de menús y cargas.
    /// </summary>
    public string Verdict
    {
        get
        {
            if (Seconds < 120) return "Sesión corta";
            if (MedianFps <= 1) return "Sin imagen casi todo el rato";
            var t = TargetHz;
            if (t <= 0) return "";
            if (MedianFps >= t * 0.95)
            {
                // Lo que está por debajo de 50 FPS es la mitad del refresco (Smart Smoothing), cargas o menús.
                if (P5Fps >= t * 0.9 && Below50 < 0.05) return $"Llega a {t} Hz";
                if (Below50 <= 0.2) return $"Llega a {t} Hz casi siempre (con caídas)";
                return $"Llega a {t} Hz el {(1 - Below50) * 100:0} % del tiempo";
            }
            if (Math.Abs(MedianFps - t / 2.0) <= t * 0.03)
                return $"A la mitad ({t / 2} FPS)" + (SmartSmoothing ? ", Smart Smoothing" : "");
            return $"No llega: {MedianFps:0} FPS de mediana";
        }
    }
}

public static class VrSessions
{
    /// <summary>Historial propio: Pimax borra sus registros viejos (guarda unas semanas).</summary>
    public static string HistoryPath { get; } = Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "sesiones-vr.json");

    private const string Mark = "Performance body to send:";

    /// <summary>Todas las sesiones de los registros de PiService, de la más antigua a la más reciente.</summary>
    public static List<VrSession> ReadPimax(string? logDir = null)
    {
        logDir ??= PimaxInfo.LogDir;
        List<VrSession> list = [];
        if (!Directory.Exists(logDir)) return list;
        foreach (var file in Directory.EnumerateFiles(logDir, "PiService_*.log"))
        {
            string text;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                text = reader.ReadToEnd();
            }
            catch (IOException) { continue; }
            string? headset = null;
            foreach (var line in text.Split('\n'))
            {
                if (PimaxInfo.HeadsetInLine(line) is { } hmd) { headset = hmd; continue; }
                var at = line.IndexOf(Mark, StringComparison.Ordinal);
                if (at >= 0 && Parse(line[(at + Mark.Length)..]) is { } s) list.Add(headset is null ? s : s with { Headset = headset });
            }
        }
        return [.. list.OrderBy(s => s.Start)];
    }

    /// <summary>El JSON de una línea "Performance body to send"; null si no se entiende.</summary>
    public static VrSession? Parse(string json)
    {
        try
        {
            var game = JsonNode.Parse(json.Trim())?["game_info"];
            if (game is null) return null;
            var fps = game["fps_analysis"];
            var levels = fps?["fps_level_ratio"];
            var gpu = game["gpu_analysis"];
            var cpu = game["cpu_analysis"];
            var info = game["session_info"];
            var ints = game["pimax_play_config"]?["int_configs"];
            if (ParseTime(Str(game["session_start_time"])) is not { } start) return null;
            var seconds = (int)Num(info?["session_duration_seconds"]);
            var end = ParseTime(Str(game["session_end_time"])) ?? start.AddSeconds(seconds);
            return new VrSession
            {
                Start = start,
                End = end,
                Seconds = seconds > 0 ? seconds : (int)(end - start).TotalSeconds,
                Process = Str(game["client_process"]) ?? "?",
                TargetHz = (int)Num(fps?["fps_target"]),
                SmartSmoothing = Num(ints?["runtime_dbg_asw_enable"]) == 1,
                QuadViews = Flag(ints?["runtime_enable_openxr_quadviews_fr"]),
                Foveated = Flag(ints?["runtime_enable_foveated_rendering"]),
                Resolution = Str(game["client_resolution"]),
                AvgFps = Math.Round(Num(fps?["avg_fps"]), 1),
                MedianFps = Math.Round(Num(fps?["median_fps"]), 1),
                P1Fps = Math.Round(Num(fps?["p1_fps"]), 1),
                P5Fps = Math.Round(Num(fps?["p5_fps"]), 1),
                Below50 = Num(levels?["below_50"]),
                From50To70 = Num(levels?["50_70"]),
                From70To90 = Num(levels?["70_90"]),
                Above90 = Num(levels?["above_90"]),
                AvgGpu = Math.Round(Num(gpu?["avg_gpu_usage"]), 1),
                GpuOver80 = Num(gpu?["gpu_level_ratio"]?["80_100"]),
                AvgCpu = Math.Round(Num(cpu?["avg_cpu_usage"]), 1),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    private static bool? Flag(JsonNode? n) => n is null ? null : Num(n) != 0;

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double Num(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<double>(out var d) ? d : v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0
        : 0;

    /// <summary>"2026-11-17 19:26:47" o el formato raro del final, "2026-11-17 19:29M:40S".</summary>
    public static DateTime? ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var clean = s.Replace("M:", ":").Replace("S", "").Trim();
        return DateTime.TryParseExact(clean, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    private static readonly Regex MissionLine = new(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\.\d+ .*loading mission from: ""([^""]+)""", RegexOptions.Compiled);

    /// <summary>Misiones cargadas (hora, nombre sin .miz) en dcs.log y dcs.log.old de la carpeta de DCS.</summary>
    public static List<(DateTime When, string Name)> DcsMissions(string? savedGamesDir)
    {
        List<(DateTime, string)> list = [];
        if (savedGamesDir is null) return list;
        foreach (var name in new[] { "dcs.log.old", "dcs.log" })
        {
            var path = Path.Combine(savedGamesDir, "Logs", name);
            if (!File.Exists(path)) continue;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                while (reader.ReadLine() is { } line)
                {
                    if (!line.Contains("loading mission from:", StringComparison.Ordinal)) continue;
                    var m = MissionLine.Match(line);
                    if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                        list.Add((t, Path.GetFileNameWithoutExtension(m.Groups[2].Value.Replace('/', '\\'))));
                }
            }
            catch (IOException) { }
        }
        return list;
    }

    /// <summary>Pone a cada sesión de DCS sin misión las que se cargaron entre su inicio y su final.</summary>
    public static List<VrSession> WithMissions(IEnumerable<VrSession> sessions, List<(DateTime When, string Name)> missions) =>
        [.. sessions.Select(s =>
        {
            if (s.Mission is not null || s.Process != "DCS") return s;
            var names = missions.Where(m => m.When >= s.Start.AddMinutes(-1) && m.When <= s.End).Select(m => m.Name).Distinct().ToList();
            return names.Count == 0 ? s : s with { Mission = string.Join(", ", names) };
        })];

    public static List<VrSession> LoadHistory(string? path = null)
    {
        path ??= HistoryPath;
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<VrSession>>(File.ReadAllText(path)) ?? []; }
        catch (JsonException) { return []; }
    }

    public static void SaveHistory(IEnumerable<VrSession> sessions, string? path = null)
    {
        path ??= HistoryPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Historial + lo nuevo de Pimax (misma hora de inicio y programa = la misma sesión; la del historial
    /// conserva su misión y se completa con los datos que le falten). Devuelve de la más reciente a la más antigua.</summary>
    public static List<VrSession> Merge(IEnumerable<VrSession> history, IEnumerable<VrSession> fresh)
    {
        var map = new Dictionary<(DateTime, string), VrSession>();
        foreach (var s in fresh) map[(s.Start, s.Process)] = s;
        foreach (var h in history)
        {
            if (!map.TryGetValue((h.Start, h.Process), out var f)) { map[(h.Start, h.Process)] = h; continue; }
            // La del historial manda (misión ya resuelta), pero se completa con lo que no tenía apuntado.
            map[(h.Start, h.Process)] = h with
            {
                Mission = h.Mission ?? f.Mission,
                QuadViews = h.QuadViews ?? f.QuadViews,
                Foveated = h.Foveated ?? f.Foveated,
                Headset = h.Headset ?? f.Headset,
            };
        }
        return [.. map.Values.OrderByDescending(s => s.Start)];
    }

    /// <summary>Lee Pimax, le pone las misiones de DCS, lo junta con el historial, lo guarda y lo devuelve.</summary>
    public static List<VrSession> Refresh(string? pimaxLogDir = null, string? dcsSavedGames = null, string? historyPath = null)
    {
        var fresh = WithMissions(ReadPimax(pimaxLogDir), DcsMissions(dcsSavedGames ?? DcsBenchmark.FindSavedGames()));
        var all = Merge(LoadHistory(historyPath), fresh);
        try { SaveHistory(all, historyPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return all;
    }
}
