using HapticSeat.Telemetry;

namespace HapticSeat.Effects;

/// <summary>
/// Tren y flaps en movimiento: zumbido suave mientras se mueven y "clonc" del tren al blocarse
/// (arriba o abajo del todo).
/// </summary>
public sealed class GearFlapsEffect : IEffect
{
    private const float Moving = 0.0005f;

    private readonly float[] _prevGear = new float[FlightState.MaxWheels];
    private readonly Envelope _clunk = new(0.08f);
    private float _prevFlaps;
    private bool _primed;
    private Oscillator _osc = new(131);

    public string Id => "gearflaps";
    public string Name => "Tren/flaps";
    public string Description => "Zumbido mientras se mueven tren y flaps; «clonc» al blocarse el tren.";
    public float Gain { get; set; } = 1;

    public void Update(FlightState s, ReadOnlySpan<FlightEvent> events, float dt, Span<float> motors)
    {
        var gearMoving = false;
        var gearLocked = false;
        for (var i = 0; i < s.WheelCount; i++)
        {
            var pos = s.GearPosition[i];
            var prev = _prevGear[i];
            if (_primed && Math.Abs(pos - prev) > Moving)
            {
                gearMoving = true;
                if (IsLocked(pos) && !IsLocked(prev)) gearLocked = true;
            }
            _prevGear[i] = pos;
        }

        var flapsMoving = _primed && Math.Abs(s.Flaps - _prevFlaps) > Moving;
        _prevFlaps = s.Flaps;
        _primed = true;

        var hum = 0.6f + 0.4f * _osc.Noise(15, dt);
        if (gearMoving) Zones.Add(motors, Zones.Seat, 0.18f * hum * Gain);
        if (flapsMoving) Zones.Add(motors, Zones.Thighs, 0.12f * hum * Gain);
        if (gearLocked) _clunk.Trigger(Zones.Buttocks, 0.6f * Gain);
        _clunk.Render(motors, dt);
    }

    private static bool IsLocked(float pos) => pos <= 0.001f || pos >= 0.999f;

    public void Reset()
    {
        _primed = false;
        _clunk.Reset();
    }
}
