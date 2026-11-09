using System.Numerics;
using HapticSeat.Telemetry;

namespace HapticSeat.Effects;

/// <summary>
/// Vuelo simulado de unos segundos que hace sonar un efecto concreto (botón Probar de la interfaz),
/// con la ganancia del perfil activo. Así se ajusta cada efecto sin abrir el simulador.
/// </summary>
public sealed class EffectDemo
{
    public const float DurationSeconds = 2.5f;

    private static readonly Vector3 LeftWing = new(0.5f, 0, -2.2f);
    private static readonly Vector3 RightWing = new(0.5f, 0, 2.2f);

    private readonly FlightState _s = new() { Connected = true, IasMs = 100, Accel = new Vector3(0, 9.81f, 0) };
    private float _t;
    private float _nextShot;

    public string EffectId { get; }

    public bool Finished => _t >= DurationSeconds;

    public EffectDemo(string effectId)
    {
        EffectId = effectId;
        switch (effectId)
        {
            case "engine":
                _s.EngineCount = 1;
                _s.EngineMaxRpm[0] = 2800;
                _s.EnginePos[0] = new Vector3(2, 0, 0);
                break;
            case "rotor":
                _s.Rotorcraft = true;
                _s.RotorSpeed = 1;
                _s.BladePassHz = 19;
                break;
            case "ground" or "gearflaps":
                _s.WheelCount = 3;
                _s.WheelPos[0] = new Vector3(0.5f, -1.5f, -1.2f);
                _s.WheelPos[1] = new Vector3(0.5f, -1.5f, 1.2f);
                _s.WheelPos[2] = new Vector3(-5, -0.5f, 0);
                break;
        }
    }

    /// <summary>Avanza la demo y devuelve el estado simulado; añade a <paramref name="events"/> los de este paso.</summary>
    public FlightState Step(float dt, List<FlightEvent> events)
    {
        var t = _t;
        var u = Math.Clamp(t / DurationSeconds, 0, 1);
        _t += dt;

        switch (EffectId)
        {
            case "engine": // de ralentí a potencia máxima
                _s.EngineRpm[0] = 800 + 1900 * u;
                break;
            case "rotor": // de estacionario a 70 km/h: pasa por la sustentación traslacional
                _s.IasMs = 20 * u;
                break;
            case "buffet": // entra en pérdida y sale
                _s.BuffetFrequency = 10;
                _s.BuffetAmplitude = MathF.Sin(MathF.PI * u);
                break;
            case "ground": // toma de contacto a 160 km/h y frenada
                _s.IasMs = 45 * (1 - 0.7f * u);
                var load = t < 0.3f ? 0 : 0.6f;
                _s.WheelLoad[0] = _s.WheelLoad[1] = load;
                _s.WheelLoad[2] = t < 0.8f ? 0 : 0.3f;
                break;
            case "gearflaps": // tren y flaps bajando, tren blocado al final
                var gear = Math.Clamp(t / 2f, 0, 1);
                _s.GearPosition[0] = _s.GearPosition[1] = _s.GearPosition[2] = gear;
                _s.Flaps = Math.Clamp(t / 1.5f, 0, 1);
                break;
            case "airbrake":
                _s.Airbrake = Math.Clamp(t * 2, 0, 1);
                break;
            case "gforce": // tirón hasta 6 G
                _s.Accel = new Vector3(0, 9.81f * (1 + 5 * MathF.Sin(MathF.PI * u)), 0);
                break;
            case "guns": // ametralladoras en las alas y luego cañón en el morro
                if (t >= _nextShot)
                {
                    _nextShot = t + 0.07f;
                    if (t < 1.3f)
                    {
                        events.Add(new FlightEvent(FlightEventType.GunFired, 40, LeftWing));
                        events.Add(new FlightEvent(FlightEventType.GunFired, 40, RightWing));
                    }
                    else
                        events.Add(new FlightEvent(FlightEventType.GunFired, 80, new Vector3(1.5f, 0.3f, 0)));
                }
                break;
            case "hits": // detrás a la derecha, delante a la izquierda, centro
                Once(t, dt, 0.2f, events, new FlightEvent(FlightEventType.Hit, 2000, new Vector3(-3, 0, 1.5f)));
                Once(t, dt, 0.9f, events, new FlightEvent(FlightEventType.Hit, 8000, new Vector3(2.5f, 0, -1.5f)));
                Once(t, dt, 1.6f, events, new FlightEvent(FlightEventType.Hit, 500, Vector3.Zero));
                break;
            case "ordnance": // bomba bajo el fuselaje y cohetes en cada ala
                Once(t, dt, 0.3f, events, new FlightEvent(FlightEventType.BombRelease, 250, new Vector3(0, -1, 0)));
                Once(t, dt, 1.3f, events, new FlightEvent(FlightEventType.RocketLaunch, 60, LeftWing));
                Once(t, dt, 1.6f, events, new FlightEvent(FlightEventType.RocketLaunch, 60, RightWing));
                break;
        }
        return _s;
    }

    private static void Once(float t, float dt, float at, List<FlightEvent> events, FlightEvent e)
    {
        if (t <= at && at < t + dt) events.Add(e);
    }
}
