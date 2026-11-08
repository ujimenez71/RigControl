using HapticSeat.Telemetry;

namespace HapticSeat.Effects;

/// <summary>
/// Un efecto háptico. Es puro respecto al tiempo (recibe dt), así que se puede probar con escenarios simulados.
/// </summary>
public interface IEffect
{
    /// <summary>Clave estable en los perfiles JSON (no cambiarla nunca).</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>Qué se siente, para la interfaz.</summary>
    string Description { get; }

    /// <summary>Ganancia del efecto, 0..2 (1 = valor por defecto).</summary>
    float Gain { get; set; }

    /// <summary>Escribe la intensidad 0..1 de cada motor en <paramref name="motors"/> (llega a cero).</summary>
    void Update(FlightState s, ReadOnlySpan<FlightEvent> events, float dt, Span<float> motors);

    /// <summary>Olvida el estado interno (pausa, cambio de avión).</summary>
    void Reset();
}
