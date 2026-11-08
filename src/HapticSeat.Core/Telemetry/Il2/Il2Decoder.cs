using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace HapticSeat.Telemetry.Il2;

/// <summary>
/// Decodifica los paquetes UDP de IL-2 Great Battles (bloques motiondevice y telemetrydevice de
/// data\startup.cfg). Formato sin documentación oficial; los IDs vienen de la ingeniería inversa de la
/// comunidad (IL2Shaker de sicsix). Todo little-endian:
///
///   Movimiento 0x494C0100: id u32, tick u32, yaw pitch roll (rad), giro X Y Z (rad/s), acel X Y Z (m/s²)
///   Estado     0x54000101: id u32, tamaño u16, tick u32, nIndicadores u8,
///                          [id u16, nFloats u8, float × n] × nIndicadores,
///                          nEventos u8, [tipo u16, nBytes u8, byte × n] × nEventos
///
/// Los bloques de longitud conocida se saltan aunque su ID sea desconocido, así que una versión nueva
/// del juego con indicadores extra no rompe la lectura.
/// </summary>
public sealed class Il2Decoder
{
    public const uint MotionId = 0x494C0100;
    public const uint StateId = 0x54000101;

    private const int MaxGuns = 64;
    private const int MotionLength = 44;

    private readonly Vector3[] _gunPos = new Vector3[MaxGuns];
    private readonly float[] _gunMomentum = new float[MaxGuns];
    private readonly bool[] _seenIndicator = new bool[256];
    private readonly bool[] _seenEvent = new bool[256];

    /// <summary>Avisos de IDs desconocidos (cada uno se avisa una sola vez).</summary>
    public Queue<string> Notices { get; } = new();

    /// <summary>Devuelve false si el paquete no es de IL-2 o está truncado (lo leído hasta ahí se conserva).</summary>
    public bool Decode(ReadOnlySpan<byte> p, FlightState s, List<FlightEvent> events)
    {
        if (p.Length < 4) return false;
        return BinaryPrimitives.ReadUInt32LittleEndian(p) switch
        {
            MotionId => DecodeMotion(p, s),
            StateId => DecodeState(p, s, events),
            _ => false,
        };
    }

    private static bool DecodeMotion(ReadOnlySpan<byte> p, FlightState s)
    {
        if (p.Length < MotionLength) return false;
        s.Pitch = F(p, 12);
        s.Roll = F(p, 16);
        s.RollRate = F(p, 20);
        s.PitchRate = F(p, 24);
        s.YawRate = F(p, 28);
        return true;
    }

    private bool DecodeState(ReadOnlySpan<byte> p, FlightState s, List<FlightEvent> events)
    {
        if (p.Length < 11) return false;
        int indicators = p[10];
        var o = 11;

        // Sin indicadores = pausa, menús o fuera del avión
        s.Paused = indicators == 0;

        for (var i = 0; i < indicators; i++)
        {
            if (o + 3 > p.Length) return false;
            int id = BinaryPrimitives.ReadUInt16LittleEndian(p[o..]);
            int n = p[o + 2];
            o += 3;
            if (o + n * 4 > p.Length) return false;
            ReadIndicator(id, p.Slice(o, n * 4), n, s);
            o += n * 4;
        }

        if (o >= p.Length) return true; // paquete sin bloque de eventos
        int count = p[o++];
        for (var i = 0; i < count; i++)
        {
            if (o + 3 > p.Length) return false;
            int type = BinaryPrimitives.ReadUInt16LittleEndian(p[o..]);
            int n = p[o + 2];
            o += 3;
            if (o + n > p.Length) return false;
            ReadEvent(type, p.Slice(o, n), s, events);
            o += n;
        }
        return true;
    }

