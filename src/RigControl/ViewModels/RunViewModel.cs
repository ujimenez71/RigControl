using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using RigControl.Core;
using RigControl.Core.Bindings;

namespace RigControl.ViewModels;

public sealed record LogItem(string Time, LogLevel Level, string Text);

/// <summary>
/// Pantalla Ejecución: lo mismo que "RigProbe run" (auto o un perfil fijo) con estado visible.
/// El bucle va en su propio hilo (RunSession); los mensajes del núcleo llegan por una cola y se
/// vuelcan a la lista cada 50 ms en el hilo de la interfaz, así una ráfaga no bloquea nada.
/// </summary>
public sealed class RunViewModel : ObservableObject, IDisposable
{
    public const string AutoTarget = "Automático (según el avión de MSFS)";
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
        if (s is null)
        {
            SimConnected = false;
            SimText = "—";
            AircraftText = "—";
            AircraftPath = "";
            ProfileText = "Detenido";
            ProfileDetail = "";
            return;
        }

        SimConnected = s.SimConnected;
        SimText = s.SimConnected ? $"Conectado · {s.SimName}" : "Esperando a MSFS…";

        var aircraft = s.Aircraft;
        AircraftPath = aircraft ?? "";
        AircraftText = aircraft is null ? (s.SimConnected ? "Sin avión (¿en el menú?)" : "—") : ShortAircraft(aircraft);

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
