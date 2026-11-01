using Vortice.DirectInput;

namespace RigControl.Core.Bindings;

/// <summary>
/// Una ejecución de perfiles ("run"): SimConnect, teclado, fuente de perfiles y el bucle del runner.
/// La consola la ejecuta bloqueando (Run); la app, en su propio hilo (Start/Stop). Las propiedades de
/// estado se pueden leer desde otro hilo para mostrarlas (son lecturas sueltas de referencias).
/// </summary>
public sealed class RunSession : IDisposable
{
    private readonly SimConnectOutput _sim = new();
    private readonly ProfileSource _source;
    private readonly bool _dryRun;
    private readonly IntPtr _window;
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private volatile CompiledProfile? _profile;

    private RunSession(ProfileSource? source, bool dryRun, IntPtr window, Func<SimConnectOutput, ProfileSource>? makeSource)
    {
        _dryRun = dryRun;
        _window = window;
        _source = source ?? makeSource!(_sim);
    }

    /// <summary>
    /// "auto" = la carpeta de perfiles según el avión; si no, la ruta o nombre de un perfil.
    /// Devuelve null si el perfil fijo no existe o tiene errores (ya se han registrado).
    /// </summary>
    /// <param name="window">Ventana para el nivel cooperativo de DirectInput (la app); sin ella, la consola.</param>
    public static RunSession? Create(string target, bool dryRun, IntPtr window = default)
    {
        if (target.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var dir = ProfileFile.ProfilesDir;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                Log.Warning($"Creada la carpeta de perfiles {dir} (está vacía: copia ahí tus .json).");
            }
            return new RunSession(null, dryRun, window, sim => new AutoProfileSource(dir, sim));
        }

        var path = ProfileFile.Resolve(target);
        var profile = ProfileLoader.LoadAndCheck(path);
        return profile is null ? null : new RunSession(new FileProfileSource(path, profile), dryRun, window, null);
    }

    public bool DryRun => _dryRun;
    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>Perfil activo ahora mismo (cambia con las recargas y con el avión).</summary>
    public CompiledProfile? Profile => _profile;

    public bool SimConnected => _sim.IsConnected;
    public string? SimName => _sim.SimName;
    public string? Aircraft => _sim.Aircraft;

    /// <summary>Se dispara (desde el hilo del bucle) cuando la ejecución termina, por Stop o por error.</summary>
    public event Action? Stopped;

    /// <summary>Ejecuta el bucle en este hilo hasta que se cancele <paramref name="stop"/>.</summary>
    public void Run(CancellationToken stop)
    {
        if (_dryRun) Log.Info("Modo simulación (--dry): las acciones solo se registran, no se envía nada.");

        using var di = DInput.DirectInput8Create();
        using var keyboard = new KeyboardOutput();
        using var dispatcher = new ActionDispatcher(keyboard, _sim, _dryRun);
        BindingRunner.Run(di, _source, new TrackingSink(this, dispatcher), stop, _window);
    }

    /// <summary>Arranca el bucle en un hilo propio (para la app).</summary>
    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() =>
        {
            try { Run(token); }
            catch (Exception ex) { Log.Error($"La ejecución se ha detenido por un error: {ex.Message}"); }
            finally { Stopped?.Invoke(); }
        })
        {
            IsBackground = true,
            Name = "RigControl runner",
            Priority = ThreadPriority.AboveNormal, // el bucle de 60 Hz no debe esperar a la interfaz
        };
        _thread.Start();
    }

    /// <summary>Pide parar y espera a que el bucle suelte teclas y dispositivos.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join(2000);
    }

    public void Dispose()
    {
        Stop();
        _source.Dispose();
        _sim.Dispose();
        _cts?.Dispose();
    }

    /// <summary>Pasa todo al dispatcher y apunta qué perfil está activo.</summary>
    private sealed class TrackingSink(RunSession session, ActionDispatcher inner) : IActionSink
    {
        public void OnAction(Binding binding, ActionPhase phase, long timeMs) => inner.OnAction(binding, phase, timeMs);
        public void Tick(long timeMs) => inner.Tick(timeMs);

        public void ProfileLoaded(CompiledProfile profile)
        {
            session._profile = profile;
            inner.ProfileLoaded(profile);
        }
    }
}
