using System.Diagnostics;

namespace RigControl.Core.Graphics;

/// <summary>
/// Lee los ajustes gráficos actuales de DCS (options.lua + autoexec.cfg) y MSFS 2024 (UserCfg.opt), los
/// compara con un perfil y los aplica: solo con el simulador cerrado (los dos reescriben su archivo al
/// salir), con copia .bak antes de cada cambio (se guardan las 5 últimas) y escribiendo temporal + renombrar.
/// Rutas y "¿está abierto?" inyectables para las pruebas.
/// </summary>
public sealed class GraphicsManager(string? dcsConfigDir = null, string? msfsUserCfg = null, Func<string, bool>? isRunning = null)
{
    private const int KeepBackups = 5;

    /// <summary>Claves de "Video" de MSFS que son del equipo y no de la calidad (nunca se guardan en un perfil).</summary>
    private static readonly HashSet<string> MsfsMachineKeys = new(StringComparer.Ordinal)
    {
        "Video.Adapter", "Video.Monitor", "Video.Resolution", "Video.FullScreenResolution", "Video.PosX", "Video.PosY",
        "Video.WindowActive", "Video.IntelMessageBoxDisplayed", "Video.CurrentGPUDriverVersion", "Video.DoNotShowGpuDriverRequirementPopup",
    };

    private readonly Func<string, bool> _isRunning = isRunning ?? (name =>
    {
        var found = Process.GetProcessesByName(name);
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    });

    /// <summary>Config de la carpeta de DCS que se usa de verdad (Saved Games\DCS*, la de dcs.log más reciente).</summary>
    public string? DcsConfigDir => dcsConfigDir ?? FindDcsConfig();

    public string? MsfsUserCfgPath => msfsUserCfg ?? FindMsfsUserCfg();

    public static string ProcessName(string sim) => sim == "msfs" ? "FlightSimulator2024" : "DCS";

    public static string? FindDcsConfig()
    {
        var savedGames = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
        if (!Directory.Exists(savedGames)) return null;
        return Directory.EnumerateDirectories(savedGames, "DCS*")
            .Where(d => File.Exists(Path.Combine(d, "Config", "options.lua")))
            .OrderByDescending(d => File.Exists(Path.Combine(d, "Logs", "dcs.log")) ? File.GetLastWriteTimeUtc(Path.Combine(d, "Logs", "dcs.log")) : DateTime.MinValue)
            .Select(d => Path.Combine(d, "Config"))
            .FirstOrDefault();
    }

    /// <summary>UserCfg.opt de MSFS 2024: el de la Store y, si no, el de Steam.</summary>
    public static string? FindMsfsUserCfg()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft Flight Simulator 2024", "UserCfg.opt"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    // ------------------------------------------------------------------ leer

