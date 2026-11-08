using HapticSeat.Telemetry;

namespace HapticSeat.Effects;

/// <summary>
/// Un golpe corto por disparo, en el lado del arma (alas a su lado, morro hacia los muslos). La fuerza
/// depende de la cantidad de movimiento del proyectil: 7,92 mm ≈ 10, 12,7 mm ≈ 40, 20 mm ≈ 80,
/// 30 mm ≈ 165, 37 mm ≈ 650 kg·m/s.
/// </summary>
public sealed class GunEffect : IEffect
{
    private const float FullScaleMomentum = 200;

    private readonly Envelope _recoil = new(0.035f);
    private readonly float[] _weights = new float[8];

    public string Id => "guns";
    public string Name => "Armas";
    public string Description => "Un golpe por disparo en el lado del arma; más fuerte cuanto mayor el calibre.";
    public float Gain { get; set; } = 1;

    public void Update(FlightState s, ReadOnlySpan<FlightEvent> events, float dt, Span<float> motors)
    {
        foreach (var e in events)
        {
            if (e.Type != FlightEventType.GunFired) continue;
            var amount = Math.Clamp(MathF.Sqrt(e.Magnitude / FullScaleMomentum), 0.15f, 1);
            Zones.Spatial(e.Position, 0.6f, _weights);
            _recoil.Trigger(_weights, amount * Gain);
        }
        _recoil.Render(motors, dt);
    }

    public void Reset() => _recoil.Reset();
}
