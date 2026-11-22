using System.Net;
using System.Net.Sockets;
using System.Text;
using RigControl.Core;
using RigControl.Core.Bindings;

namespace RigControl.Tests;

/// <summary>IL-2: telemetría con un IL-2 falso por UDP, variables I:, simulador del perfil y cambio automático.</summary>
internal static class Il2Tests
{
    private const int Port = 15678;

    public static int Run()
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        // ---- Variables I:
        Check(Il2Telemetry.TryParseVar("I:gear:2", out var g2, out _) && g2 == new Il2Var(Il2VarKind.Gear, 1), "I:gear:2 = tren, rueda 2");
        Check(Il2Telemetry.TryParseVar("I:IAS", out var ias, out _) && ias == new Il2Var(Il2VarKind.Ias, -1), "I:IAS sin índice");
        foreach (var bad in new[] { "I:PATATA", "I:IAS:2", "I:RPM:0", "I:RPM:9", "I:RPM:x" })
            Check(!Il2Telemetry.TryParseVar(bad, out _, out var e) && e is not null, $"rechaza {bad}: {e}");
        var idx = new List<SimVarSpec>();
        int Index(SimVarSpec v) { idx.Add(v); return idx.Count - 1; }
        Check(LightParser.TryParse("I:gear >= 0.99", Index, out var c1, out _) && c1.Op == LightOp.GreaterOrEqual && idx[^1].Name == "I:GEAR",
              $"luz \"I:gear >= 0.99\" -> {idx.LastOrDefault()?.Name}");
        Check(!LightParser.TryParse("I:GEAR, percent>1", Index, out _, out var eu) && eu!.Contains("unidades"), $"I: con unidades: {eu}");
        Check(!LightParser.TryParse("I:NADA=1", Index, out _, out var en) && en!.Contains("desconocida"), $"I: desconocida: {en}");

        // ---- Simulador del perfil
        SimKind Sim(string json) => ProfileCompiler.ResolveSim(ProfileFile.Parse(json), out _);
        string? SimError(string json) { ProfileCompiler.ResolveSim(ProfileFile.Parse(json), out var e); return e; }
        Check(Sim("""{ "name": "x", "aircraft": ["il2:bf 109"] }""") == SimKind.Il2, "aircraft il2:... = perfil de IL-2");
        Check(Sim("""{ "name": "x", "sim": "il2", "aircraft": ["*"] }""") == SimKind.Il2, "sim \"il2\" con comodín");
        Check(SimError("""{ "name": "x", "aircraft": ["il2:bf 109", "dcs:f-16"] }""") is not null, "mezclar IL-2 y DCS = error");
        Check(SimError("""{ "name": "x", "sim": "il2", "aircraft": ["bf 109"] }""") is { } se && se.Contains("il2:"), "sim il2 sin prefijo il2: = error");

        (CompiledProfile?, List<string>) Compile(string json) => ProfileCompiler.Compile(ProfileFile.Parse(json));
        var (okProfile, okErrors) = Compile("""
            { "name": "Bf 109", "aircraft": ["il2:bf 109"], "devices": { "b": "294B:1901" },
              "bindings": [ { "input": "b:b1", "gesture": "press", "when": { "state": "I:ON_GROUND" }, "action": "key:G" } ],
              "lights": { "GearLeftGreen": "I:GEAR:1>=0.99", "GearNoseGreen": ["I:GEAR:2>=0.99"], "GearRightRed": "I:GEAR=0.01..0.98" },
              "icp": [ "IAS {I:IAS|0} KMH", "RPM {I:RPM|0}" ] }
            """);
        Check(okProfile is not null, "perfil de IL-2 con luces, state e icp compila " + string.Join(" | ", okErrors));
        var (_, mixErrors) = Compile("""{ "name": "x", "aircraft": ["il2:bf 109"], "lights": { "GearLeftGreen": "A:GEAR LEFT POSITION" } }""");
        Check(mixErrors.Any(e => e.Contains("no es de IL-2")), "A: en un perfil de IL-2 = error: " + string.Join(" | ", mixErrors));
        var (_, mixErrors2) = Compile("""{ "name": "x", "aircraft": ["c172"], "lights": { "GearLeftGreen": "I:GEAR" } }""");
        Check(mixErrors2.Any(e => e.Contains("variable de IL-2")), "I: en un perfil de MSFS = error: " + string.Join(" | ", mixErrors2));
        var (_, icpErrors) = Compile("""{ "name": "x", "aircraft": ["il2:yak"], "icp": [ "{D:UHF_RADIO}" ] }""");
        Check(icpErrors.Any(e => e.Contains("IL-2")), "D: en el ICP de un perfil de IL-2 = error: " + string.Join(" | ", icpErrors));

