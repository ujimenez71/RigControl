using System.Globalization;

namespace RigControl.Core.Graphics;

/// <summary>Un nivel de un ajuste: valor en options.lua (texto Lua), nombre, puntos de calidad visual y coste estimado de GPU.</summary>
public sealed record SettingLevel(string Value, string Name, double Quality, double Gpu);

/// <summary>Un ajuste gráfico de DCS que el optimizador puede mover.</summary>
/// <param name="Restart">DCS solo lo aplica al reiniciar (no se puede probar entre pruebas de la misma sesión).</param>
public sealed record DcsSetting(string Key, string Label, string Why, SettingLevel[] Levels, bool Restart = false)
{
    /// <summary>Nivel que corresponde al valor actual (también "0.700" = "0.7"), o -1.</summary>
    public int IndexOf(string? luaValue)
    {
        if (luaValue is null) return -1;
        var v = LuaOptionsFile.Display(luaValue).Trim();
        for (var i = 0; i < Levels.Length; i++)
        {
            var l = LuaOptionsFile.Display(Levels[i].Value).Trim();
            if (string.Equals(l, v, StringComparison.OrdinalIgnoreCase)) return i;
            if (double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && Math.Abs(a - b) < 1e-6) return i;
        }
        return -1;
    }
}

/// <summary>
/// Ajustes gráficos de DCS con sus niveles. Niveles, puntos de calidad y costes previstos tomados de
/// dcstune/data/settings.json de DCS Graphics Optimizer (MIT, ver Graphics\Bench\THIRD-PARTY-NOTICES.md):
/// los valores son los que acepta DCS 2.9 (optionsDb.lua); la calidad es una tabla de criterio, no una medida;
/// el coste es una estimación que la medición sustituye. Textos traducidos.
/// </summary>
public static class DcsSettingsCatalog
{
    private static SettingLevel L(string v, string n, double q, double g) => new(v, n, q, g);

    public static readonly DcsSetting[] All =
    [
        new("graphics.textures", "Texturas", "Aviones, cabinas y vehículos más nítidos",
            [L("0", "Bajas", 0, 0), L("1", "Medias", 8, 0), L("2", "Altas", 14, 0.01)], Restart: true),
        new("graphics.terrainTextures", "Texturas del terreno", "Suelo, pistas y campos más nítidos",
            [L("\"min\"", "Bajas", 0, 0), L("\"max\"", "Altas", 8, 0.01)], Restart: true),
        new("graphics.shadows", "Sombras", "Sombras más nítidas y detalladas en la cabina y los aviones",
            [L("0", "No", 0, 0), L("1", "Solo planas", 3, 0.02), L("2", "Bajas", 7, 0.06), L("3", "Medias", 9, 0.09), L("4", "Altas", 11, 0.14)]),
        new("graphics.flatTerrainShadows", "Sombras de objetos del terreno", "Edificios y árboles con sombra de verdad en el suelo",
            [L("2", "No", 0, 0), L("1", "Planas", 3, 0.02), L("0", "Normales", 6, 0.10)]),
        new("graphics.clouds", "Nubes", "Nubes más suaves y detalladas",
            [L("0", "Bajas", 0, 0), L("1", "Estándar", 5, 0.04), L("2", "Altas", 8, 0.08), L("3", "Ultra", 10, 0.14)]),
        new("graphics.visibRange", "Distancia de visibilidad", "Edificios, árboles y objetos visibles más lejos",
            [L("\"Low\"", "Baja", 0, 0), L("\"Medium\"", "Media", 4, 0.03), L("\"High\"", "Alta", 8, 0.06), L("\"Ultra\"", "Ultra", 10, 0.09), L("\"Extreme\"", "Extrema", 11, 0.12)]),
        new("graphics.water", "Agua", "Olas y reflejos del mar más realistas",
            [L("0", "Baja", 0, 0), L("1", "Media", 3, 0.02), L("2", "Alta", 6, 0.05)]),
        new("graphics.SSAO", "Oclusión ambiental (SSAO)", "Sombras suaves de contacto en rincones y bajo los objetos",
            [L("0", "No", 0, 0), L("1", "Baja", 2, 0.03), L("2", "Alta", 3, 0.06)]),
        new("graphics.SSLR", "Reflejos en pantalla (SSLR)", "Reflejos en pistas mojadas, agua y cristal",
            [L("0", "No", 0, 0), L("1", "Sí", 2, 0.05)]),
        new("graphics.SSS", "Sombras en pantalla (SSS)", "Sombras finas extra en detalles pequeños",
            [L("0", "No", 0, 0), L("1", "Sí", 2, 0.04)]),
        new("graphics.forestDistanceFactor", "Distancia de los bosques", "Bosques visibles más lejos",
            [L("0.3", "30 %", 0, 0), L("0.5", "50 %", 2, 0.02), L("0.7", "70 %", 3, 0.04), L("1", "100 %", 4, 0.07)]),
        new("graphics.clutterMaxDistance", "Distancia de la hierba", "Hierba y detalle pequeño del suelo más lejos",
            [L("0", "No", 0, 0), L("500", "500 m", 2, 0.02), L("1000", "1000 m", 3, 0.04), L("1500", "1500 m", 3.5, 0.06)]),
    ];

