using System.Numerics;

namespace HapticSeat.Telemetry.Msfs;

/// <summary>
/// Variables que se piden a MSFS y su conversión al estado común. Puro, para poder probarlo sin simulador.
/// Solo lo que usan los efectos. MSFS no da la posición de motores ni ruedas: se suponen las de un avión típico.
/// </summary>
public static class MsfsVars
{
    private const float MsPerKnot = 0.514444f;
    private const float StandardG = 9.81f;

    /// <summary>Un turbina se expresa como RPM equivalentes a partir de N1 (%): los efectos solo miran la proporción.</summary>
    private const float TurbineFullRpm = 3000;

    private const float StallRampDegrees = 3;
    private const int Wheels = TypicalAirframe.Wheels;

    private const int Ias = 0, Agl = 1, GForce = 2, OnGround = 3, StallWarning = 4, Alpha = 5, StallAlpha = 6,
        Flaps = 7, Spoilers = 8, EngineCount = 9, MaxRpm = 10, Rpm1 = 11, N1First = 15,
        GearCenter = 19, GearLeft = 20, GearRight = 21, Contact0 = 22;

    /// <summary>Nombre y unidades, en el orden en que llegan los datos.</summary>
    internal static readonly (string Name, string Unit)[] Datums =
    [
        ("AIRSPEED INDICATED", "knots"),
        ("PLANE ALT ABOVE GROUND", "meters"),
        ("G FORCE", "GForce"),
        ("SIM ON GROUND", "Bool"),
        ("STALL WARNING", "Bool"),
        ("INCIDENCE ALPHA", "degrees"),
        ("STALL ALPHA", "degrees"),
        ("TRAILING EDGE FLAPS LEFT PERCENT", "percent over 100"),
        ("SPOILERS LEFT POSITION", "percent over 100"),
        ("NUMBER OF ENGINES", "number"),
        ("MAX RATED ENGINE RPM", "rpm"),
        ("GENERAL ENG RPM:1", "rpm"),
        ("GENERAL ENG RPM:2", "rpm"),
        ("GENERAL ENG RPM:3", "rpm"),
        ("GENERAL ENG RPM:4", "rpm"),
        ("TURB ENG N1:1", "percent"),
        ("TURB ENG N1:2", "percent"),
        ("TURB ENG N1:3", "percent"),
        ("TURB ENG N1:4", "percent"),
        ("GEAR CENTER POSITION", "percent over 100"),
        ("GEAR LEFT POSITION", "percent over 100"),
        ("GEAR RIGHT POSITION", "percent over 100"),
        ("CONTACT POINT COMPRESSION:0", "percent over 100"),
        ("CONTACT POINT COMPRESSION:1", "percent over 100"),
        ("CONTACT POINT COMPRESSION:2", "percent over 100"),
    ];

    public static int Count => Datums.Length;

    /// <summary>Vuelca una muestra (<see cref="Count"/> valores) en el estado. No asigna memoria.</summary>
    public static void Apply(ReadOnlySpan<double> d, FlightState s)
    {
        s.IasMs = (float)d[Ias] * MsPerKnot;
        s.AglM = (float)d[Agl];
        // G FORCE ya incluye la gravedad (1 en vuelo nivelado), que es lo que espera VerticalG
        s.Accel = new Vector3(0, (float)d[GForce] * StandardG, 0);
        s.Flaps = (float)d[Flaps];
        s.Airbrake = (float)d[Spoilers];

        var onGround = d[OnGround] != 0;
        ApplyBuffet(d, onGround, s);
        ApplyEngines(d, s);
        ApplyWheels(d, onGround, s);
    }

    /// <summary>MSFS no da buffet: se deduce de lo cerca que está el ángulo de ataque de la pérdida.</summary>
    private static void ApplyBuffet(ReadOnlySpan<double> d, bool onGround, FlightState s)
    {
        s.BuffetAmplitude = 0;
        s.BuffetFrequency = 0;
        if (onGround) return;

        var warning = d[StallWarning] != 0 ? 0.35f : 0;
        var stall = (float)d[StallAlpha];
        var nearStall = stall > 1 ? Math.Clamp(((float)d[Alpha] - (stall - StallRampDegrees)) / StallRampDegrees, 0, 1) * 0.7f : 0;
        s.BuffetAmplitude = Math.Max(warning, nearStall);
        if (s.BuffetAmplitude > 0) s.BuffetFrequency = 8 + 6 * s.BuffetAmplitude;
    }

    private static void ApplyEngines(ReadOnlySpan<double> d, FlightState s)
    {
        var count = Math.Clamp((int)d[EngineCount], 0, FlightState.MaxEngines);
        s.EngineCount = count;
        var maxRpm = (float)d[MaxRpm];
        for (var i = 0; i < FlightState.MaxEngines; i++)
        {
            if (i >= count)
            {
                s.EngineRpm[i] = s.EngineMaxRpm[i] = 0;
                continue;
            }

            var n1 = (float)d[N1First + i];
            s.EngineRpm[i] = n1 > 1 ? n1 / 100 * TurbineFullRpm : (float)d[Rpm1 + i];
            s.EngineMaxRpm[i] = n1 > 1 ? TurbineFullRpm : maxRpm;
            s.EnginePos[i] = TypicalAirframe.EnginePosition(count, i);
        }
    }

    private static void ApplyWheels(ReadOnlySpan<double> d, bool onGround, FlightState s)
    {
        s.WheelCount = Wheels;
        s.GearPosition[0] = (float)d[GearCenter];
        s.GearPosition[1] = (float)d[GearLeft];
        s.GearPosition[2] = (float)d[GearRight];

        var anyCompressed = false;
        for (var i = 0; i < Wheels; i++)
            anyCompressed |= d[Contact0 + i] > 0.005;

        for (var i = 0; i < Wheels; i++)
        {
            // Hay aviones sin dato de compresión: basta saber que están en el suelo
            s.WheelLoad[i] = anyCompressed ? (float)d[Contact0 + i] : onGround ? 0.5f : 0;
            s.WheelPos[i] = TypicalAirframe.WheelPositions[i];
        }
    }
}