    /// <summary>
    /// Ajustes actuales que entran en un perfil. DCS: graphics.*, VR.* y autoexec.*. MSFS con VR: los de
    /// "Video" que acaban en VR + GraphicsVR.*; sin VR: el resto de "Video" (menos los del equipo) + Graphics.*.
    /// </summary>
    public Dictionary<string, string> Current(string sim, bool vr = true)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sim == "dcs")
        {
            var (options, autoexec) = LoadDcs();
            foreach (var k in options.Keys.Where(k => k.StartsWith("graphics.", StringComparison.Ordinal) || k.StartsWith("VR.", StringComparison.Ordinal)))
                result[k] = options.Get(k)!;
            foreach (var k in autoexec.Keys) result["autoexec." + k] = autoexec.Get(k)!;
        }
        else
        {
            var cfg = LoadMsfs();
            foreach (var k in cfg.Keys.Where(k => IsMsfsProfileKey(k, vr))) result[k] = cfg.Get(k)!;
        }
        return result;
    }

    private static bool IsMsfsProfileKey(string key, bool vr)
    {
        if (key.StartsWith("Video.", StringComparison.Ordinal))
            return !MsfsMachineKeys.Contains(key) && (vr ? key.EndsWith("VR", StringComparison.Ordinal) : !key.EndsWith("VR", StringComparison.Ordinal));
        return key.StartsWith(vr ? "GraphicsVR." : "Graphics.", StringComparison.Ordinal);
    }

    /// <summary>Lo que cambiaría al aplicar el perfil (solo las diferencias).</summary>
    public List<GraphicsChange> Diff(GraphicsProfile profile)
    {
        List<GraphicsChange> changes = [];
        if (profile.Sim == "dcs")
        {
            var (options, autoexec) = LoadDcs();
            foreach (var (key, wanted) in profile.Settings)
            {
                var current = key.StartsWith("autoexec.", StringComparison.Ordinal) ? autoexec.Get(key[9..]) : options.Get(key);
                if (current is null || !Same(current, wanted)) changes.Add(new GraphicsChange(key, current, wanted));
            }
        }
        else
        {
            var cfg = LoadMsfs();
            foreach (var (key, wanted) in profile.Settings)
            {
                var current = cfg.Get(key);
                if (current is null || !Same(current, wanted)) changes.Add(new GraphicsChange(key, current, wanted));
            }
        }
        return changes;
    }

    /// <summary>Iguales como texto o como número ("0.5" y "0.500000").</summary>
    private static bool Same(string a, string b)
    {
        a = a.Trim(); b = b.Trim();
        if (a == b) return true;
        return double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
               && double.TryParse(b, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
               && Math.Abs(x - y) < 1e-9;
    }

    // ------------------------------------------------------------------ aplicar

    /// <summary>
    /// Aplica el perfil (con dryRun solo calcula). Devuelve los cambios hechos. InvalidOperationException si el
    /// simulador está abierto o no se encuentra su archivo; InvalidDataException si un valor no vale.
    /// </summary>
    public List<GraphicsChange> Apply(GraphicsProfile profile, bool dryRun = false)
    {
        var changes = Diff(profile);
        if (dryRun || changes.Count == 0) return changes;
        if (_isRunning(ProcessName(profile.Sim)))
            throw new InvalidOperationException($"{profile.SimName} está abierto: ciérralo antes (al salir reescribe su configuración y perdería el cambio).");

        if (profile.Sim == "dcs")
        {
            var dir = DcsConfigDir ?? throw new InvalidOperationException("No se encuentra la carpeta Config de DCS (Saved Games).");
            var (options, autoexec) = LoadDcs();
            var optionsPath = Path.Combine(dir, "options.lua");
            var autoexecPath = Path.Combine(dir, "autoexec.cfg");
            var touchesOptions = false;
            var touchesAutoexec = false;
            foreach (var c in changes)
            {
                if (c.Key.StartsWith("autoexec.", StringComparison.Ordinal)) { autoexec.Set(c.Key[9..], c.Wanted); touchesAutoexec = true; }
                else { options.Set(c.Key, c.Wanted); touchesOptions = true; }
            }
            if (touchesOptions) Write(optionsPath, options.Text);
            if (touchesAutoexec) Write(autoexecPath, autoexec.Text);
        }
        else
        {
            var path = MsfsUserCfgPath ?? throw new InvalidOperationException("No se encuentra UserCfg.opt de MSFS 2024.");
            var cfg = LoadMsfs();
            foreach (var c in changes) cfg.Set(c.Key, c.Wanted);
            Write(path, cfg.Text);
        }
        return changes;
    }

    /// <summary>
    /// Para el Hangar: aplica el perfil por su nombre y devuelve una frase para enseñar. Nunca lanza (un fallo
    /// no debe parar la secuencia): con el simulador abierto, o sin el perfil, lo dice y no toca nada.
    /// </summary>
    public string ApplyNamed(string name, bool dryRun = false)
    {
        try
        {
            if (GraphicsFile.Load().Find(name) is not { } profile) return $"Gráficos: no existe el perfil «{name}» en graficos.json; no se tocan.";
            var changes = Apply(profile, dryRun);
            return changes.Count == 0 ? $"Gráficos «{profile.Name}»: ya estaban así."
                : $"Gráficos «{profile.Name}»: {changes.Count} cambio(s){(dryRun ? " (sin hacer)" : "")}.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return $"Gráficos: {ex.Message}";
        }
    }

    /// <summary>Perfil con los ajustes actuales.</summary>
    public GraphicsProfile Capture(string sim, string name, bool vr = true) =>
        new() { Name = name, Sim = sim, Vr = vr, Settings = Current(sim, vr) };

    /// <summary>Copias que hay de un archivo (las más nuevas primero).</summary>
    public static IEnumerable<string> Backups(string path) =>
        File.Exists(path) || Directory.Exists(Path.GetDirectoryName(path))
            ? Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".rigcontrol-*.bak").OrderDescending()
            : [];

    private static void Write(string path, string text)
    {
        if (File.Exists(path))
        {
            File.Copy(path, $"{path}.rigcontrol-{DateTime.Now:yyyyMMdd-HHmmss}.bak", overwrite: true);
            foreach (var old in Backups(path).Skip(KeepBackups)) File.Delete(old);
        }
        var tmp = path + ".rigcontrol.tmp";
        File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private (LuaOptionsFile Options, AutoexecFile Autoexec) LoadDcs()
    {
        var dir = DcsConfigDir ?? throw new InvalidOperationException("No se encuentra la carpeta Config de DCS (Saved Games).");
        var optionsPath = Path.Combine(dir, "options.lua");
        if (!File.Exists(optionsPath)) throw new InvalidOperationException($"No existe {optionsPath}.");
        return (LuaOptionsFile.Load(optionsPath), AutoexecFile.Load(Path.Combine(dir, "autoexec.cfg")));
    }

    private UserCfgFile LoadMsfs()
    {
        var path = MsfsUserCfgPath ?? throw new InvalidOperationException("No se encuentra UserCfg.opt de MSFS 2024.");
        return UserCfgFile.Load(path);
    }
}
