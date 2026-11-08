using HapticSeat.Telemetry;

namespace HapticSeat.Effects;

/// <summary>
/// Carga G sostenida: presión pulsante en glúteos y espalda a partir de 2,5 G.
/// Supone que IL-2 da la aceleración vertical con la gravedad incluida (1 G en vuelo nivelado): verificar.
/// </summary>
public sealed class GForceEffect : IEffect
{
    private const float Threshold = 2.5f;
    private Oscillator _osc = new(181);

    public string Id => "gforce";
    public string Name => "Carga G";
    public string Description => "Presión pulsante en asiento y espalda a partir de 2,5 G.";
    public float Gain { get; set; } = 1;

    public void Update(FlightState s, ReadOnlySpan<FlightEvent> events, float dt, Span<float> motors)
    {
        var g = s.VerticalG;
        if (g <= Threshold) return;
        var amount = 0.45f * Math.Clamp((g - Threshold) / 5, 0, 1);
        Zones.Add(motors, Zones.SeatAndBack, amount * (0.7f + 0.3f * _osc.Sine(5, dt)) * Gain);
    }

    public void Reset() { }
}