    public static DcsSetting? Find(string key) => All.FirstOrDefault(s => s.Key == key);

    /// <summary>Texto Lua -> valor para el script de DCS (número, texto o booleano), como lo pide optionsEditor.setOption.</summary>
    public static object ToHookValue(string luaValue)
    {
        var v = luaValue.Trim();
        if (v is "true" or "false") return v == "true";
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') return LuaOptionsFile.Display(v);
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : v;
    }
}

/// <summary>Lo que dice el registro de Pimax Play de la última sesión: refresco objetivo, Smart Smoothing, uso de GPU.</summary>
public sealed record PimaxSession(int TargetFps, bool SmartSmoothing, string? Resolution, double GpuOver80Percent, double AvgGpu, DateTime When);

public static class PimaxInfo
{
    public static string LogDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pimax", "PiService", "Log");

    /// <summary>
    /// Última línea "Performance body to send" de los registros de PiService (la escribe al cerrar una sesión de
    /// un juego). Null si no hay Pimax o no se encuentra. <paramref name="process"/> = solo sesiones de ese programa.
    /// </summary>
    public static PimaxSession? LastSession(string? process = "DCS", string? logDir = null)
    {
        logDir ??= LogDir;
        if (!Directory.Exists(logDir)) return null;
        foreach (var file in Directory.EnumerateFiles(logDir, "PiService_*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
        {
            string[] lines;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                lines = reader.ReadToEnd().Split('\n');
            }
            catch (IOException) { continue; }
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var at = lines[i].IndexOf("Performance body to send:", StringComparison.Ordinal);
                if (at < 0) continue;
                try
                {
                    var json = System.Text.Json.Nodes.JsonNode.Parse(lines[i][(at + 25)..].Trim());
                    var game = json?["game_info"];
                    if (game is null) continue;
                    if (process is not null && !string.Equals(game["client_process"]?.GetValue<string>(), process, StringComparison.OrdinalIgnoreCase)) continue;
                    var target = game["fps_analysis"]?["fps_target"]?.GetValue<int>() ?? 0;
                    var asw = game["pimax_play_config"]?["int_configs"]?["runtime_dbg_asw_enable"]?.GetValue<int>() == 1;
                    var gpu = game["gpu_analysis"];
                    var ratios = gpu?["gpu_level_ratio"];
                    var over80 = (ratios?["80_100"]?.GetValue<double>() ?? 0) * 100;
                    var when = DateTime.TryParse(lines[i][..Math.Min(23, lines[i].Length)], CultureInfo.InvariantCulture, DateTimeStyles.None, out var w) ? w : File.GetLastWriteTime(file);
                    return new PimaxSession(target, asw, game["client_resolution"]?.GetValue<string>(), Math.Round(over80, 1),
                                            gpu?["avg_gpu_usage"]?.GetValue<double>() ?? 0, when);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
            }
        }
        return null;
    }

    private const string HmdMark = "product name: ";

    /// <summary>
    /// Último visor conectado según PiService ("[Device::HmdConnected] HMD connected: Pimax K11, product name: Pimax Dream Air,
    /// serial number: ..."): el nombre comercial. Null si no hay registro de Pimax o no aparece.
    /// </summary>
    public static string? ConnectedHeadset(string? logDir = null)
    {
        logDir ??= LogDir;
        if (!Directory.Exists(logDir)) return null;
        foreach (var file in Directory.EnumerateFiles(logDir, "PiService_*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(5))
        {
            string? found = null;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                while (reader.ReadLine() is { } line)
                    if (HeadsetInLine(line) is { } name) found = name;
            }
            catch (IOException) { continue; }
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>El nombre del visor si la línea es la de "HMD connected"; si no, null.</summary>
    public static string? HeadsetInLine(string line)
    {
        if (!line.Contains("HMD connected:", StringComparison.Ordinal)) return null;
        var at = line.IndexOf(HmdMark, StringComparison.Ordinal);
        if (at < 0) return null;
        var rest = line[(at + HmdMark.Length)..];
        var end = rest.IndexOf(',');
        var name = (end < 0 ? rest : rest[..end]).Trim();
        return name.Length > 0 ? name : null;
    }

    /// <summary>
    /// Momento en que se apagó la pantalla del visor entre <paramref name="from"/> y <paramref name="to"/> (líneas
    /// "HMD screen state changed to false/true" de PiService: el sensor de proximidad la apaga si nadie lo lleva
    /// puesto, y entonces DCS cae a unos pocos FPS). Null si estuvo encendida (se toleran <paramref name="toleranceSeconds"/>
    /// apagada en total: Pimax la apaga y enciende un momento al arrancar) o si no hay registro de Pimax.
    /// </summary>
    public static DateTime? ScreenOff(DateTime from, DateTime to, string? logDir = null, double toleranceSeconds = 10)
    {
        logDir ??= LogDir;
        if (!Directory.Exists(logDir)) return null;
        const string mark = "HMD screen state changed to ";
        List<(DateTime When, bool On)> events = [];
        foreach (var file in Directory.EnumerateFiles(logDir, "PiService_*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                while (reader.ReadLine() is { } line)
                {
                    var at = line.IndexOf(mark, StringComparison.Ordinal);
                    if (at < 0 || line.Length < 23) continue;
                    if (!DateTime.TryParse(line[..23], CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) continue;
                    events.Add((when, line[(at + mark.Length)..].TrimStart().StartsWith("true", StringComparison.Ordinal)));
                }
            }
            catch (IOException) { }
        }
        events.Sort((a, b) => a.When.CompareTo(b.When));
        var on = true;                     // sin noticias, encendida
        foreach (var e in events.Where(e => e.When < from)) on = e.On;
        DateTime? firstOff = on ? null : from;
        var offSince = on ? (DateTime?)null : from;
        var offSeconds = 0.0;
        foreach (var e in events.Where(e => e.When >= from && e.When <= to))
        {
            if (!e.On && offSince is null) { offSince = e.When; firstOff ??= e.When; }
            else if (e.On && offSince is { } since) { offSeconds += (e.When - since).TotalSeconds; offSince = null; }
        }
        if (offSince is { } still) offSeconds += (to - still).TotalSeconds;
        return offSeconds > toleranceSeconds ? firstOff : null;
    }

    /// <summary>
    /// Refresco al que iba el visor: el de Pimax si es de esta medición; si no, el ritmo de los fotogramas
    /// (mediana a menos de un 4 % de 72/90/120 Hz); si no, el configurado.
    /// </summary>
    public static int DetectRefresh(double medianMs, int configured, PimaxSession? pimax, DateTime measuredFrom, IReadOnlyList<int>? rates = null) =>
        DetectRefresh([medianMs], configured, pimax, measuredFrom, rates);

    /// <summary>
    /// Igual, con todos los fotogramas: gana el refresco con más fotogramas a menos de un 6 % de su periodo o del
    /// doble (Smart Smoothing a la mitad), si son al menos la mitad. Una sola mediana no vale: con unas escenas a
    /// 90 y otras a 45 caía en 17 ms (= mitad de 120 Hz) el 17/11/2026.
    /// </summary>
    /// <paramref name="rates"/> = refrescos del visor (la Dream Air no tiene 120 Hz: 60 FPS no es "la mitad de 120").
    public static int DetectRefresh(IReadOnlyList<double> frameMs, int configured, PimaxSession? pimax, DateTime measuredFrom, IReadOnlyList<int>? rates = null)
    {
        if (pimax is { TargetFps: > 0 } p && p.When >= measuredFrom.AddMinutes(-1)) return p.TargetFps;
        if (frameMs.Count == 0) return configured;
        static bool Near(double ms, double period) => Math.Abs(ms - period) / period < 0.06;
        var best = (rates ?? HeadsetModels.DefaultRates)
            .Select(hz => (Hz: hz, Count: frameMs.Count(ms => Near(ms, 1000.0 / hz) || Near(ms, 2000.0 / hz))))
            .MaxBy(c => c.Count);
        return best.Count * 2 >= frameMs.Count ? best.Hz : configured;
    }

    /// <summary>
    /// Pimax escribe el resumen de la sesión al cerrarse el juego, a veces un instante DESPUÉS de que DCS termine:
    /// espera hasta <paramref name="waitMs"/> uno posterior a <paramref name="since"/> (sin registro de Pimax, no espera).
    /// </summary>
    public static PimaxSession? WaitSession(DateTime since, string? logDir = null, int waitMs = 15_000)
    {
        logDir ??= LogDir;
        if (!Directory.Exists(logDir)) return null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var s = LastSession(logDir: logDir);
            if (s is not null && s.When >= since) return s;
            if (clock.ElapsedMilliseconds >= waitMs) return s;
            Thread.Sleep(500);
        }
    }
}
