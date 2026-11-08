using System.Numerics;

namespace HapticSeat.Telemetry;

public enum FlightEventType : byte
{
    /// <summary>Un disparo. Magnitud = cantidad de movimiento del proyectil (kg·m/s).</summary>
    GunFired,

    /// <summary>Impacto recibido. Magnitud = módulo de la fuerza (unidades de IL-2, sin calibrar).</summary>
    Hit,

    /// <summary>Daño sufrido. Magnitud = valor crudo de IL-2.</summary>
    Damage,

    /// <summary>Suelta de bomba. Magnitud = masa en kg.</summary>
    BombRelease,

    /// <summary>Lanzamiento de cohete. Magnitud = masa en kg.</summary>
    RocketLaunch,
}

/// <summary>Evento puntual. Posición en ejes del avión (X adelante+, Y arriba+, Z derecha+).</summary>
public readonly record struct FlightEvent(FlightEventType Type, float Magnitude, Vector3 Position);
