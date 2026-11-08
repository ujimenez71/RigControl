using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HapticSeat.Effects;
using HapticSeat.Hf8;
using HapticSeat.Profiles;
using HapticSeat.Telemetry;

namespace HapticSeat;

public enum SeatState
{
    /// <summary>No hay HF8 enchufado (se sigue buscando).</summary>
    NotFound,

    /// <summary>Lo tiene otro programa, normalmente HFS.</summary>
    Busy,

    Connected,
}

/// <summary>Copia del estado del host para la interfaz (se rellena sin asignar memoria).</summary>
public sealed class HostSnapshot
{
    public readonly FlightState Flight = new();
    public readonly float[] Motors = new float[Hf8Motors.Count];
    public readonly long[] EventCounts = new long[Enum.GetValues<FlightEventType>().Length];
    public SeatState Seat;
    public string? SeatPort;
    public long Packets;

    /// <summary>Efecto que suena en modo Probar, o null.</summary>
    public string? TestingEffect;

    /// <summary>Motor en calibración (-1 = ninguno) y nivel que suena ahora (0 en las pausas).</summary>
    public int ThresholdMotor = -1;
    public int ThresholdLevel;
}

/// <summary>
/// Corazón de la app: bucle a ~100 Hz telemetría → efectos → HF8 en su propio hilo. Reconecta el cojín
/// solo (desenchufado, HFS abierto). La interfaz le manda órdenes con métodos seguros entre hilos
/// (se ejecutan en el bucle) y lee el estado con <see cref="Snapshot"/>.
/// </summary>
public sealed class HapticHost : IDisposable
{
    private const int FrameMs = 10;
    private const int ReconnectMs = 2000;

    // Calibración: 1 s de espera, luego pulsos de 1 s + 0,5 s de pausa, niveles 5, 10 … 80
    private const double RampDelayS = 1;
    private const double PulseS = 1;
    private const double PulsePeriodS = 1.5;
    private const int RampSteps = 16;
    private const int RampStep = 5;

    /// <summary>Tiempo de reacción que se descuenta al pulsar "lo noto".</summary>
    private const double ReactionS = 0.3;

    private readonly ITelemetrySource _source;
    private readonly EffectEngine _engine;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _snapshotLock = new();
    private readonly HostSnapshot _shared = new();
    private readonly FlightState _state = new();
    private readonly List<FlightEvent> _events = new(64);
    private readonly List<FlightEvent> _demoEvents = new(16);
    private readonly float[] _motors = new float[Hf8Motors.Count];
    private readonly byte[] _levels = new byte[Hf8Motors.Count];
    private readonly long[] _eventCounts;
    private byte[] _minLevels;
    private Thread? _thread;
    private volatile bool _running;

    private Hf8Device? _hf8;
    private SeatState _seat = SeatState.NotFound;
    private long _nextReconnectMs;

    private EffectDemo? _demo;

    private int _thresholdMotor = -1;
    private double _thresholdStartS;

    private int _pulseMotor = -1;
    private double _pulseUntilS;

    public HapticHost(ITelemetrySource source, EffectEngine engine, byte[] minLevels)
    {
        _source = source;
        _engine = engine;
        _minLevels = [.. minLevels];
        _eventCounts = new long[_shared.EventCounts.Length];
    }

    public IReadOnlyList<IEffect> Effects => _engine.Effects;

    /// <summary>Error al abrir la telemetría (p. ej. puerto UDP en uso exclusivo), o null.</summary>
    public string? SourceError { get; private set; }

