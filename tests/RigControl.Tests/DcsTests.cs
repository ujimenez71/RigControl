using System.Net;
using System.Net.Sockets;
using System.Text;
using RigControl.Core;
using RigControl.Core.Bindings;

namespace RigControl.Tests;

/// <summary>DCS-BIOS: flujo, órdenes y cambio de perfil.</summary>
internal static class DcsTests
{
    public static int Run()
    {
        var log = new List<string>();
        Log.Sink = e => log.Add(e.Text);
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }
        string Txt(byte[]? b) => b is null ? "null" : Encoding.ASCII.GetString(b).Replace("\n", "\n");

        // ---- Acciones D:
        var a1 = ActionSpec.Parse("D:ufc_1", out var e1);
        Check(a1 is { Kind: ActionKind.DcsBios } && Txt(a1.DcsMessage) == "UFC_1 1\n" && Txt(a1.DcsRelease) == "UFC_1 0\n", $"D:ufc_1 = pulsador ({Txt(a1?.DcsMessage)} / {Txt(a1?.DcsRelease)}) {e1}");
        var a2 = ActionSpec.Parse("D:HDG_KNOB INC", out _);
        Check(a2 is not null && Txt(a2.DcsMessage) == "HDG_KNOB INC\n" && a2.DcsRelease is null, "D:HDG_KNOB INC = valor, sin soltar");
        foreach (var bad in new[] { "D:", "D:UFC 1 2", "D:UFC-1" })
            Check(ActionSpec.Parse(bad, out var e) is null && e is not null, $"rechaza \"{bad}\": {e}");

        // ---- DCS falso: emisor multicast + receptor de órdenes (puertos de prueba)
        const int ExportPort = 15010, CommandPort = 17778;
        using var fakeDcsOut = new UdpClient(); fakeDcsOut.MulticastLoopback = true;
        var group = new IPEndPoint(DcsBiosOutput.MulticastGroup, ExportPort);
        using var fakeDcsIn = new UdpClient(new IPEndPoint(IPAddress.Loopback, CommandPort));
        fakeDcsIn.Client.ReceiveTimeout = 500;

        byte[] Frame(ushort address, byte[] data)
        {
            var f = new List<byte> { 0x55, 0x55, 0x55, 0x55, (byte)address, (byte)(address >> 8), (byte)data.Length, (byte)(data.Length >> 8) };
            f.AddRange(data);
            return [.. f];
        }
        byte[] Name(string n) { var b = new byte[24]; Encoding.ASCII.GetBytes(n).CopyTo(b, 0); return b; }

        using var dcs = new DcsBiosOutput(ExportPort, CommandPort);
        var changes = new List<string>();
        dcs.AircraftChanged += n => changes.Add(n);
        long t = 0;
        void Pump(int ms) { for (var i = 0; i < ms / 10; i++) { t += 10; dcs.Tick(t); Thread.Sleep(10); } }

        Pump(50); // abre los sockets
        // Como DCS de verdad (visto con el AH-64D): el nombre llega a trozos en paquetes distintos.
        fakeDcsOut.Send(Frame(0, Name("F-")), group);
        Pump(60);
        Check(dcs.Aircraft is null && changes.Count == 0, "nombre a medias (\"F-\"): todavía no se anuncia");
        fakeDcsOut.Send(Frame(0, Name("F-15ESE")), group);
        Pump(500);
        Check(dcs.IsActive, "recibe el flujo de DCS-BIOS por multicast");
        Check(dcs.Aircraft == "F-15ESE" && changes.SequenceEqual(["dcs:f-15ese"]), $"avión detectado: {dcs.Aircraft} -> {string.Join(",", changes)}");
        fakeDcsOut.Send(Frame(0, Name("F-15ESE")), group); Pump(500);
        Check(changes.Count == 1, "el mismo avión no vuelve a avisar");
        fakeDcsOut.Send(Frame(0x1000, [0x34, 0x12]), group); Pump(100);
        Check(dcs.Image[0x1000] == 0x34 && dcs.Image[0x1001] == 0x12, "actualiza la imagen de la cabina en otras direcciones");
        fakeDcsOut.Send([0x55, 0x55, 0x55, 0x55, 0x00, 0x10, 0xFF, 0x7F, 1, 2], group); Pump(100);
        Check(dcs.IsActive, "paquete malformado (longitud imposible): se ignora sin romper nada");

