using HapticSeat.Hf8;
using HapticSeat.Telemetry;

namespace HapticSeat.Effects;

/// <summary>
/// Ejecuta todos los efectos y los mezcla por motor: el más fuerte manda y el resto suma a la mitad,
/// para que un golpe se distinga sobre el fondo del motor sin saturar. Puro (dt inyectado).
/// </summary>
public sealed class EffectEngine
{
    private readonly IEffect[] _effects;
    private readonly bool[] _enabled;
    private readonly float[][] _scratch;
    private bool _wasActive;

    /// <summary>Ganancia general 0..2 (1 = sin cambio).</summary>
    public float MasterGain { get; set; } = 1;

    /// <summary>Índice del único efecto que suena (botón Probar), aunque esté desactivado; -1 = todos.</summary>
    public int Solo { get; set; } = -1;

    public IReadOnlyList<IEffect> Effects => _effects;

    public EffectEngine(params IEffect[] effects)
    {
        _effects = effects;
        _enabled = [.. effects.Select(_ => true)];
        _scratch = [.. effects.Select(_ => new float[Hf8Motors.Count])];
    }

    public int IndexOf(string id) => Array.FindIndex(_effects, e => e.Id == id);

    public bool IsEnabled(int index) => _enabled[index];

    public void SetEnabled(int index, bool enabled)
    {
        // Al reactivarlo, su memoria (posición previa del tren, ruedas…) está desfasada: evita golpes falsos
        if (enabled && !_enabled[index]) _effects[index].Reset();
        _enabled[index] = enabled;
    }

    public void ResetAll()
    {
        foreach (var e in _effects) e.Reset();
    }

    public static EffectEngine CreateDefault() => new(
        new EngineEffect(),
        new RotorEffect(),
        new StallBuffetEffect(),
        new GroundEffect(),
        new GearFlapsEffect(),
        new AirbrakeEffect(),
        new GForceEffect(),
        new GunEffect(),
        new HitEffect(),
        new OrdnanceEffect());

    /// <summary>Calcula la intensidad 0..1 de cada motor.</summary>
    public void Update(FlightState s, ReadOnlySpan<FlightEvent> events, float dt, Span<float> motors)
    {
        motors.Clear();
        if (!s.Active)
        {
            if (_wasActive) ResetAll();
            _wasActive = false;
            return;
        }
        _wasActive = true;

        for (var i = 0; i < _effects.Length; i++)
        {
            Array.Clear(_scratch[i]);
            var plays = Solo < 0 ? _enabled[i] : Solo == i;
            if (plays) _effects[i].Update(s, events, dt, _scratch[i]);
        }

        for (var m = 0; m < motors.Length; m++)
        {
            float max = 0, sum = 0;
            foreach (var layer in _scratch)
            {
                var v = Math.Max(0, layer[m]);
                sum += v;
                max = Math.Max(max, v);
            }
            motors[m] = Math.Clamp((max + 0.5f * (sum - max)) * MasterGain, 0, 1);
        }
    }

    /// <summary>
    /// Pasa 0..1 a niveles del HF8 (0..255). Por debajo del mínimo de cada motor no se nota nada, así que
    /// cualquier valor no nulo empieza ahí.
    /// </summary>
    public static void ToLevels(ReadOnlySpan<float> motors, Span<byte> levels, ReadOnlySpan<byte> minLevels)
    {
        for (var m = 0; m < motors.Length; m++)
        {
            var v = motors[m];
            var min = minLevels[m];
            levels[m] = v < 0.005f ? (byte)0 : (byte)Math.Round(min + v * (255 - min));
        }
    }
}
