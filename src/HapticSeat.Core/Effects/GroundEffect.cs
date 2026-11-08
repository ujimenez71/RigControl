using HapticSeat.Telemetry;

namespace HapticSeat.Effects;

/// <summary>
/// Rodadura por pista (cada rueda en su lado, más rugosa con la velocidad) y golpe de toma de contacto
/// cuando una rueda pasa de estar en el aire a tener carga, más fuerte cuanto mayor la G vertical.
/// </summary>
public sealed class GroundEffect : IEffect
{
    private const float OnGround = 0.005f;
    private const float Rolling = 1; // m/s

    private readonly Oscillator[] _osc = [new(101), new(103), new(107), new(109)];
    private readonly float[] _prevLoad = new float[FlightState.MaxWheels];
    private readonly float[] _weights = new float[8];
    private readonly Envelope _touchdown = new(0.15f);
    private bool _primed;

    public string Id => "ground";
    public string Name => "Suelo";
    public string Description => "Rodadura por pista en el lado de cada rueda y golpe al tocar tierra.";
    public float Gain { get; set; } = 1;

    public void Update(FlightState s, ReadOnlySpan<FlightEvent> events, float dt, Span<float> motors)
    {
        var speed = Math.Clamp(s.IasMs / 50, 0, 1);

        // La rodadura se promedia entre las ruedas apoyadas: con más ruedas no debe vibrar más,
        // y así la toma de contacto siempre destaca sobre ella
        var onGround = 0;
        for (var i = 0; i < s.WheelCount; i++)
            if (s.WheelLoad[i] > OnGround) onGround++;

        for (var i = 0; i < s.WheelCount; i++)
        {
            var load = s.WheelLoad[i];
            Zones.Spatial(s.WheelPos[i], 0.7f, _weights);

            if (load > OnGround)
            {
                // Parado no hay rodadura: si no, el asiento vibraría sin parar con el avión aparcado
                if (s.IasMs > Rolling)
                {
                    var hz = 6 + 20 * speed;
                    var rough = _osc[i].Noise(hz, dt);
                    var amount = (0.05f + 0.25f * speed) * Math.Clamp(0.4f + load, 0, 1) * rough / onGround;
                    for (var m = 0; m < motors.Length; m++) motors[m] += Zones.Seat[m] * _weights[m] * amount * Gain;
                }

                // Toma de contacto: rueda que estaba en el aire y viene con velocidad
                if (_primed && _prevLoad[i] <= OnGround && s.IasMs > 15)
                {
                    var hit = Math.Clamp(0.45f + 0.35f * (s.VerticalG - 1), 0.45f, 1);
                    _touchdown.Trigger(_weights, hit * Gain);
                }
            }
            _prevLoad[i] = load;
        }

        _primed = true;
        _touchdown.Render(motors, dt);
    }

    public void Reset()
    {
        _primed = false;
        Array.Clear(_prevLoad);
        _touchdown.Reset();
    }
}
