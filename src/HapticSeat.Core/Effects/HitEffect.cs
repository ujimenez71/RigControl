using HapticSeat.Telemetry;

namespace HapticSeat.Effects;

/// <summary>
/// Impactos recibidos, en la zona del avión donde pegan, con fuerza logarítmica. Los eventos de daño
/// (IL-2 los repite tras cada impacto) añaden un golpe menor. La escala de fuerza está sin calibrar.
/// </summary>
public sealed class HitEffect : IEffect
{
    /// <summary>Fuerza de IL-2 que se considera impacto máximo.</summary>
    public float FullScaleForce { get; set; } = 10000;

    private readonly Envelope _hits = new(0.15f);
    private readonly float[] _weights = new float[8];

    public string Id => "hits";
    public string Name => "Impactos";
    public string Description => "Impactos recibidos, en la zona del avión donde pegan.";
    public float Gain { get; set; } = 1;

    public void Update(FlightState s, ReadOnlySpan<FlightEvent> events, float dt, Span<float> motors)
    {
        foreach (var e in events)
        {
            float amount;
            if (e.Type == FlightEventType.Hit)
                amount = Math.Clamp(MathF.Log10(1 + e.Magnitude) / MathF.Log10(1 + FullScaleForce), 0.3f, 1);
            else if (e.Type == FlightEventType.Damage)
                amount = 0.3f;
            else
                continue;

            Zones.Spatial(e.Position, 0.8f, _weights);
            _hits.Trigger(_weights, amount * Gain);
        }
        _hits.Render(motors, dt);
    }

    public void Reset() => _hits.Reset();
}
