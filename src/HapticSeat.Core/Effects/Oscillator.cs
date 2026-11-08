namespace HapticSeat.Effects;

/// <summary>
/// Moduladores de intensidad. Los motores del HF8 solo aceptan intensidad, así que la "frecuencia" que se
/// siente sale de variar esa intensidad en el tiempo (hasta ~50 Hz con la salida a 100 Hz).
/// Todos devuelven 0..1.
/// </summary>
public struct Oscillator(uint seed = 0x9E3779B9)
{
    private float _phase;
    private float _hold;
    private uint _rng = seed == 0 ? 1 : seed;

    /// <summary>Pulso suave (seno).</summary>
    public float Sine(float hz, float dt)
    {
        Advance(hz, dt);
        return 0.5f + 0.5f * MathF.Sin(_phase * MathF.Tau);
    }

    /// <summary>Rugosidad: un valor aleatorio nuevo en cada ciclo (buffet, pista).</summary>
    public float Noise(float hz, float dt)
    {
        if (Advance(hz, dt)) _hold = NextRandom();
        return _hold;
    }

    /// <summary>Avanza la fase; true si ha completado un ciclo.</summary>
    private bool Advance(float hz, float dt)
    {
        _phase += hz * dt;
        if (_phase < 1) return false;
        _phase -= MathF.Floor(_phase);
        return true;
    }

    private float NextRandom()
    {
        // xorshift32: determinista y sin asignaciones
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return (_rng & 0xFFFFFF) / (float)0xFFFFFF;
    }
}

/// <summary>Golpes que decaen: cada motor guarda su nivel y cae exponencialmente.</summary>
public sealed class Envelope(float decaySeconds)
{
    private readonly float[] _level = new float[Hf8.Hf8Motors.Count];

    public float DecaySeconds { get; set; } = decaySeconds;

    /// <summary>Dispara un golpe; si ya hay uno más fuerte en un motor, se queda el mayor.</summary>
    public void Trigger(ReadOnlySpan<float> weights, float amount)
    {
        for (var m = 0; m < _level.Length; m++) _level[m] = Math.Max(_level[m], weights[m] * amount);
    }

    /// <summary>Suma el nivel actual a los motores y lo hace decaer.</summary>
    public void Render(Span<float> motors, float dt)
    {
        var k = MathF.Exp(-dt / DecaySeconds);
        for (var m = 0; m < _level.Length; m++)
        {
            motors[m] += _level[m];
            _level[m] *= k;
        }
    }

    public void Reset() => Array.Clear(_level);
}
