using HapticSeat.Telemetry;

namespace HapticSeat.Effects;

/// <summary>Aerofrenos/frenos de picado: buffet proporcional a la apertura y a la velocidad.</summary>
public sealed class AirbrakeEffect : IEffect
{
    private Oscillator _osc = new(151);

    public string Id => "airbrake";
    public string Name => "Aerofreno";
    public string Description => "Buffet según la apertura de los aerofrenos y la velocidad.";
    public float Gain { get; set; } = 1;

    public void Update(FlightState s, ReadOnlySpan<FlightEvent> events, float dt, Span<float> motors)
    {
        if (s.Airbrake <= 0.01f) return;
        var amount = 0.4f * s.Airbrake * Math.Clamp(s.IasMs / 100, 0, 1);
        Zones.Add(motors, Zones.SeatAndBack, amount * (0.5f + 0.5f * _osc.Noise(12, dt)) * Gain);
    }

    public void Reset() { }
}
