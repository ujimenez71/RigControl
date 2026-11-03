using System.Net;
using System.Net.Sockets;
using System.Text;
using RigControl.Core;
using RigControl.Core.Bindings;

namespace RigControl.Tests;

/// <summary>Condiciones según el estado del simulador ("state") y variables D: de DCS-BIOS.</summary>
internal static class StateTests
{
    private sealed class Sink(double[] values) : IActionSink
    {
        public readonly List<string> Fired = [];
        public double[] Values => values;
        public void OnAction(Binding binding, ActionPhase phase, long timeMs) => Fired.Add(binding.Action);
    }

    public static int Run()
    {
        Log.Sink = _ => { };
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        (CompiledProfile? P, List<string> E) Compile(string json)
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, json);
            return ProfileCompiler.Compile(ProfileFile.Load(path));
        }

        // ---- Intérprete: D:
        var vars = new List<SimVarSpec>();
        int Idx(SimVarSpec s) { var i = vars.IndexOf(s); if (i >= 0) return i; vars.Add(s); return vars.Count - 1; }
        Check(LightParser.TryParse("d:plt_extl_acol_l_sw=2", Idx, out var c1, out var e1) && vars[0].Name == "D:PLT_EXTL_ACOL_L_SW" && vars[0].IsDcs,
              $"D: en mayúsculas y marcado como DCS ({vars.FirstOrDefault()}) {e1}");
        Check(c1.Op == LightOp.Equal && c1.A == 2, "D:...=2 = igual a 2");
        foreach (var bad in new[] { "D:X, Number=1", "D:UFC-1", "D:" })
            Check(!LightParser.TryParse(bad, Idx, out _, out var e) && e is not null, $"rechaza \"{bad}\": {e}");
        Check(!c1.Evaluate([]), "sin valores (array vacío): falsa, sin excepción");

        // ---- Compilador
        const string Toggle = """
        { "name": "t", "sim": "dcs", "aircraft": ["dcs:ah-64d"], "devices": { "c": "3344:832E" },
          "layers": { "acolOff": { "state": "D:PLT_EXTL_ACOL_L_SW=1" } },
          "bindings": [
            { "input": "c:b1", "gesture": "press", "when": { "layer": "acolOff" }, "action": "D:PLT_EXTL_ACOL_L_SW 2" },
            { "input": "c:b1", "gesture": "press", "action": "D:PLT_EXTL_ACOL_L_SW 1" },
            { "input": "c:b2", "gesture": "press", "when": { "state": "!D:PLT_EXTL_ACOL_L_SW" }, "action": "D:X 1" }
          ],
          "lights": { "Autopilot": "D:PLT_EXTL_ACOL_L_SW=2" } }
        """;
        var (p, errs) = Compile(Toggle);
        Check(p is not null, "perfil con \"state\" en capa, condición y luz compila " + string.Join(" | ", errs));
        Check(p!.WatchedVars.Length == 1, $"luces y condiciones comparten la variable: {p.WatchedVars.Length}");
        Check(ConflictGuard.Check(p).Count == 0, "Conflict Guard: condicionado + sin condición no es duplicado");
        var saved = Path.GetTempFileName();
        { var src = Path.GetTempFileName(); File.WriteAllText(src, Toggle); ProfileFile.Load(src).Save(saved); }
        Check(File.ReadAllText(saved).Contains("\"state\": \"D:PLT_EXTL_ACOL_L_SW=1\"") && ProfileCompiler.Compile(ProfileFile.Load(saved)).Profile is not null,
              "guardar conserva \"state\" y el perfil sigue compilando");

        var (pm, em) = Compile("""{ "name": "t", "sim": "msfs", "devices": {}, "bindings": [], "lights": { "Autopilot": "D:X=1" } }""");
        Check(pm is null && em.Any(e => e.Contains("DCS-BIOS")), $"D: en perfil de MSFS: error ({em.FirstOrDefault()})");
        var (pd, ed) = Compile("""{ "name": "t", "sim": "dcs", "devices": { "c": "3344:832E" }, "bindings": [ { "input": "c:b1", "gesture": "press", "when": { "state": "A:GEAR HANDLE POSITION" }, "action": "x" } ] }""");
        Check(pd is null && ed.Any(e => e.Contains("MSFS")), $"A: en perfil de DCS: error ({ed.FirstOrDefault()})");
        var (pb, eb) = Compile("""{ "name": "t", "devices": { "c": "3344:832E" }, "bindings": [ { "input": "c:b1", "gesture": "press", "when": { "state": "A:X", "input": "c:b2" }, "action": "x" } ] }""");
        Check(pb is null && eb.Any(e => e.Contains("state")), $"state e input a la vez: error ({eb.FirstOrDefault()})");
        var (pg, eg) = Compile("""{ "name": "t", "devices": { "c": "3344:832E" }, "bindings": [ { "input": "c:b1", "gesture": "press", "when": { "state": "A:GEAR HANDLE POSITION=1" }, "action": "x" } ] }""");
        Check(pg is not null, "perfil general con A: en state compila " + string.Join(" | ", eg));

        // ---- Motor: interruptor de anticolisión con un solo botón (según el estado)
        var values = new[] { 1.0 };
        var sink = new Sink(values);
        var engine = new BindingEngine(p, sink);
        engine.SetInput(0, true, 100); engine.SetInput(0, false, 150);
        Check(sink.Fired.SequenceEqual(["D:PLT_EXTL_ACOL_L_SW 2"]), $"ACOL en OFF (1) -> pone WHT (2): {string.Join(",", sink.Fired)}");
        values[0] = 2; sink.Fired.Clear();
        engine.SetInput(0, true, 1000); engine.SetInput(0, false, 1050);
        Check(sink.Fired.SequenceEqual(["D:PLT_EXTL_ACOL_L_SW 1"]), $"ACOL en WHT (2) -> pone OFF (1): {string.Join(",", sink.Fired)}");
        values[0] = double.NaN; sink.Fired.Clear();
        engine.SetInput(0, true, 2000); engine.SetInput(0, false, 2050);
        engine.SetInput(1, true, 2100); engine.SetInput(1, false, 2150);
        Check(sink.Fired.SequenceEqual(["D:PLT_EXTL_ACOL_L_SW 1"]), $"sin datos: gana el binding sin condición y el negado con ! no se cumple: {string.Join(",", sink.Fired)}");
        values[0] = 0; sink.Fired.Clear();
        engine.SetInput(1, true, 3000);
        Check(sink.Fired.SequenceEqual(["D:X 1"]), "!D: con valor 0 -> se cumple");

        // ---- DCS-BIOS de punta a punta: valores D: desde la imagen de la cabina
        const int ExportPort = 15020, CommandPort = 17790;
        using var fakeOut = new UdpClient(); fakeOut.MulticastLoopback = true;
        var group = new IPEndPoint(DcsBiosOutput.MulticastGroup, ExportPort);
        using var fakeIn = new UdpClient(new IPEndPoint(IPAddress.Loopback, CommandPort));
        fakeIn.Client.ReceiveTimeout = 500;
        string Recv() { try { var ep = new IPEndPoint(IPAddress.Any, 0); return Encoding.ASCII.GetString(fakeIn.Receive(ref ep)); } catch { return "(nada)"; } }
        byte[] Frame(ushort address, byte[] data) =>
            [0x55, 0x55, 0x55, 0x55, (byte)address, (byte)(address >> 8), (byte)data.Length, (byte)(data.Length >> 8), .. data];
        byte[] Name(string n) { var b = new byte[24]; Encoding.ASCII.GetBytes(n).CopyTo(b, 0); return b; }

        // Igual que en el AH-64D de verdad: ANTICOL en 0x801C, máscara 0xC0, desplazamiento 6 (RED/OFF/WHT).
        var asked = new List<string>();
        IReadOnlyList<DcsControl> Catalog(string a)
        {
            asked.Add(a);
            return [new DcsControl("PLT_EXTL_ACOL_L_SW", "Ext Lights", "Anti-Collision", ["selector"], 0x801C, 0xC0, 6, 2, ["RED", "OFF", "WHT"]),
                    new DcsControl("X", "Otro", "Sin salida", ["action"], null, 0, 0, 0, [])];
        }

        var log = new List<string>();
        Log.Sink = e => log.Add(e.Text);
        using var dcs = new DcsBiosOutput(ExportPort, CommandPort, Catalog);
        var disp = new ActionDispatcher(null, null, dryRun: false, dcs);
        disp.ProfileLoaded(p);
        Check(asked.SequenceEqual(["dcs:ah-64d"]), $"sin avión en DCS, busca los controles del avión del perfil: {string.Join(",", asked)}");
        Check(double.IsNaN(disp.Values[0]), "sin datos de DCS-BIOS: NaN");

        long t = 0;
        void Pump(int ms) { for (var i = 0; i < ms / 10; i++) { t += 10; disp.Tick(t); Thread.Sleep(10); } }
        Pump(50);
        fakeOut.Send(Frame(0, Name("AH-64D_BLK_II")), group);
        Pump(500);
        Check(double.IsNaN(disp.Values[0]), "con datos de DCS pero sin haber llegado aún esa dirección: NaN (no 0)");
        fakeOut.Send(Frame(0x801C, [0x40, 0x00]), group); // 0x40 >> 6 = 1 = OFF
        Pump(100);
        Check(asked.Count == 2 && asked[1] == "AH-64D_BLK_II", $"al detectar el avión vuelve a buscar sus controles: {string.Join(",", asked)}");
        Check(disp.Values[0] == 1, $"ANTICOL leído de la imagen: {disp.Values[0]} (OFF)");

        var engine2 = new BindingEngine(p, disp);
        engine2.SetInput(0, true, t); engine2.SetInput(0, false, t + 50);
        Check(Recv() == "PLT_EXTL_ACOL_L_SW 2\n", "pulsar con ANTICOL en OFF -> manda WHT");
        fakeOut.Send(Frame(0x801C, [0x80, 0x00]), group); // DCS confirma: WHT
        Pump(100);
        Check(disp.Values[0] == 2, $"DCS confirma WHT: {disp.Values[0]}");
        engine2.SetInput(0, true, t); engine2.SetInput(0, false, t + 50);
        Check(Recv() == "PLT_EXTL_ACOL_L_SW 1\n", "pulsar con ANTICOL en WHT -> manda OFF");

        // Controles que no existen o no tienen valor: aviso
        var (p2, _) = Compile("""{ "name": "t", "sim": "dcs", "devices": {}, "bindings": [], "lights": { "Autopilot": "D:NOPE", "Hdg": "D:X" } }""");
        log.Clear();
        disp.ProfileLoaded(p2!);
        Check(log.Any(l => l.Contains("D:NOPE") && l.Contains("D:X (sin valor numérico)")), $"avisa de los controles que no hay: {log.FirstOrDefault()}");
        Pump(50);
        Check(p2!.WatchedVars.Select((_, i) => disp.Values[i]).All(double.IsNaN), "y quedan sin valor (NaN)");

        // Corte corto: se mantiene el último valor; largo (30 s): NaN
        disp.ProfileLoaded(p);
        fakeOut.Send(Frame(0x801C, [0x80, 0x00]), group); Pump(100);
        for (var i = 0; i < 5; i++) { t += 500; disp.Tick(t); }
        Check(disp.Values[0] == 2, $"DCS-BIOS 2,5 s sin datos: mantiene el último valor ({disp.Values[0]})");
        for (var i = 0; i < 60; i++) { t += 500; disp.Tick(t); }
        Check(double.IsNaN(disp.Values[0]), "DCS-BIOS 30 s sin datos: el valor vuelve a NaN");
        fakeOut.Send(Frame(0, Name("AH-64D_BLK_II")), group); Pump(500);
        Check(double.IsNaN(disp.Values[0]), "al volver DCS, la dirección vieja no vale hasta que llegue de nuevo");
        return fails;
    }
}
