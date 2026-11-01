using RigControl.Core;
using RigControl.Core.Bindings;

namespace RigControl.Tests;

/// <summary>Repetición de hold (repeatMs).</summary>
internal static class RepeatTests
{
    public static int Run()
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }
        (CompiledProfile? P, List<string> E) Compile(string b)
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, $$"""{ "name": "t", "devices": { "y": "294B:1900" }, "bindings": [ {{b}} ] }""");
            return ProfileCompiler.Compile(ProfileFile.Load(path));
        }

        foreach (var (json, why) in new[] {
            ("""{ "input": "y:b1", "gesture": "press", "action": "K:X", "repeatMs": 50 }""", "repeatMs fuera de hold"),
            ("""{ "input": "y:b1", "gesture": "hold", "action": "key:A", "repeatMs": 50 }""", "repeatMs con key:"),
            ("""{ "input": "y:b1", "gesture": "hold", "action": "K:X", "repeatMs": 5 }""", "repeatMs demasiado bajo") })
        {
            var (p, e) = Compile(json);
            Check(p is null, $"rechaza {why}: {e.FirstOrDefault()}");
        }

        var (prof, _) = Compile("""{ "input": "y:hat1.up", "gesture": "hold", "action": "K:ELEV_TRIM_DN", "repeatMs": 50 }""");
        var sim = new FakeSim();
        var d = new ActionDispatcher(null, sim, dryRun: false);
        var engine = new BindingEngine(prof!, d);
        Console.SetOut(TextWriter.Null);
        long t = 1000;
        engine.SetInput(0, true, t); d.Tick(t);
        for (var i = 0; i < 31; i++) { t += 16; engine.Tick(t); d.Tick(t); }   // ~496 ms mantenido
        var whileHeld = sim.Sent;
        engine.SetInput(0, false, t); d.Tick(t);
        for (var i = 0; i < 20; i++) { t += 16; engine.Tick(t); d.Tick(t); }
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Check(whileHeld is >= 9 and <= 11, $"~500 ms mantenido a 50 ms: 1 + ~9 repeticiones (hubo {whileHeld})");
        Check(sim.Sent == whileHeld, $"al soltar deja de repetir ({sim.Sent} en total)");
        Check(d.ActiveRepeats == 0, "no queda ninguna repetición activa");

        // Cambio de perfil con el hat mantenido: EndHolds corta la repetición.
        engine.SetInput(0, true, t); d.Tick(t);
        Console.SetOut(TextWriter.Null);
        engine.EndHolds(t);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        var before = sim.Sent;
        for (var i = 0; i < 10; i++) { t += 16; d.Tick(t); }
        Check(sim.Sent == before && d.ActiveRepeats == 0, "EndHolds (cambio de perfil) corta la repetición");

        return fails;
    }
}

sealed class FakeSim : ISimOutput
{
    public int Sent;
    public bool IsConnected => true;
    public bool Send(ActionSpec a) { Sent++; return true; }
    public void Prepare(CompiledProfile p) { }
    public void Tick(long t) { }
}