    public void Start()
    {
        try
        {
            _source.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            // Sin telemetría el cojín sigue funcionando (pruebas, calibración)
            SourceError = $"No se pudo escuchar {_source.Name}: {ex.Message}";
        }
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "HapticHost", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    // ------------------------------------------------------ órdenes (desde cualquier hilo)

    /// <summary>Aplica ganancias y efectos activos del perfil (se copia: la interfaz puede seguir editándolo).</summary>
    public void ApplyProfile(Profile profile)
    {
        var p = profile.Clone();
        _commands.Enqueue(() =>
        {
            _engine.MasterGain = Math.Clamp(p.MasterGain, 0, 200) / 100f;
            for (var i = 0; i < _engine.Effects.Count; i++)
            {
                var setting = p.For(_engine.Effects[i].Id);
                _engine.Effects[i].Gain = Math.Clamp(setting.Gain, 0, 200) / 100f;
                _engine.SetEnabled(i, setting.Enabled);
            }
        });
    }

    public void SetMinLevels(byte[] levels)
    {
        byte[] copy = [.. levels];
        _commands.Enqueue(() => _minLevels = copy);
    }

    /// <summary>Hace sonar un efecto unos segundos con un vuelo simulado. Solo si no se está volando.</summary>
    public void TestEffect(string effectId) => _commands.Enqueue(() =>
    {
        if (_state.Active || _engine.IndexOf(effectId) < 0) return;
        StopCalibration();
        _engine.ResetAll();
        _demo = new EffectDemo(effectId);
    });

    /// <summary>Empieza la rampa de calibración en un motor (pulsos de nivel creciente).</summary>
    public void StartThreshold(int motor) => _commands.Enqueue(() =>
    {
        if (_state.Active || motor is < 0 or >= Hf8Motors.Count) return;
        StopCalibration();
        _demo = null;
        _thresholdMotor = motor;
        _thresholdStartS = _clock.Elapsed.TotalSeconds;
    });

    /// <summary>
    /// "Lo noto": para la rampa y devuelve el nivel del pulso que se estaba notando, descontando el tiempo
    /// de reacción (si se pulsa en una pausa, cuenta el pulso anterior). 0 si aún no había empezado.
    /// </summary>
    public int StopThreshold()
    {
        if (Volatile.Read(ref _thresholdMotor) < 0) return 0;
        var level = RampLevelFelt(_clock.Elapsed.TotalSeconds - Volatile.Read(ref _thresholdStartS) - ReactionS);
        _commands.Enqueue(StopCalibration);
        return level;
    }

    /// <summary>Un pulso de 1 s al nivel mínimo de un motor, para comprobar que apenas se nota.</summary>
    public void PulseMinimum(int motor) => _commands.Enqueue(() =>
    {
        if (_state.Active || motor is < 0 or >= Hf8Motors.Count) return;
        StopCalibration();
        _demo = null;
        _pulseMotor = motor;
        _pulseUntilS = _clock.Elapsed.TotalSeconds + 1;
    });

    public void Snapshot(HostSnapshot into)
    {
        lock (_snapshotLock)
        {
            _shared.Flight.CopyTo(into.Flight);
            _shared.Motors.CopyTo(into.Motors, 0);
            _shared.EventCounts.CopyTo(into.EventCounts, 0);
            into.Seat = _shared.Seat;
            into.SeatPort = _shared.SeatPort;
            into.Packets = _shared.Packets;
            into.TestingEffect = _shared.TestingEffect;
            into.ThresholdMotor = _shared.ThresholdMotor;
            into.ThresholdLevel = _shared.ThresholdLevel;
        }
    }

    // ------------------------------------------------------ bucle

    private void Loop()
    {
        var last = _clock.Elapsed.TotalSeconds;
        while (_running)
        {
            var now = _clock.Elapsed.TotalSeconds;
            var dt = (float)Math.Clamp(now - last, 0.001, 0.1);
            last = now;

            while (_commands.TryDequeue(out var command)) command();
            EnsureSeat();

            _source.Snapshot(_state);
            _events.Clear();
            _source.DrainEvents(_events);
            foreach (var e in _events) _eventCounts[(int)e.Type]++;

            // Al empezar a volar se cancela cualquier prueba o calibración
            if (_state.Active && (_demo is not null || _thresholdMotor >= 0 || _pulseMotor >= 0))
            {
                _demo = null;
                _engine.Solo = -1;
                StopCalibration();
            }

            var thresholdLevel = 0;
            if (_thresholdMotor >= 0)
                thresholdLevel = RenderThreshold(now);
            else if (_pulseMotor >= 0)
                RenderPulse(now);
            else
                RenderEffects(dt);

            _hf8?.SetMotors(_levels);
            Publish(thresholdLevel);
            Thread.Sleep(FrameMs);
        }
    }

    private void RenderEffects(float dt)
    {
        var state = _state;
        var events = _events;
        if (_demo is not null)
        {
            _demoEvents.Clear();
            state = _demo.Step(dt, _demoEvents);
            events = _demoEvents;
            _engine.Solo = _engine.IndexOf(_demo.EffectId);
            if (_demo.Finished)
            {
                _demo = null;
                _engine.Solo = -1;
                _engine.ResetAll();
            }
        }

        _engine.Update(state, CollectionsMarshal.AsSpan(events), dt, _motors);
        EffectEngine.ToLevels(_motors, _levels, _minLevels);
    }

    private int RenderThreshold(double now)
    {
        var t = now - _thresholdStartS;
        Array.Clear(_levels);
        Array.Clear(_motors);
        if (t > RampDelayS + RampSteps * PulsePeriodS)
        {
            StopCalibration(); // terminó sin que se pulsara "lo noto"
            return 0;
        }

        var level = RampLevelPlaying(t);
        _levels[_thresholdMotor] = (byte)level;
        _motors[_thresholdMotor] = level / 255f;
        return level;
    }

    private void RenderPulse(double now)
    {
        Array.Clear(_levels);
        Array.Clear(_motors);
        if (now >= _pulseUntilS)
        {
            _pulseMotor = -1;
            return;
        }
        _levels[_pulseMotor] = Math.Max((byte)1, _minLevels[_pulseMotor]);
        _motors[_pulseMotor] = _levels[_pulseMotor] / 255f;
    }

    /// <summary>Nivel que suena en el instante t de la rampa (0 en la espera inicial y en las pausas).</summary>
    internal static int RampLevelPlaying(double t)
    {
        var s = t - RampDelayS;
        if (s < 0) return 0;
        var k = (int)(s / PulsePeriodS);
        if (k >= RampSteps || s - k * PulsePeriodS >= PulseS) return 0;
        return (k + 1) * RampStep;
    }

    /// <summary>Nivel del pulso que se estaba notando en el instante t (en una pausa, el pulso anterior).</summary>
    internal static int RampLevelFelt(double t)
    {
        var s = t - RampDelayS;
        if (s < 0) return 0;
        var k = Math.Min((int)(s / PulsePeriodS), RampSteps - 1);
        return (k + 1) * RampStep;
    }

    private void StopCalibration()
    {
        _thresholdMotor = -1;
        _pulseMotor = -1;
    }

    /// <summary>Abre el HF8 si no lo está (cada 2 s) y lo cierra si ha fallado (desenchufado).</summary>
    private void EnsureSeat()
    {
        if (_hf8 is { Faulted: true })
        {
            _hf8.Dispose();
            _hf8 = null;
            _seat = SeatState.NotFound;
        }

        if (_hf8 is not null || _clock.ElapsedMilliseconds < _nextReconnectMs) return;
        _nextReconnectMs = _clock.ElapsedMilliseconds + ReconnectMs;

        _hf8 = Hf8Device.TryOpen(out var error);
        _seat = _hf8 is not null ? SeatState.Connected
            : error == Hf8OpenError.Busy ? SeatState.Busy
            : SeatState.NotFound;
    }

    private void Publish(int thresholdLevel)
    {
        lock (_snapshotLock)
        {
            _state.CopyTo(_shared.Flight);
            _motors.CopyTo(_shared.Motors, 0);
            _eventCounts.CopyTo(_shared.EventCounts, 0);
            _shared.Seat = _seat;
            _shared.SeatPort = _hf8?.PortName;
            _shared.Packets = _source.PacketCount;
            _shared.TestingEffect = _demo?.EffectId;
            _shared.ThresholdMotor = _thresholdMotor;
            _shared.ThresholdLevel = thresholdLevel;
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
        _hf8?.Dispose(); // deja los motores a 0
        _source.Dispose();
    }
}
