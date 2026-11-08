using HapticSeat.Telemetry;

namespace HapticSeat.Effects;

/// <summary>
/// Vibración de fondo de cada motor: crece con las RPM y "late" a un ritmo ligado a ellas.
/// En multimotores se nota más del lado de cada motor.
/// </summary>
public sealed class EngineEffect : IEffect
{
    private const float DefaultMaxRpm = 2700;
    private readonly Oscillator[] _osc = [new(11), new(23), new(37), new(53)];
    private readonly float[] _weights = new float[8];

    public string Id => "engine";
    public string Name => "Motor";
    public string Description => "Vibración de fondo que crece con las RPM; en multimotores, del lado de cada motor.";
    public float Gain { get; set; } = 1;

    public void Update(FlightState s, ReadOnlySpan<FlightEvent> events, float dt, Span<float> motors)
    {
        for (var i = 0; i < s.EngineCount; i++)
        {
            var rpm = s.EngineRpm[i];
            if (rpm < 50) continue;

            var max = s.EngineMaxRpm[i] > 0 ? s.EngineMaxRpm[i] : DefaultMaxRpm;
            var norm = Math.Clamp(rpm / max, 0, 1.2f);
            var level = 0.10f + 0.25f * norm;
            var throbHz = Math.Clamp(rpm / 60 / 8, 2, 12);
            var throb = 0.7f + 0.3f * _osc[i].Sine(throbHz, dt);

            Zones.Spatial(s.EnginePos[i], 0.4f, _weights);
            for (var m = 0; m < motors.Length; m++)
                motors[m] += Zones.SeatAndBack[m] * _weights[m] * level * throb * Gain / Math.Max(1, s.EngineCount * 0.7f);
        }
    }

    public void Reset() { }
}
