using System.Numerics;
using HapticSeat.Hf8;

namespace HapticSeat.Effects;

/// <summary>
/// Pesos por motor (0..1) para repartir un efecto por el cuerpo. Orden de motores: ver <see cref="Hf8Motor"/>
/// (pares der/izq: muslos, glúteos, espalda, hombros).
/// </summary>
public static class Zones
{
    public static readonly float[] All = [1, 1, 1, 1, 1, 1, 1, 1];
    public static readonly float[] Seat = [0.8f, 0.8f, 1, 1, 0, 0, 0, 0];
    public static readonly float[] Thighs = [1, 1, 0.3f, 0.3f, 0, 0, 0, 0];
    public static readonly float[] Buttocks = [0.3f, 0.3f, 1, 1, 0.2f, 0.2f, 0, 0];
    public static readonly float[] SeatAndBack = [0.6f, 0.6f, 1, 1, 0.8f, 0.8f, 0.4f, 0.4f];

    /// <summary>
    /// Pesos según la posición en el avión. El piloto mira hacia delante sentado: lo de proa se siente en
    /// los muslos, lo del centro en los glúteos y lo de popa en espalda y hombros; la izquierda y la derecha,
    /// en su lado. <paramref name="focus"/> 0 = igual en todo el cojín, 1 = solo en la zona de origen.
    /// </summary>
    public static void Spatial(Vector3 pos, float focus, Span<float> weights)
    {
        const float SpanM = 2.5f; // a esta distancia del piloto el efecto ya está del todo en un lado
        var lat = Math.Clamp(pos.Z / SpanM, -1, 1);
        var lon = Math.Clamp(pos.X / SpanM, -1, 1);
        var right = 0.5f + 0.5f * lat;
        var left = 1 - right;
        var front = Math.Max(0, lon);
        var rear = Math.Max(0, -lon);
        var mid = 1 - Math.Abs(lon);

        Span<float> rows = [front + 0.4f * mid, mid, rear + 0.3f * mid, 0.7f * rear];
        var max = 0f;
        for (var m = 0; m < Hf8Motors.Count; m++)
        {
            var side = m % 2 == 0 ? right : left; // pares = derecha
            weights[m] = rows[m / 2] * side;
            max = Math.Max(max, weights[m]);
        }

        for (var m = 0; m < Hf8Motors.Count; m++)
        {
            var w = max > 0 ? weights[m] / max : 1;
            weights[m] = 1 - focus + focus * w;
        }
    }

    /// <summary>Suma <paramref name="amount"/> × peso a cada motor.</summary>
    public static void Add(Span<float> motors, ReadOnlySpan<float> weights, float amount)
    {
        for (var m = 0; m < motors.Length; m++) motors[m] += weights[m] * amount;
    }
}
