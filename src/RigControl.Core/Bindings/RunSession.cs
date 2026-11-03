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
    private readonly DcsBiosOutput _dcs = new();
    private readonly ProfileSource _source;
    private readonly bool _dryRun;
    private readonly IntPtr _window;
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private volatile CompiledProfile? _profile;

    private RunSession(ProfileSource? source, bool dryRun, IntPtr window, Func<SimConnectOutput, DcsBiosOutput, ProfileSource>? makeSource)
    {
        _dryRun = dryRun;
        _window = window;
        _source = source ?? makeSource!(_sim, _dcs);
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
            return new RunSession(null, dryRun, window, (sim, dcs) => new AutoProfileSource(dir, sim, dcs));
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

    /// <summary>Llegan datos de DCS-BIOS.</summary>
    public bool DcsActive => _dcs.IsActive;
    public string? DcsAircraft => _dcs.Aircraft;

    /// <summary>Se dispara (desde el hilo del bucle) cuando la ejecución termina, por Stop o por error.</summary>
    public event Action? Stopped;

    /// <summary>Ejecuta el bucle en este hilo hasta que se cancele <paramref name="stop"/>.</summary>
    public void Run(CancellationToken stop)
    {
        if (_dryRun) Log.Info("Modo simulación (--dry): las acciones solo se registran, no se envía nada.");

        using var di = DInput.DirectInput8Create();
        using var keyboard = new KeyboardOutput();
        using var dispatcher = new ActionDispatcher(keyboard, _sim, _dryRun, _dcs);
        // Las luces son realimentación local (no envían nada al simulador): funcionan también en simulación.
        using var lights = new BravoLightsOutput();
        _lights = lights;
        try { BindingRunner.Run(di, _source, new TrackingSink(this, dispatcher, lights), stop, _window); }
        finally { _lights = null; }
    }

    private volatile BravoLightsOutput? _lights;

    /// <summary>Luces del Bravo encendidas ahora mismo por el perfil.</summary>
    public BravoLed Lights => _lights?.Current ?? BravoLed.None;

    /// <summary>El perfil activo define luces del Bravo.</summary>
    public bool LightsActive => _lights?.Active ?? false;

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
        _dcs.Dispose(); // suelta los pulsadores pendientes antes de cerrar
        _cts?.Dispose();
    }

    /// <summary>Pasa todo al dispatcher, apunta qué perfil está activo y actualiza las luces del Bravo.</summary>
    private sealed class TrackingSink(RunSession session, ActionDispatcher inner, BravoLightsOutput lights) : IActionSink
    {
        public void OnAction(Binding binding, ActionPhase phase, long timeMs) => inner.OnAction(binding, phase, timeMs);

        public double[] Values => inner.Values; // para las condiciones "state"

        public void Tick(long timeMs)
        {
            inner.Tick(timeMs); // incluye SimConnect y DCS-BIOS: aquí llegan los valores nuevos
            lights.Update(inner.Values, session._sim.IsConnected || session._dcs.HasState, timeMs);
        }

        public void ProfileLoaded(CompiledProfile profile)
        {
            session._profile = profile;
            inner.ProfileLoaded(profile); // SimConnect pide las variables del perfil nuevo
            lights.SetProfile(profile);
        }
    }
}
