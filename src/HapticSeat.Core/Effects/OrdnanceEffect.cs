using HapticSeat.Telemetry;

namespace HapticSeat.Effects;

/// <summary>Suelta de bombas (golpe largo, según la masa) y salida de cohetes (golpe seco), en su lado.</summary>
public sealed class OrdnanceEffect : IEffect
{
    private readonly Envelope _bombs = new(0.25f);
    private readonly Envelope _rockets = new(0.12f);
    private readonly float[] _weights = new float[8];

    public string Id => "ordnance";
    public string Name => "Bombas/cohetes";
    public string Description => "Suelta de bombas y salida de cohetes, en su lado.";
    public float Gain { get; set; } = 1;

    public void Update(FlightState s, ReadOnlySpan<FlightEvent> events, float dt, Span<float> motors)
    {
        foreach (var e in events)
        {
            if (e.Type is not (FlightEventType.BombRelease or FlightEventType.RocketLaunch)) continue;
            var amount = Math.Clamp(0.4f + MathF.Sqrt(Math.Max(0, e.Magnitude) / 1000), 0.4f, 1);
            Zones.Spatial(e.Position, 0.6f, _weights);
            (e.Type == FlightEventType.BombRelease ? _bombs : _rockets).Trigger(_weights, amount * Gain);
        }
        _bombs.Render(motors, dt);
        _rockets.Render(motors, dt);
    }

    public void Reset()
    {
        _bombs.Reset();
        _rockets.Reset();
    }
}
