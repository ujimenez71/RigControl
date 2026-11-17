using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RigControl.Core.Hangar;

namespace RigControl.Core.Graphics;

/// <summary>Una escena de la misión de prueba: avión de la IA que sigue la cámara y desde dónde.</summary>
public sealed record BenchScene(string Name, string Unit, double Back, double Up, double Side = 0);

/// <summary>Cifras de un trozo de la medición. MissedPercent = fotogramas que no llegan al refresco del visor (+10 %).</summary>
public sealed record FrameStats(int Frames, double AvgFps, double P1LowFps, double MedianMs, double MaxMs, int Stutters, double MissedPercent)
{
    public const double StutterFactor = 2.5;

    /// <summary>Lanza InvalidDataException con menos de 30 fotogramas (no sirve para comparar).</summary>
    public static FrameStats From(IReadOnlyList<double> frameMs, int refreshHz)
    {
        if (frameMs.Count < 30) throw new InvalidDataException($"solo {frameMs.Count} fotogramas medidos");
        var ordered = frameMs.Order().ToArray();
        var n = ordered.Length;
        var median = ordered[n / 2];
        // 1 % más bajo = FPS en el percentil 99 del tiempo de fotograma (más estable que la media del 1 % peor).
        var p99 = ordered[Math.Min(n - 1, (int)(n * 0.99))];
        var budget = 1000.0 / Math.Max(1, refreshHz) * 1.1;
        return new FrameStats(n, Math.Round(1000.0 * n / ordered.Sum(), 1), Math.Round(1000.0 / p99, 1), Math.Round(median, 2),
                              Math.Round(ordered[^1], 1), frameMs.Count(x => x > StutterFactor * median),
                              Math.Round(100.0 * frameMs.Count(x => x > budget) / n, 1));
    }

    public override string ToString() =>
        $"{AvgFps,6:0.0} FPS de media · 1 % bajo {P1LowFps,5:0.0} · mediana {MedianMs,5:0.00} ms · tirones {Stutters,3} · fuera de tiempo {MissedPercent,4:0.0} %";
}

public sealed record SceneResult(string Name, FrameStats Stats);

/// <summary>Una prueba dentro de un arranque: etiqueta y ajustes "graphics.*" (texto Lua) que se ponen antes de medir.</summary>
public sealed record BenchTrial(string Tag, IReadOnlyDictionary<string, string> Set);

/// <summary>Una medición completa (se guarda en mediciones\ para comparar después).</summary>
public sealed record BenchResult(DateTime When, string Profile, bool Vr, int RefreshHz, int Seconds, FrameStats Total,
                                 List<SceneResult> Scenes, Dictionary<string, string> Settings);

/// <summary>Arrancar y vigilar DCS (sustituible en las pruebas por un DCS falso).</summary>
public interface IDcsLauncher
{
    bool IsRunning();
    IDcsProcess Start(string exe, IReadOnlyList<string> args, string workingDirectory);
}

public interface IDcsProcess : IDisposable
{
    bool WaitForExit(int milliseconds);
    void Kill();
}

public sealed class DcsLauncher : IDcsLauncher
{
    public bool IsRunning()
    {
        var found = Process.GetProcessesByName("DCS");
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }

    public IDcsProcess Start(string exe, IReadOnlyList<string> args, string workingDirectory)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = workingDirectory };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return new Proc(Process.Start(psi) ?? throw new InvalidOperationException("No se pudo arrancar DCS."));
    }

    private sealed class Proc(Process p) : IDcsProcess
    {
        public bool WaitForExit(int milliseconds) => p.WaitForExit(milliseconds);
        public void Kill() { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        public void Dispose() => p.Dispose();
    }
}

/// <summary>
/// Mide los FPS de DCS (en VR o en el monitor) con la misión de prueba, sin que haga falta tocar nada:
/// copia de options.lua/autoexec.cfg, perfil gráfico opcional, script temporal en Scripts\Hooks (con una clave
/// aleatoria: un DCS arrancado a mano no lo ejecuta), misión en Missions, arranque con
/// "--no-launcher --force_enable_VR -w &lt;perfil&gt;", espera del resultado y limpieza + restauración SIEMPRE
/// (también la siguiente vez si se cortó a medias). Mecanismo de DCS Graphics Optimizer (MIT).
/// </summary>
public sealed class DcsBenchmark
{
    public static readonly BenchScene[] Scenes =
    [
        new("ciudad", "TUNE-CITY-1", 35, 8),
        new("aerodromo", "TUNE-RAMP-1", 28, 5, 10),
        new("nubes", "TUNE-HIGH-1", 40, 10),
    ];

