using System.Buffers.Text;
using System.Numerics;
using System.Text;

namespace HapticSeat.Telemetry.Dcs;

/// <summary>
/// Interpreta los paquetes de dcs/HapticSeatExport.lua (formato descrito allí). DCS no avisa de los disparos:
/// se deducen de lo que baja el armamento entre paquetes, y los impactos de que aumenten las averías.
/// </summary>
public sealed class DcsDecoder
{
    /// <summary>Las RPM llegan en %: los efectos solo miran la proporción.</summary>
    private const float FullRpm = 3000;

    private const float StandardG = 9.81f;
    private const float StallBuffetAmplitude = 0.5f;
    private const float StallBuffetHz = 11;

    /// <summary>Se usa un calibre medio (entre 20 y 30 mm): el juego no dice cuál es.</summary>
    private const float GunMomentum = 120;
    private const float BombMass = 250;
    private const float RocketMass = 30;
    private const float HitForce = 1000;

    /// <summary>Tope de golpes por paquete, para que una caída de munición (rearme) no dé una ráfaga larga.</summary>
    private const int MaxEventsPerPacket = 4;

    private const int FieldCount = 19;
    private const int Time = 1, Name = 2, Ias = 3, Agl = 4, G = 5, Flaps = 6, Airbrake = 7, Gear = 8, RodNose = 9,
        Shells = 14, Bombs = 15, Rockets = 16, Failures = 17, Stall = 18, RpmLeft = 12, RpmRight = 13;

    private static readonly Vector3 GunPosition = new(1.5f, -1, 0);
    private static readonly Vector3 PylonPosition = new(0, -1, 0);

    private readonly Range[] _fields = new Range[FieldCount];
    private byte[] _nameBytes = [];
    private double _prevShells = -1, _prevBombs = -1, _prevRockets = -1, _prevFailures = -1;

    /// <summary>Tiempo de la misión en el último paquete; deja de avanzar en pausa.</summary>
    public double ModelTime { get; private set; }

    /// <summary>Vuelca un paquete en el estado. Devuelve false si no es de los nuestros.</summary>
    public bool Decode(ReadOnlySpan<byte> packet, FlightState s, List<FlightEvent> events)
    {
        if (!SplitFields(packet) || !packet[_fields[0]].SequenceEqual("HS1"u8)) return false;

        var name = packet[_fields[Name]];
        if (!name.SequenceEqual(_nameBytes))
        {
            _nameBytes = name.ToArray();
            s.Aircraft = Encoding.UTF8.GetString(name);
            s.ResetAircraft();
            ForgetArmament();
        }

        ModelTime = Number(packet, Time);
        s.IasMs = (float)Number(packet, Ias);
        s.AglM = (float)Number(packet, Agl);
        s.Accel = new Vector3(0, (float)Number(packet, G) * StandardG, 0);
        s.Flaps = (float)Number(packet, Flaps);
        s.Airbrake = (float)Number(packet, Airbrake);

        var stalling = Number(packet, Stall) != 0;
        s.BuffetAmplitude = stalling ? StallBuffetAmplitude : 0;
        s.BuffetFrequency = stalling ? StallBuffetHz : 0;

        ReadEngines(packet, s);
        ReadWheels(packet, s);

        Armament(Number(packet, Shells), ref _prevShells, FlightEventType.GunFired, GunMomentum, GunPosition, events);
        Armament(Number(packet, Bombs), ref _prevBombs, FlightEventType.BombRelease, BombMass, PylonPosition, events);
        Armament(Number(packet, Rockets), ref _prevRockets, FlightEventType.RocketLaunch, RocketMass, PylonPosition, events);
        Damage(Number(packet, Failures), events);
        return true;
    }

    /// <summary>Localiza los campos; false si no son exactamente los esperados.</summary>
    private bool SplitFields(ReadOnlySpan<byte> packet)
    {
        int count = 0, start = 0;
        for (var i = 0; i < packet.Length; i++)
        {
            if (packet[i] != (byte)';') continue;
            if (count == FieldCount - 1) return false;
            _fields[count++] = start..i;
            start = i + 1;
        }
        _fields[count++] = start..packet.Length;
        return count == FieldCount;
    }

    private void ForgetArmament() => _prevShells = _prevBombs = _prevRockets = _prevFailures = -1;

    private double Number(ReadOnlySpan<byte> packet, int field) =>
        Utf8Parser.TryParse(packet[_fields[field]], out double value, out _) && double.IsFinite(value) ? value : 0;

    private void ReadEngines(ReadOnlySpan<byte> packet, FlightState s)
    {
        var left = (float)Number(packet, RpmLeft);
        var right = (float)Number(packet, RpmRight);
        s.EngineCount = right > 0 ? 2 : 1;
        for (var i = 0; i < s.EngineCount; i++)
        {
            s.EngineRpm[i] = (i == 0 ? left : right) / 100 * FullRpm;
            s.EngineMaxRpm[i] = FullRpm;
            s.EnginePos[i] = TypicalAirframe.EnginePosition(s.EngineCount, i);
        }
        if (s.EngineCount == 1) s.EngineRpm[1] = s.EngineMaxRpm[1] = 0;
    }

    private void ReadWheels(ReadOnlySpan<byte> packet, FlightState s)
    {
        s.WheelCount = TypicalAirframe.Wheels;
        var gear = (float)Number(packet, Gear);
        for (var i = 0; i < TypicalAirframe.Wheels; i++)
        {
            s.GearPosition[i] = gear;
            s.WheelLoad[i] = (float)Number(packet, RodNose + i);
            s.WheelPos[i] = TypicalAirframe.WheelPositions[i];
        }
    }

    /// <summary>Solo cuenta lo que baja: subir es rearmar. El primer dato de cada avión es la referencia.</summary>
    private static void Armament(double now, ref double previous, FlightEventType type, float magnitude, Vector3 position, List<FlightEvent> events)
    {
        if (previous >= 0 && now < previous)
        {
            var count = Math.Min((int)(previous - now), MaxEventsPerPacket);
            for (var i = 0; i < count; i++) events.Add(new FlightEvent(type, magnitude, position));
        }
        previous = now;
    }

    private void Damage(double now, List<FlightEvent> events)
    {
        if (_prevFailures >= 0 && now > _prevFailures)
            events.Add(new FlightEvent(FlightEventType.Hit, HitForce, Vector3.Zero));
        _prevFailures = now;
    }
}
