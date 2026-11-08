using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using RigControl.Core;
using RigControl.Core.Bindings;

namespace RigControl.ViewModels;

public sealed record LogItem(string Time, LogLevel Level, string Text);

/// <summary>Una luz del Bravo en el panel: color según el tipo (autopiloto, verde, rojo, ámbar).</summary>
public sealed class LightViewModel(BravoLed led, string label, string kind) : ObservableObject
{
    private bool _isOn;
    public BravoLed Led { get; } = led;
    public string Label { get; } = label;
    public string Kind { get; } = kind;
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }
}

/// <summary>
/// Pantalla Ejecución: lo mismo que "RigProbe run" (auto o un perfil fijo) con estado visible.
/// El bucle va en su propio hilo (RunSession); los mensajes del núcleo llegan por una cola y se
/// vuelcan a la lista cada 50 ms en el hilo de la interfaz, así una ráfaga no bloquea nada.
/// </summary>
public sealed class RunViewModel : ObservableObject, IDisposable
{
    public const string AutoTarget = "Automático (según el avión cargado)";
    private const int MaxLog = 1000;

    private readonly ConcurrentQueue<LogEntry> _incoming = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
    private IntPtr _window;
    private RunSession? _session;
    private int _statusCountdown;

    private string? _selectedTarget = AutoTarget;
    private bool _dryRun;
    private bool _isRunning;
    private string _simText = "—";
    private string _aircraftText = "—";
    private string _aircraftPath = "";
    private string _profileText = "—";
    private string _profileDetail = "";
    private bool _simConnected;

    public RunViewModel()
    {
        _timer.Tick += (_, _) => Drain();
        // Todo lo que el núcleo registre (desde cualquier hilo) entra por aquí.
        Log.Sink = e => _incoming.Enqueue(e);
        RefreshTargets();
    }

    /// <summary>Las 28 luces del Bravo, en el orden del panel físico.</summary>
    public ObservableCollection<LightViewModel> Lights { get; } =
    [
        new(BravoLed.Hdg, "HDG", "ap"), new(BravoLed.Nav, "NAV", "ap"), new(BravoLed.Apr, "APR", "ap"),
        new(BravoLed.Rev, "REV", "ap"), new(BravoLed.Alt, "ALT", "ap"), new(BravoLed.Vs, "VS", "ap"),
        new(BravoLed.Ias, "IAS", "ap"), new(BravoLed.Autopilot, "AP", "ap"),
        new(BravoLed.GearLeftGreen, "L ▼", "green"), new(BravoLed.GearNoseGreen, "N ▼", "green"),
        new(BravoLed.GearRightGreen, "R ▼", "green"), new(BravoLed.GearLeftRed, "L", "red"),
        new(BravoLed.GearNoseRed, "N", "red"), new(BravoLed.GearRightRed, "R", "red"),
        new(BravoLed.MasterWarning, "MASTER WARN", "red"), new(BravoLed.EngineFire, "ENG FIRE", "red"),
        new(BravoLed.LowOilPressure, "LOW OIL", "amber"), new(BravoLed.LowFuelPressure, "LOW FUEL", "amber"),
        new(BravoLed.AntiIce, "ANTI ICE", "amber"), new(BravoLed.StarterEngaged, "STARTER", "amber"),
        new(BravoLed.Apu, "APU", "amber"), new(BravoLed.MasterCaution, "MASTER CAUT", "amber"),
        new(BravoLed.Vacuum, "VACUUM", "amber"), new(BravoLed.LowHydPressure, "LOW HYD", "amber"),
        new(BravoLed.AuxFuelPump, "AUX FUEL", "amber"), new(BravoLed.ParkingBrake, "PARK BRAKE", "amber"),
        new(BravoLed.LowVolts, "LOW VOLTS", "amber"), new(BravoLed.Door, "DOOR", "amber"),
    ];

    private bool _lightsActive;

    /// <summary>El perfil activo define luces (si no, el panel se oculta).</summary>
    public bool LightsActive { get => _lightsActive; private set => Set(ref _lightsActive, value); }

    public ObservableCollection<string> Targets { get; } = [];
    public ObservableCollection<LogItem> Entries { get; } = [];

    public string? SelectedTarget { get => _selectedTarget; set => Set(ref _selectedTarget, value); }
    public bool DryRun { get => _dryRun; set => Set(ref _dryRun, value); }

    public bool IsRunning
    {
        get => _isRunning;
        private set { if (Set(ref _isRunning, value)) { Raise(nameof(IsIdle)); Raise(nameof(StartStopText)); } }
    }
    public bool IsIdle => !_isRunning;
    public string StartStopText => _isRunning ? "■  Detener" : "▶  Iniciar";

    public bool SimConnected { get => _simConnected; private set => Set(ref _simConnected, value); }
    public string SimText { get => _simText; private set => Set(ref _simText, value); }

    private string _dcsText = "";
    public string DcsText { get => _dcsText; private set => Set(ref _dcsText, value); }
    public string AircraftText { get => _aircraftText; private set => Set(ref _aircraftText, value); }
    public string AircraftPath { get => _aircraftPath; private set => Set(ref _aircraftPath, value); }
    public string ProfileText { get => _profileText; private set => Set(ref _profileText, value); }
    public string ProfileDetail { get => _profileDetail; private set => Set(ref _profileDetail, value); }

