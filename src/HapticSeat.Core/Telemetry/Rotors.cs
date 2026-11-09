namespace HapticSeat.Telemetry;

/// <summary>
/// Paso de palas a régimen nominal (palas × rpm del rotor / 60) de los helicópteros conocidos, por el
/// nombre que da el simulador. Datos públicos de cada modelo, redondeados.
/// </summary>
public static class Rotors
{
    private static readonly (string Prefix, float Hz)[] Known =
    [
        ("AH-64", 4 * 289 / 60f),  // 19,3 Hz
        ("UH-1", 2 * 324 / 60f),   // 10,8 Hz: el "golpeteo" de dos palas
        ("Mi-8", 5 * 192 / 60f),   // 16 Hz
        ("Mi-24", 5 * 240 / 60f),  // 20 Hz
        ("Ka-50", 3 * 300 / 60f),  // 15 Hz por rotor (coaxial)
        ("SA342", 3 * 387 / 60f),  // 19,4 Hz
        ("OH58", 4 * 395 / 60f),   // 26 Hz
        ("CH-47", 3 * 225 / 60f),  // 11,3 Hz
    ];

    /// <summary>0 si el helicóptero no está en la tabla (el efecto usa un valor medio).</summary>
    public static float BladePassHz(string aircraft)
    {
        foreach (var (prefix, hz) in Known)
            if (aircraft.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return hz;
        return 0;
    }
}
