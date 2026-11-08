using System.Numerics;

namespace HapticSeat.Telemetry;

/// <summary>
/// Estado de vuelo común a todos los simuladores. Lo rellena cada lector de telemetría y lo consumen
/// los efectos. Posiciones en ejes del avión, en metros: X adelante+, Y arriba+, Z derecha+.
/// </summary>
public sealed class FlightState
{
    public const int MaxEngines = 4;
    public const int MaxWheels = 4;

    /// <summary>Simulador del que viene este estado ("MSFS", "IL-2"), para mostrarlo.</summary>
    public string Source = "";

    /// <summary>Llegan paquetes (el simulador está emitiendo).</summary>
    public bool Connected;

    /// <summary>Pausa, menús o fuera del avión.</summary>
    public bool Paused;

    public bool Active => Connected && !Paused;

    public string Aircraft = "";

    public float IasMs;
    public float AglM;

    /// <summary>Aceleraciones del avión en m/s²: longitudinal, vertical y lateral.</summary>
    public Vector3 Accel;

    public float VerticalG => Accel.Y / 9.81f;

    public float RollRate, PitchRate, YawRate;
    public float Pitch, Roll;

    public float Flaps;
    public float Airbrake;

    public float BuffetFrequency;
    public float BuffetAmplitude;

    public int EngineCount;
    public readonly float[] EngineRpm = new float[MaxEngines];
    public readonly float[] EngineMaxRpm = new float[MaxEngines];
    public readonly Vector3[] EnginePos = new Vector3[MaxEngines];

    public int WheelCount;

    /// <summary>0 recogido, 1 desplegado.</summary>
    public readonly float[] GearPosition = new float[MaxWheels];

    /// <summary>Carga sobre la rueda, ~0..1. Mayor que 0 = tocando el suelo.</summary>
    public readonly float[] WheelLoad = new float[MaxWheels];

    public readonly Vector3[] WheelPos = new Vector3[MaxWheels];

    public void CopyTo(FlightState o)
    {
        o.Source = Source;
        o.Connected = Connected;
        o.Paused = Paused;
        o.Aircraft = Aircraft;
        o.IasMs = IasMs;
        o.AglM = AglM;
        o.Accel = Accel;
        o.RollRate = RollRate;
        o.PitchRate = PitchRate;
        o.YawRate = YawRate;
        o.Pitch = Pitch;
        o.Roll = Roll;
        o.Flaps = Flaps;
        o.Airbrake = Airbrake;
        o.BuffetFrequency = BuffetFrequency;
        o.BuffetAmplitude = BuffetAmplitude;
        o.EngineCount = EngineCount;
        EngineRpm.CopyTo(o.EngineRpm, 0);
        EngineMaxRpm.CopyTo(o.EngineMaxRpm, 0);
        EnginePos.CopyTo(o.EnginePos, 0);
        o.WheelCount = WheelCount;
        GearPosition.CopyTo(o.GearPosition, 0);
        WheelLoad.CopyTo(o.WheelLoad, 0);
        WheelPos.CopyTo(o.WheelPos, 0);
    }

    /// <summary>Vuelve a cero al cambiar de avión o de misión.</summary>
    public void ResetAircraft()
    {
        EngineCount = 0;
        WheelCount = 0;
        Array.Clear(EngineRpm);
        Array.Clear(EngineMaxRpm);
        Array.Clear(EnginePos);
        Array.Clear(GearPosition);
        Array.Clear(WheelLoad);
        Array.Clear(WheelPos);
    }
}