    public string ProfilesDir => ProfileFile.ProfilesDir;

    public void Initialize(IntPtr window)
    {
        _window = window;
        _timer.Start();
    }

    /// <summary>Opciones: automático + cada perfil de la carpeta.</summary>
    public void RefreshTargets()
    {
        var selected = SelectedTarget;
        Targets.Clear();
        Targets.Add(AutoTarget);
        if (Directory.Exists(ProfileFile.ProfilesDir))
            foreach (var f in Directory.GetFiles(ProfileFile.ProfilesDir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
                Targets.Add(Path.GetFileName(f));
        SelectedTarget = selected is not null && Targets.Contains(selected) ? selected : AutoTarget;
    }

    public void StartStop()
    {
        if (IsRunning) Stop();
        else Start();
    }

    /// <summary>Desde el Hangar: ejecución real con el perfil según el avión. Si ya hay una en marcha, no la toca.</summary>
    public void StartAuto()
    {
        if (IsRunning) return;
        SelectedTarget = AutoTarget;
        DryRun = false;
        Start();
    }

    public void ClearLog() => Entries.Clear();

    public void Dispose()
    {
        Stop();
        _timer.Stop();
    }

    private void Start()
    {
        var target = SelectedTarget == AutoTarget || SelectedTarget is null
            ? "auto"
            : Path.Combine(ProfileFile.ProfilesDir, SelectedTarget);

        Log.Info($"---- Iniciando: {(target == "auto" ? "automático" : SelectedTarget)}{(DryRun ? " (simulación)" : "")} ----");
        var session = RunSession.Create(target, DryRun, _window);
        if (session is null)
        {
            Log.Error("No se pudo iniciar: corrige el perfil (los errores están arriba).");
            return;
        }

        session.Stopped += () => _incoming.Enqueue(new LogEntry(DateTime.Now, LogLevel.Info, "---- Detenido ----"));
        _session = session;
        session.Start();
        IsRunning = true;
        RefreshStatus();
    }

    private void Stop()
    {
        if (_session is null) return;
        _session.Dispose(); // para el bucle, suelta teclas y dispositivos
        _session = null;
        IsRunning = false;
        RefreshStatus();
    }

    private void Drain()
    {
        while (_incoming.TryDequeue(out var e))
        {
            var text = e.Text.Trim('\n', '\r');
            if (text.Length == 0) continue;
            Entries.Insert(0, new LogItem(e.Time.ToString("HH:mm:ss.fff"), e.Level, text));
        }
        while (Entries.Count > MaxLog) Entries.RemoveAt(Entries.Count - 1);

        // El hilo del runner pudo terminar solo (error): reflejarlo.
        if (_session is { IsRunning: false } && IsRunning) Stop();

        // Estado cada ~250 ms (5 ticks).
        if (--_statusCountdown <= 0)
        {
            _statusCountdown = 5;
            RefreshStatus();
        }
    }

    private void RefreshStatus()
    {
        var s = _session;
        var lit = s?.Lights ?? BravoLed.None;
        foreach (var l in Lights) l.IsOn = (lit & l.Led) != 0;
        LightsActive = s?.LightsActive ?? false;

        if (s is null)
        {
            SimConnected = false;
            SimText = "—";
            DcsText = "";
            AircraftText = "—";
            AircraftPath = "";
            ProfileText = "Detenido";
            ProfileDetail = "";
            return;
        }

        SimConnected = s.SimConnected || s.DcsActive;
        SimText = s.SimConnected ? $"MSFS · {s.SimName}" : s.DcsActive ? "DCS (DCS-BIOS)" : "Esperando a MSFS o DCS…";
        DcsText = s.DcsActive ? $"DCS-BIOS: recibiendo datos" : "DCS-BIOS: sin datos";

        var aircraft = s.Aircraft;
        AircraftPath = aircraft ?? (s.DcsAircraft is { } d ? $"DCS: {d}" : "");
        AircraftText = aircraft is not null ? ShortAircraft(aircraft)
            : s.DcsAircraft is { } dcsName ? dcsName
            : s.SimConnected ? "Sin avión (¿en el menú?)" : "—";

        var p = s.Profile;
        if (p is null || p.Bindings.Length == 0 && p.SourcePath is null)
        {
            ProfileText = "Ningún perfil";
            ProfileDetail = "Ningún perfil encaja con el avión: mandos sin bindings.";
        }
        else
        {
            ProfileText = p.Name;
            ProfileDetail = $"{(p.SourcePath is null ? "" : Path.GetFileName(p.SourcePath) + " · ")}{p.Bindings.Length} bindings" +
                            (s.DryRun ? " · simulación" : "");
        }
    }

    /// <summary>"simobjects\airplanes\asobo_c172sp_g1000\presets\..." -> "asobo_c172sp_g1000".</summary>
    private static string ShortAircraft(string path)
    {
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 2 && parts[0] == "simobjects" ? parts[2] : path;
    }
}