        // ---- IL-2 falso: paquetes de estado por UDP
        using var sender = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, Port);
        using var il2 = new Il2Telemetry(Port);
        var changes = new List<string>();
        il2.AircraftChanged += a => changes.Add(a);
        long t = 0;
        void Pump(int ms) { for (var i = 0; i < ms / 10; i++) { t += 10; il2.Tick(t); Thread.Sleep(10); } }

        Pump(30); // abre el socket
        sender.Send(Packet(Indicators(), Events()), target); // datos sin nombre: misión empezada antes de abrir RigControl
        Pump(100);
        Check(il2.IsActive && il2.Aircraft is null && changes.SequenceEqual(["il2:"]), $"datos sin nombre: activo y anuncia \"il2:\" ({string.Join(",", changes)})");

        il2.Prepare(okProfile!);
        var values = new double[okProfile!.WatchedVars.Length];
        double V(string name) { il2.Fill(values); var i = Array.FindIndex(okProfile.WatchedVars, w => w.Name == name); return i < 0 ? double.NaN : values[i]; }

        sender.Send(Packet(Indicators(), Events(("Bf 109 G-6", 0))), target);
        Pump(100);
        Check(il2.Aircraft == "Bf 109 G-6" && changes[^1] == "il2:bf 109 g-6", $"avión por el evento 0: {il2.Aircraft} -> {changes[^1]}");
        sender.Send(Packet(Indicators(), Events()), target); // el siguiente paquete ya trae los indicadores
        Pump(100);
        Check(Math.Abs(V("I:IAS") - 360) < 0.01, $"IAS 100 m/s = 360 km/h ({V("I:IAS")})");
        Check(Math.Abs(V("I:RPM") - 2400) < 0.01, $"RPM del motor 1 ({V("I:RPM")})");
        Check(V("I:GEAR:1") == 1 && V("I:GEAR:2") == 1 && V("I:ON_GROUND") == 1, $"tren abajo y con peso ({V("I:GEAR:1")}, {V("I:ON_GROUND")})");
        Check(okProfile.Lights.First(l => l.Name == "GearLeftGreen").Evaluate(values) && !okProfile.Lights.First(l => l.Name == "GearRightRed").Evaluate(values),
              "luces: verde izquierda encendida, roja derecha apagada");

        sender.Send(Packet(Indicators(gear: [0.5f, 0.5f, 0.5f], load: [0, 0, 0]), Events()), target);
        Pump(100);
        il2.Fill(values);
        Check(okProfile.Lights.First(l => l.Name == "GearRightRed").Evaluate(values) && V("I:ON_GROUND") == 0, "tren a medio camino: roja encendida, sin peso");

        var ctx = new IcpContext(values, null, "Bf 109", "Bf 109 G-6", DateTime.Now);
        il2.Fill(values);
        var cells = new char[25]; var inv = new bool[25];
        IcpTemplate.Render(okProfile.Icp[0], ctx, cells, inv);
        var line = new string(cells);
        Check(line.StartsWith("IAS 360 KMH"), $"ICP: \"{line.TrimEnd()}\"");

        sender.Send(Packet([], Events()), target); // pausa: sin indicadores
        Pump(50);
        Check(il2.Paused && Math.Abs(V("I:IAS") - 360) < 0.01, "pausa: se mantienen los últimos valores");

        sender.Send(Packet(Indicators(), Events(("Bf 109 G-6", 0))), target);
        Pump(50);
        Check(changes.Count(c => c == "il2:bf 109 g-6") == 1, "el mismo avión no vuelve a avisar");

        sender.Send([1, 0, 76, 73, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9], target); // paquete de movimiento
        sender.Send([0x01, 0x01, 0x00, 0x54, 0, 0, 0, 0, 0, 0, 5, 0, 0], target); // truncado
        Pump(50);
        Check(il2.IsActive && il2.Aircraft == "Bf 109 G-6", "paquetes de movimiento y truncados: se ignoran sin romper nada");

        // Corte: a los 2 s inactivo, a los 30 s se descarta todo
        for (var i = 0; i < 5; i++) { t += 500; il2.Tick(t); }
        Check(!il2.IsActive && il2.HasState && il2.Aircraft is not null, "sin datos 2,5 s: inactivo pero mantiene el avión");
        t += Il2Telemetry.StaleMs; il2.Tick(t);
        Check(!il2.HasState && il2.Aircraft is null && double.IsNaN(V("I:IAS")), "sin datos 30 s: descarta avión y valores");

        // ---- Cambio automático de perfil
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "a-bf109.json"), """{ "name": "Bf 109", "aircraft": ["il2:bf 109"], "devices": {}, "bindings": [] }""");
        File.WriteAllText(Path.Combine(dir, "b-il2.json"), """{ "name": "IL-2 genérico", "sim": "il2", "aircraft": ["*"], "devices": {}, "bindings": [] }""");
        File.WriteAllText(Path.Combine(dir, "c-msfs.json"), """{ "name": "MSFS genérico", "sim": "msfs", "aircraft": ["*"], "devices": {}, "bindings": [] }""");
        using var sim = new SimConnectOutput();
        using var il2b = new Il2Telemetry(Port + 1);
        using var auto = new AutoProfileSource(dir, sim, null, il2b);
        Check(auto.Initial.Name == "MSFS genérico", $"sin avión: el comodín de MSFS, nunca el de IL-2 ({auto.Initial.Name})");
        t = 0;
        CompiledProfile? Wait(byte[] packet)
        {
            sender.Send(packet, new IPEndPoint(IPAddress.Loopback, Port + 1));
            CompiledProfile? got = null;
            for (var i = 0; i < 50 && got is null; i++) { t += 10; il2b.Tick(t); got = auto.Poll(t); Thread.Sleep(10); }
            return got;
        }
        for (var i = 0; i < 3; i++) { t += 10; il2b.Tick(t); }
        Check(Wait(Packet(Indicators(), Events()))?.Name == "IL-2 genérico", "datos de IL-2 sin nombre: comodín de IL-2");
        Check(Wait(Packet(Indicators(), Events(("Bf 109 F-4", 0))))?.Name == "Bf 109", "Bf 109 F-4: su perfil (\"il2:bf 109\")");
        Check(Wait(Packet(Indicators(), Events(("Yak-1 ser.69", 0))))?.Name == "IL-2 genérico", "Yak-1: vuelve al comodín de IL-2");
        Directory.Delete(dir, true);

        return fails;
    }

    // ------------------------------------------------------------ paquetes de IL-2

    private static byte[] Indicators(float[]? gear = null, float[]? load = null) =>
    [
        .. Indicator(0, [2400f]),                 // RPM, un motor
        .. Indicator(1, [1.3f]),                  // admisión
        .. Indicator(4, gear ?? [1f, 1f, 1f]),    // tren por rueda
        .. Indicator(5, load ?? [900f, 900f, 300f]),
        .. Indicator(6, [100f]),                  // IAS m/s
        .. Indicator(10, [250f]),                 // AGL
        .. Indicator(11, [0f]),
        .. Indicator(12, [0f]),
    ];

    private static byte[] Indicator(ushort id, float[] values)
    {
        var b = new List<byte> { (byte)id, (byte)(id >> 8), (byte)values.Length };
        foreach (var v in values) b.AddRange(BitConverter.GetBytes(v));
        return [.. b];
    }

    /// <summary>Bloque de eventos: aquí solo el del nombre del avión (tipo 0).</summary>
    private static byte[] Events(params (string Name, ushort Type)[] events)
    {
        var b = new List<byte> { (byte)events.Length };
        foreach (var (name, type) in events)
        {
            var text = Encoding.ASCII.GetBytes(name);
            var body = new List<byte> { (byte)text.Length };
            body.AddRange(text);
            body.Add(0);
            b.AddRange([(byte)type, (byte)(type >> 8), (byte)body.Count]);
            b.AddRange(body);
        }
        return [.. b];
    }

    private static int CountIndicators(byte[] block)
    {
        var n = 0;
        for (var o = 0; o < block.Length; n++) o += 3 + block[o + 2] * 4;
        return n;
    }

    private static byte[] Packet(byte[] indicators, byte[] events)
    {
        var b = new List<byte>();
        b.AddRange(BitConverter.GetBytes(Il2Telemetry.StateId));
        b.AddRange([0, 0]);              // tamaño (no se usa)
        b.AddRange([0, 0, 0, 0]);        // tick
        b.Add((byte)CountIndicators(indicators));
        b.AddRange(indicators);
        b.AddRange(events);
        return [.. b];
    }
}
