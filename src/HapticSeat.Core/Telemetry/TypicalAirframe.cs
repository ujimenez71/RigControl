using System.Numerics;

namespace HapticSeat.Telemetry;

/// <summary>
/// Posiciones de un avión típico, en metros y ejes del avión (X adelante+, Y arriba+, Z derecha+), para los
/// simuladores que no las dan.
/// </summary>
public static class TypicalAirframe
{
    public const int Wheels = 3;

    /// <summary>Morro o rueda central, izquierda, derecha.</summary>
    public static readonly Vector3[] WheelPositions =
    [
        new(1.5f, -1.5f, 0),
        new(-0.5f, -1.5f, -1.2f),
        new(-0.5f, -1.5f, 1.2f),
    ];

    private static readonly Vector3[][] EnginePositions =
    [
        [new(2.5f, 0, 0)],
        [new(0.5f, 0, -2.5f), new(0.5f, 0, 2.5f)],
        [new(0.5f, 0, -2.5f), new(2.5f, 0, 0), new(0.5f, 0, 2.5f)],
        [new(0.5f, 0, -5), new(0.5f, 0, -2.5f), new(0.5f, 0, 2.5f), new(0.5f, 0, 5)],
    ];

    /// <summary>Posición del motor <paramref name="index"/> en un avión de <paramref name="count"/> motores (1 a 4).</summary>
    public static Vector3 EnginePosition(int count, int index) => EnginePositions[count - 1][index];
}