    private const string KeyMark = "@RIGCONTROL_KEY@";
    private const int GraceSeconds = 45;
    private const string VrStarted = "OpenXR: Creating swapchain for view";
    private static readonly string[] VrFailed = ["OpenXR failure", "OpenXR exception"];

    private readonly IDcsLauncher _launcher;

    public DcsBenchmark(string savedGamesDir, string dcsExe, IDcsLauncher? launcher = null)
    {
        SavedGamesDir = savedGamesDir;
        DcsExe = dcsExe;
        _launcher = launcher ?? new DcsLauncher();
    }

    /// <summary>Carpeta de Saved Games que usa DCS (p. ej. ...\Saved Games\DCS.openbeta); su nombre va en "-w".</summary>
    public string SavedGamesDir { get; }
    public string DcsExe { get; }

    /// <summary>Esperas (en las pruebas, mucho más cortas).</summary>
    public int PollMs { get; init; } = 1000;
    public int GraceMs { get; init; } = GraceSeconds * 1000;

    /// <summary>Registros de Pimax Play (refresco del visor); en las pruebas, una carpeta que no existe.</summary>
    public string PimaxLogDir { get; init; } = PimaxInfo.LogDir;

    /// <summary>Dónde se guardan las mediciones (en las pruebas, una carpeta temporal).</summary>
    public string ResultsDirectory { get; init; } = ResultsDir;

    private string Config => Path.Combine(SavedGamesDir, "Config");
    private string OptionsPath => Path.Combine(Config, "options.lua");
    private string AutoexecPath => Path.Combine(Config, "autoexec.cfg");
    private string HookPath => Path.Combine(SavedGamesDir, "Scripts", "Hooks", "rigcontrol_bench.lua");
    private string MissionPath => Path.Combine(SavedGamesDir, "Missions", "rigcontrol_bench.miz");
    private string DirectivePath => Path.Combine(Config, "rigcontrol_bench_directive.json");
    public string ResultPath => Path.Combine(Config, "rigcontrol_bench_result.json");
    private string JournalPath => Path.Combine(Config, "rigcontrol_bench_journal.txt");
    private string BackupOptions => OptionsPath + ".rigcontrol-bench";
    private string BackupAutoexec => AutoexecPath + ".rigcontrol-bench";
    public string FailureLogPath => Path.Combine(Config, "rigcontrol-bench-failure.log");

