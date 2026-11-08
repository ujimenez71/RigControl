using System.Numerics;
using System.Text;
using HapticSeat.Telemetry.Il2;

namespace HapticSeat.Tests;

/// <summary>Construye paquetes de IL-2 con el mismo formato que emite el juego.</summary>
internal sealed class Il2PacketBuilder
{
    private readonly List<(ushort Id, float[] Values)> _indicators = [];
    private readonly List<(ushort Type, byte[] Data)> _events = [];

    public Il2PacketBuilder Indicator(ushort id, params float[] values)
    {
        _indicators.Add((id, values));
        return this;
    }

    public Il2PacketBuilder Event(ushort type, byte[] data)
    {
        _events.Add((type, data));
        return this;
    }

    public Il2PacketBuilder AircraftName(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name);
        return Event(0, [(byte)bytes.Length, .. bytes]);
    }

    public Il2PacketBuilder EngineData(ushort index, Vector3 pos, float maxRpm) =>
        Event(1, Bytes(w => { w.Write(index); w.Write(index); Vec(w, pos); w.Write(maxRpm); }));

    public Il2PacketBuilder GunData(ushort index, Vector3 pos, float mass, float velocity) =>
        Event(2, Bytes(w => { w.Write(index); Vec(w, pos); w.Write(mass); w.Write(velocity); }));

    public Il2PacketBuilder WheelData(ushort index, Vector3 pos) =>
        Event(3, Bytes(w => { w.Write(index); w.Write(index); Vec(w, pos); }));

    public Il2PacketBuilder BombRelease(Vector3 pos, float mass) =>
        Event(4, Bytes(w => { Vec(w, pos); w.Write(mass); w.Write((ushort)0); }));

    public Il2PacketBuilder Hit(Vector3 pos, Vector3 force) =>
        Event(7, Bytes(w => { Vec(w, pos); Vec(w, force); }));

    public Il2PacketBuilder GunFired(byte index) => Event(9, [index]);

    public byte[] Build(uint tick = 1)
    {
        var body = Bytes(w =>
        {
            w.Write((byte)_indicators.Count);
            foreach (var (id, values) in _indicators)
            {
                w.Write(id);
                w.Write((byte)values.Length);
                foreach (var v in values) w.Write(v);
            }
            w.Write((byte)_events.Count);
            foreach (var (type, data) in _events)
            {
                w.Write(type);
                w.Write((byte)data.Length);
                w.Write(data);
            }
        });

        return Bytes(w =>
        {
            w.Write(Il2Decoder.StateId);
            w.Write((ushort)(10 + body.Length));
            w.Write(tick);
            w.Write(body);
        });
    }

    public static byte[] Motion(float pitch, float roll, float rollRate, float pitchRate, float yawRate) => Bytes(w =>
    {
        w.Write(Il2Decoder.MotionId);
        w.Write(1u);
        w.Write(0f); // yaw
        w.Write(pitch);
        w.Write(roll);
        w.Write(rollRate);
        w.Write(pitchRate);
        w.Write(yawRate);
        w.Write(0f);
        w.Write(0f);
        w.Write(0f);
    });

    private static void Vec(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static byte[] Bytes(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms)) write(w);
        return ms.ToArray();
    }
}
