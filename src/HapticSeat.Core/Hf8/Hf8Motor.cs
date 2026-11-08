namespace HapticSeat.Hf8;

/// <summary>
/// Los 8 motores del HF8 Pro. El número es el índice del comando M&lt;n&gt;.
/// Mapa verificado sentado en el cojín (20/10/2026).
/// </summary>
public enum Hf8Motor
{
    MusloDer = 0,
    MusloIzq = 1,
    GluteoDer = 2,
    GluteoIzq = 3,
    EspaldaDer = 4,
    EspaldaIzq = 5,
    HombroDer = 6,
    HombroIzq = 7,
}

public static class Hf8Motors
{
    public const int Count = 8;

    public static readonly string[] Names =
    [
        "muslo der.", "muslo izq.", "glúteo der.", "glúteo izq.",
        "espalda der.", "espalda izq.", "hombro der.", "hombro izq.",
    ];

    /// <summary>
    /// Nivel desde el que se nota cada motor, medido con "umbral" sentado y con el dial al máximo
    /// (20/10/2026): glúteo 20, espalda 25, hombro 30. Los muslos no se midieron: como el glúteo.
    /// </summary>
    public static readonly byte[] DefaultMinLevels = [20, 20, 20, 20, 25, 25, 30, 30];
}