    public static string ResultsDir { get; } = Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "mediciones");

    /// <summary>DCS.exe: el de un paso del Hangar que lo abra o, si no, las rutas de instalación habituales.</summary>
    public static string? FindDcsExe()
    {
        try
        {
            foreach (var step in HangarFile.Load().Hangars.SelectMany(h => h.Steps))
            {
                var target = Environment.ExpandEnvironmentVariables(step.Target ?? "");
                if (target.EndsWith("DCS.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target)) return target;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        string[] guesses =
        [
            @"C:\Program Files\Eagle Dynamics\DCS World\bin\DCS.exe",
            @"C:\Program Files\Eagle Dynamics\DCS World OpenBeta\bin\DCS.exe",
        ];
        return guesses.FirstOrDefault(File.Exists);
    }

    /// <summary>Saved Games\DCS* que se usa de verdad (la de dcs.log más reciente).</summary>
    public static string? FindSavedGames() => GraphicsManager.FindDcsConfig() is { } c ? Path.GetDirectoryName(c) : null;

    /// <summary>Si una medición anterior se cortó (corte de luz, RigProbe cerrado), deja todo como estaba.</summary>
    public bool RecoverIfInterrupted()
    {
        if (!File.Exists(JournalPath)) return false;
        Restore();
        return true;
    }

    /// <summary>
    /// Una medición. Profile = perfil gráfico que se aplica para medirlo (null = los ajustes actuales). Lanza
    /// InvalidOperationException (DCS abierto, sin resultado, VR que no arrancó...), TimeoutException u
    /// OperationCanceledException; en todos los casos los archivos de DCS quedan como estaban.
    /// </summary>
    public BenchResult Measure(GraphicsProfile? profile, bool vr, int refreshHz, IProgress<string>? progress = null,
                               CancellationToken cancel = default, int sceneSeconds = 25, int settleSeconds = 20, int timeoutSeconds = 900)
    {
        var results = MeasureTrials(profile, [], vr, refreshHz, progress, cancel, sceneSeconds, settleSeconds, timeoutSeconds);
        var result = results[0] with { Profile = profile?.Name ?? "(actual)" };
        Save(result, ResultsDirectory);
        return result;
    }

    /// <summary>
    /// Varias pruebas en UN arranque de DCS: cada una pone sus ajustes (solo "graphics.*" que DCS aplica sin
    /// reiniciar, vía optionsEditor en el script) y repite la misión. Sin pruebas = una medición de lo que haya.
    /// Devuelve una medición por prueba (Profile = su etiqueta), con el refresco detectado. No las guarda.
    /// </summary>
    public List<BenchResult> MeasureTrials(GraphicsProfile? profile, IReadOnlyList<BenchTrial> trials, bool vr, int refreshHz,
                                           IProgress<string>? progress = null, CancellationToken cancel = default,
                                           int sceneSeconds = 25, int settleSeconds = 20, int timeoutSeconds = 900)
    {
        if (RecoverIfInterrupted()) progress?.Report("Restaurada la configuración de una medición anterior que se cortó.");
        if (_launcher.IsRunning()) throw new InvalidOperationException("DCS está abierto: ciérralo antes de medir.");
        if (!File.Exists(OptionsPath)) throw new InvalidOperationException($"No existe {OptionsPath}.");
        if (!File.Exists(DcsExe)) throw new InvalidOperationException($"No existe {DcsExe}.");

        var started = DateTime.Now;
        Backup();
        try
        {
            if (profile is not null)
            {
                var changes = new GraphicsManager(Config, null, _ => false).Apply(profile);
                progress?.Report($"Perfil «{profile.Name}» aplicado para la medición ({changes.Count} cambio(s)).");
            }
            var settings = new GraphicsManager(Config, null, _ => false).Current("dcs");
            if (!vr)
            {
                // En el monitor, sin límite de FPS ni sincronización: si no, todas las configuraciones salen iguales.
                // En VR el ritmo lo marca el visor y se mide con los ajustes tal cual.
                var options = LuaOptionsFile.Load(OptionsPath);
                options.Set("graphics.sync", "false");
                options.Set("graphics.maxFPS", "300");
                File.WriteAllText(OptionsPath, options.Text);
            }

            Install(trials, sceneSeconds, settleSeconds, timeoutSeconds);
            var minutes = Math.Max(5, 2 + trials.Count * (sceneSeconds * 3 + 40) / 60);
            progress?.Report($"Arrancando DCS {(vr ? "en VR" : "en el monitor")}" +
                             (trials.Count > 1 ? $" para {trials.Count} pruebas seguidas" : "") + $"… (unos {minutes} min; no toques el ordenador)");
            Run(vr, timeoutSeconds, progress, cancel);

            if (vr) CheckVr();
            return ReadResults(vr, refreshHz, trials, settings, started);
        }
        finally
        {
            Restore();
        }
    }

    // ------------------------------------------------------------------ pasos

    private void Backup()
    {
        File.Copy(OptionsPath, BackupOptions, overwrite: true);
        var hadAutoexec = File.Exists(AutoexecPath);
        if (hadAutoexec) File.Copy(AutoexecPath, BackupAutoexec, overwrite: true);
        File.WriteAllText(JournalPath, hadAutoexec ? "autoexec" : "sin-autoexec");
    }

    /// <summary>Vuelve a dejar options.lua y autoexec.cfg como estaban y quita todo lo instalado.</summary>
    private void Restore()
    {
        var hadAutoexec = File.Exists(JournalPath) && File.ReadAllText(JournalPath).Trim() == "autoexec";
        if (File.Exists(BackupOptions)) File.Move(BackupOptions, OptionsPath, overwrite: true);
        if (File.Exists(BackupAutoexec)) File.Move(BackupAutoexec, AutoexecPath, overwrite: true);
        else if (File.Exists(JournalPath) && !hadAutoexec && File.Exists(AutoexecPath)) File.Delete(AutoexecPath);
        foreach (var f in new[] { HookPath, MissionPath, DirectivePath, ResultPath, JournalPath })
            if (File.Exists(f)) File.Delete(f);
    }

    private void Install(IReadOnlyList<BenchTrial> trials, int sceneSeconds, int settleSeconds, int timeoutSeconds)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(HookPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(MissionPath)!);
        File.WriteAllText(HookPath, ReadResource("RigControl.Bench.Hook").Replace(KeyMark, key));
        using (var mission = typeof(DcsBenchmark).Assembly.GetManifestResourceStream("RigControl.Bench.Mission")!)
        using (var f = File.Create(MissionPath))
            mission.CopyTo(f);
        if (File.Exists(ResultPath)) File.Delete(ResultPath);

        // Igual que espera el script: rutas como las da lfs.writedir() (con "\"), contraseña solo letras y números.
        var directive = new JsonObject
        {
            ["key"] = key,
            ["not_after"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600,
            ["password"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            ["port"] = FreePort(),
            ["mission"] = MissionPath,
            ["scenes"] = new JsonArray([.. Scenes.Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name, ["unit"] = s.Unit, ["back"] = s.Back, ["up"] = s.Up, ["side"] = s.Side,
            })]),
            ["scene_sec"] = sceneSeconds,
            ["scene_lead_sec"] = 4,
            ["settle_sec"] = settleSeconds,
            ["total_timeout"] = timeoutSeconds - 30,
            ["screenshots"] = false,
            ["tag"] = "rigcontrol",
        };
        if (trials.Count > 0)
            directive["trials"] = new JsonArray([.. trials.Select((t, i) => (JsonNode)new JsonObject
            {
                ["tag"] = $"t{i:00}",
                // El script antepone "graphics." a cada nombre; los valores, ya con su tipo (número, texto, booleano).
                ["set"] = new JsonObject(t.Set.Select(kv => KeyValuePair.Create(kv.Key["graphics.".Length..],
                    (JsonNode?)JsonValue.Create(DcsSettingsCatalog.ToHookValue(kv.Value) switch
                    {
                        double d => (object)d, bool b => b, var o => o.ToString()!,
                    })))),
                ["settle_sec"] = i == 0 ? settleSeconds : TrialSettleSeconds,
            })]);
        File.WriteAllText(DirectivePath, directive.ToJsonString());
    }

    private void Run(bool vr, int timeoutSeconds, IProgress<string>? progress, CancellationToken cancel)
    {
        string[] args = ["--no-launcher", vr ? "--force_enable_VR" : "--force_disable_VR", "-w", Path.GetFileName(SavedGamesDir)];
        using var proc = _launcher.Start(DcsExe, args, Path.GetDirectoryName(DcsExe)!);
        var clock = Stopwatch.StartNew();
        long? resultAt = null;
        var lastReport = 0L;
        while (!proc.WaitForExit(PollMs))
        {
            if (cancel.IsCancellationRequested)
            {
                proc.Kill();
                proc.WaitForExit(30_000);
                cancel.ThrowIfCancellationRequested();
            }
            if (File.Exists(ResultPath))
            {
                resultAt ??= clock.ElapsedMilliseconds;
                if (clock.ElapsedMilliseconds - resultAt > GraceMs)
                {
                    progress?.Report("La medición terminó pero DCS no se cerró solo: cerrándolo.");
                    proc.Kill();
                    proc.WaitForExit(30_000);
                    break;
                }
            }
            else if (clock.ElapsedMilliseconds > timeoutSeconds * 1000L)
            {
                proc.Kill();
                proc.WaitForExit(30_000);
                KeepFailureLog();
                throw new TimeoutException($"DCS no terminó la medición en {timeoutSeconds / 60} min y se ha cerrado. Mira {FailureLogPath}.");
            }
            if (clock.ElapsedMilliseconds - lastReport >= 30_000)
            {
                lastReport = clock.ElapsedMilliseconds;
                progress?.Report($"  … {clock.Elapsed:mm\\:ss}");
            }
        }
        if (!File.Exists(ResultPath))
        {
            KeepFailureLog();
            throw new InvalidOperationException($"DCS se cerró sin escribir el resultado (¿pidió algo al arrancar: actualización, inicio de sesión?). Su registro: {FailureLogPath}.");
        }
    }

    private void CheckVr()
    {
        var logPath = Path.Combine(SavedGamesDir, "Logs", "dcs.log");
        var log = File.Exists(logPath) ? ReadShared(logPath) : "";
        if (!log.Contains(VrStarted, StringComparison.Ordinal) || VrFailed.Any(f => log.Contains(f, StringComparison.Ordinal)))
        {
            KeepFailureLog();
            throw new InvalidOperationException("DCS no pudo mostrar la imagen en el visor (OpenXR no arrancó): la medición no vale. " +
                                                "Comprueba que la Crystal Light está encendida, despierta y funcionando en Pimax Play.");
        }
    }

    /// <summary>Asentamiento de cada prueba después de la primera (la misión ya está cargada una vez).</summary>
    public const int TrialSettleSeconds = 10;

    private List<BenchResult> ReadResults(bool vr, int refreshHz, IReadOnlyList<BenchTrial> trials, Dictionary<string, string> settings, DateTime started)
    {
        var root = JsonNode.Parse(File.ReadAllText(ResultPath)) ?? throw new InvalidDataException("Resultado vacío.");
        if (root["ok"]?.GetValue<bool>() != true)
        {
            KeepFailureLog();
            throw new InvalidOperationException($"DCS no pudo hacer la medición: {root["reason"]?.GetValue<string>()}. Su registro: {FailureLogPath}.");
        }

        // Una lista de escenas por prueba ("trials"; sin pruebas, "scenes").
        List<(string Name, List<double> Ms)>[] raw = trials.Count > 0
            ? [.. (root["trials"]?.AsArray() ?? []).Select(t => ReadScenes(t?["scenes"]))]
            : [ReadScenes(root["scenes"])];
        if (raw.Length == 0 || raw[0].Count == 0) throw new InvalidDataException("El resultado no trae escenas.");
        if (trials.Count > 0 && raw.Length < trials.Count) throw new InvalidDataException($"Solo hay {raw.Length} de {trials.Count} pruebas en el resultado.");

        // En VR el ritmo lo marca el visor: el refresco de verdad sale de Pimax o del ritmo de la primera prueba.
        if (vr)
        {
            var firstAll = raw[0].SelectMany(s => s.Ms).Order().ToList();
            var detected = PimaxInfo.DetectRefresh(firstAll[firstAll.Count / 2], refreshHz, PimaxInfo.LastSession(logDir: PimaxLogDir), started);
            refreshHz = detected;
        }
        var seconds = (int)(DateTime.Now - started).TotalSeconds;
        return [.. raw.Select((scenes, i) => new BenchResult(started, trials.Count > 0 ? trials[i].Tag : "(actual)", vr, refreshHz, seconds,
            FrameStats.From([.. scenes.SelectMany(s => s.Ms)], refreshHz),
            [.. scenes.Select(s => new SceneResult(s.Name, FrameStats.From(s.Ms, refreshHz)))],
            trials.Count > 0 ? Merge(settings, trials[i].Set) : settings))];
    }

    private static List<(string Name, List<double> Ms)> ReadScenes(JsonNode? scenes) =>
        [.. (scenes?.AsArray() ?? []).Select(s => (s?["name"]?.GetValue<string>() ?? "?",
            (s?["frame_ms"]?.AsArray() ?? []).Select(v => v!.GetValueKind() == JsonValueKind.String
                ? double.Parse(v.GetValue<string>(), CultureInfo.InvariantCulture) : v.GetValue<double>()).ToList()))];

    private static Dictionary<string, string> Merge(Dictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        var r = new Dictionary<string, string>(a, StringComparer.Ordinal);
        foreach (var (k, v) in b) r[k] = v;
        return r;
    }

    private static void Save(BenchResult result, string dir)
    {
        Directory.CreateDirectory(dir);
        var safe = string.Concat(result.Profile.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var path = Path.Combine(dir, $"{result.When:yyyyMMdd-HHmmss}-dcs-{(result.Vr ? "vr" : "monitor")}-{safe}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Mediciones guardadas, las más nuevas primero.</summary>
    public static List<BenchResult> LoadResults()
    {
        if (!Directory.Exists(ResultsDir)) return [];
        List<BenchResult> list = [];
        foreach (var f in Directory.EnumerateFiles(ResultsDir, "*.json").OrderDescending())
            try { if (JsonSerializer.Deserialize<BenchResult>(File.ReadAllText(f)) is { } r) list.Add(r); }
            catch (JsonException) { }
        return list;
    }

    private void KeepFailureLog()
    {
        var log = Path.Combine(SavedGamesDir, "Logs", "dcs.log");
        try { if (File.Exists(log)) File.WriteAllText(FailureLogPath, ReadShared(log)); }
        catch (IOException) { }
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    private static string ReadResource(string name)
    {
        using var s = typeof(DcsBenchmark).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Falta el recurso {name}.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Puerto libre (TCP y UDP) para la sesión local; nunca el 10308 de siempre.</summary>
    private static int FreePort()
    {
        for (var i = 0; i < 50; i++)
        {
            var port = RandomNumberGenerator.GetInt32(20000, 60000);
            try
            {
                using var tcp = new TcpListener(IPAddress.Loopback, port);
                tcp.Start();
                tcp.Stop();
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("No hay un puerto de red libre para la sesión local de DCS.");
    }
}