        string Recv() { try { var ep = new IPEndPoint(IPAddress.Any, 0); return Encoding.ASCII.GetString(fakeDcsIn.Receive(ref ep)).Replace("\n", "\n"); } catch { return "(nada)"; } }

        // Enviar sin haber llamado nunca a Tick (fallo real del 02/11/2026: la orden se perdía sin avisar)
        using (var fresh = new DcsBiosOutput(ExportPort + 2, CommandPort))
            Check(fresh.Send(a2!, tap: false) && Recv() == "HDG_KNOB INC\n", "envía aunque todavía no se haya llamado a Tick");

        // Toque de pulsador: 1 ahora y 0 a los 50 ms
        dcs.Send(a1!, tap: true);
        Check(Recv() == "UFC_1 1\n", "toque: envía UFC_1 1");
        Pump(80);
        Check(Recv() == "UFC_1 0\n", "toque: suelta con UFC_1 0 a los ~50 ms");

        // Dispatcher con un hold de pulsador: 1 al pulsar, 0 al soltar
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """{ "name": "t", "devices": { "c": "3344:832E" }, "bindings": [ { "input": "c:b1", "gesture": "hold", "action": "D:PLT_FCR_SCAN" } ] }""");
        var (prof, errs) = ProfileCompiler.Compile(ProfileFile.Load(path));
        Check(prof is not null, "perfil con D: compila " + string.Join(" | ", errs));
        var disp = new ActionDispatcher(null, null, dryRun: false, dcs);
        var engine = new BindingEngine(prof!, disp);
        engine.SetInput(0, true, t); Check(Recv() == "PLT_FCR_SCAN 1\n", "hold: INICIO envía 1");
        engine.SetInput(0, false, t + 300); Check(Recv() == "PLT_FCR_SCAN 0\n", "hold: FIN envía 0");

        // Cambio automático de perfil con avión de DCS
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "f15e.json"), """{ "name": "F-15E", "aircraft": ["dcs:f-15e"], "devices": {}, "bindings": [] }""");
        File.WriteAllText(Path.Combine(dir, "apache.json"), """{ "name": "AH-64D", "aircraft": ["dcs:ah-64d"], "devices": {}, "bindings": [] }""");
        using var sim = new SimConnectOutput();
        using var dcs2 = new DcsBiosOutput(ExportPort, CommandPort + 1);
        using var auto = new AutoProfileSource(dir, sim, dcs2);
        t = 0; for (var i = 0; i < 5; i++) { t += 10; dcs2.Tick(t); Thread.Sleep(10); }
        fakeDcsOut.Send(Frame(0, Name("AH-64D_BLK_II")), group);
        CompiledProfile? got = null;
        for (var i = 0; i < 80 && got is null; i++) { t += 10; dcs2.Tick(t); got = auto.Poll(t); Thread.Sleep(10); }
        Check(got?.Name == "AH-64D", $"run auto: con el AH-64D de DCS activa \"{got?.Name}\"");
        fakeDcsOut.Send(Frame(0, Name("F-15ESE")), group);
        got = null;
        for (var i = 0; i < 80 && got is null; i++) { t += 10; dcs2.Tick(t); got = auto.Poll(t); Thread.Sleep(10); }
        Check(got?.Name == "F-15E", $"run auto: al cambiar al F-15E activa \"{got?.Name}\"");

        // Sin datos 2 s -> inactivo y sin avión
        for (var i = 0; i < 5; i++) { t += 500; dcs.Tick(t); }
        Check(!dcs.IsActive && dcs.Aircraft is null, "sin datos 2 s: inactivo y sin avión");

        return fails;
    }
}