    private void ReadIndicator(int id, ReadOnlySpan<byte> v, int n, FlightState s)
    {
        switch (id)
        {
            case 0: // RPM por motor
                s.EngineCount = Math.Min(n, FlightState.MaxEngines);
                for (var j = 0; j < s.EngineCount; j++) s.EngineRpm[j] = F(v, j * 4);
                break;
            case 4: // posición del tren por rueda, 0 recogido .. 1 desplegado
                s.WheelCount = Math.Min(n, FlightState.MaxWheels);
                for (var j = 0; j < s.WheelCount; j++) s.GearPosition[j] = F(v, j * 4);
                break;
            case 5: // carga sobre cada rueda
                s.WheelCount = Math.Min(n, FlightState.MaxWheels);
                for (var j = 0; j < s.WheelCount; j++) s.WheelLoad[j] = F(v, j * 4);
                break;
            case 6 when n >= 1:
                s.IasMs = F(v, 0);
                break;
            case 8 when n >= 3: // longitudinal, vertical, lateral
                s.Accel = new Vector3(F(v, 0), F(v, 4), F(v, 8));
                break;
            case 9 when n >= 2:
                s.BuffetFrequency = F(v, 0);
                s.BuffetAmplitude = F(v, 4);
                break;
            case 10 when n >= 1:
                s.AglM = F(v, 0);
                break;
            case 11 when n >= 1:
                s.Flaps = F(v, 0);
                break;
            case 12 when n >= 1:
                s.Airbrake = F(v, 0);
                break;
            case 1 or 2 or 3 or 7: // presión de admisión y valores sin identificar: conocidos, sin uso
                break;
            default:
                Notice(_seenIndicator, id, $"IL-2: indicador desconocido {id} ({n} floats)");
                break;
        }
    }

    private void ReadEvent(int type, ReadOnlySpan<byte> e, FlightState s, List<FlightEvent> events)
    {
        switch (type)
        {
            case 0 when e.Length >= 1: // nombre del avión: empieza misión o cambio de avión
                var len = Math.Min(e[0], e.Length - 1);
                s.Aircraft = Encoding.ASCII.GetString(e.Slice(1, len)).TrimEnd('\0');
                s.ResetAircraft();
                Array.Clear(_gunPos);
                Array.Clear(_gunMomentum);
                break;
            case 1 when e.Length >= 20: // datos de motor: índice u16 ×2, posición, RPM máximas
            {
                var idx = BinaryPrimitives.ReadUInt16LittleEndian(e);
                if (idx < FlightState.MaxEngines)
                {
                    s.EnginePos[idx] = V(e, 4);
                    s.EngineMaxRpm[idx] = F(e, 16);
                }
                break;
            }
            case 2 when e.Length >= 22: // datos de arma: índice u16, posición, masa (kg), velocidad (m/s)
            {
                var idx = BinaryPrimitives.ReadUInt16LittleEndian(e);
                if (idx < MaxGuns)
                {
                    _gunPos[idx] = V(e, 2);
                    _gunMomentum[idx] = F(e, 14) * F(e, 18);
                }
                break;
            }
            case 3 when e.Length >= 16: // datos de rueda: índice u16 ×2, posición
            {
                var idx = BinaryPrimitives.ReadUInt16LittleEndian(e);
                if (idx < FlightState.MaxWheels) s.WheelPos[idx] = V(e, 4);
                break;
            }
            case 4 when e.Length >= 16:
                events.Add(new FlightEvent(FlightEventType.BombRelease, F(e, 12), V(e, 0)));
                break;
            case 5 when e.Length >= 16:
                events.Add(new FlightEvent(FlightEventType.RocketLaunch, F(e, 12), V(e, 0)));
                break;
            case 7 when e.Length >= 24: // impacto: posición, fuerza
                events.Add(new FlightEvent(FlightEventType.Hit, V(e, 12).Length(), V(e, 0)));
                break;
            case 8 when e.Length >= 16:
                events.Add(new FlightEvent(FlightEventType.Damage, F(e, 12), V(e, 0)));
                break;
            case 9 when e.Length >= 1: // disparo: índice de arma
            {
                int idx = e[0];
                if (idx < MaxGuns)
                    events.Add(new FlightEvent(FlightEventType.GunFired, _gunMomentum[idx], _gunPos[idx]));
                break;
            }
            case 6 or 13 or 14: // conocidos sin uso: vectores sin identificar, inicio de misión, asiento
                break;
            default:
                Notice(_seenEvent, type, $"IL-2: evento desconocido {type} ({e.Length} bytes)");
                break;
        }
    }

    private void Notice(bool[] seen, int id, string text)
    {
        if (id >= seen.Length || seen[id]) return;
        seen[id] = true;
        Notices.Enqueue(text);
    }

    private static float F(ReadOnlySpan<byte> p, int offset) => BinaryPrimitives.ReadSingleLittleEndian(p[offset..]);

    private static Vector3 V(ReadOnlySpan<byte> p, int offset) => new(F(p, offset), F(p, offset + 4), F(p, offset + 8));
}
