using RigControl.Core.Bindings;

namespace RigControl.Tests;

/// <summary>"sim" del perfil: deducción, errores y que en automático no se cruzan MSFS y DCS.</summary>
internal static class SimTests
{
    public static int Run()
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        SimKind Resolve(string? sim, params string[] aircraft) =>
            ProfileCompiler.ResolveSim(new ProfileFile { Sim = sim, Aircraft = [.. aircraft] }, out var e) is var k && e is null
                ? k : throw new InvalidOperationException(e);
        string? Error(string? sim, params string[] aircraft)
        {
            ProfileCompiler.ResolveSim(new ProfileFile { Sim = sim, Aircraft = [.. aircraft] }, out var e);
            return e;
        }

        Check(Resolve(null, "c172") == SimKind.Msfs, "deduce MSFS de \"c172\"");
        Check(Resolve(null, "dcs:f-15e", "dcs:ah-64d") == SimKind.Dcs, "deduce DCS de \"dcs:...\"");
        Check(Resolve(null) == SimKind.General && Resolve(null, "*") == SimKind.General, "sin aviones o solo \"*\" = general");
        Check(Resolve("msfs", "*") == SimKind.Msfs && Resolve("DCS") == SimKind.Dcs, "\"sim\" explícito (sin distinguir mayúsculas)");
        foreach (var (sim, ac, why) in new (string?, string[], string)[]
                 {
                     (null, ["c172", "dcs:f-15e"], "mezcla MSFS y DCS"), ("msfs", ["dcs:f-15e"], "msfs con avión de DCS"),
                     ("dcs", ["f-15e"], "dcs sin prefijo dcs:"), ("general", ["c172"], "general con aviones"),
                     ("xplane", [], "simulador desconocido"),
                 })
        {
            var e = Error(sim, ac);
            Check(e is not null, $"rechaza {why}: {e}");
        }

        // Selección en automático: un "*" de MSFS no debe activarse en DCS, y uno de DCS no en MSFS.
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "a-msfs-any.json"), """{ "name": "MSFS comodín", "sim": "msfs", "aircraft": ["*"], "devices": {}, "bindings": [] }""");
        File.WriteAllText(Path.Combine(dir, "b-dcs-any.json"), """{ "name": "DCS comodín", "sim": "dcs", "aircraft": ["*"], "devices": {}, "bindings": [] }""");
        File.WriteAllText(Path.Combine(dir, "c-c172.json"), """{ "name": "C172", "aircraft": ["c172"], "devices": {}, "bindings": [] }""");
        using var simc = new SimConnectOutput();
        using var dcs = new DcsBiosOutput(25010, 27778);
        using var auto = new AutoProfileSource(dir, simc, dcs);
        var aircraft = typeof(AutoProfileSource).GetField("_aircraft", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var select = typeof(AutoProfileSource).GetMethod("Select", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        string Pick(string? a) { aircraft.SetValue(auto, a); return ((CompiledProfile)select.Invoke(auto, [false])!).Name; }
        auto.Poll(0); // carga la carpeta

        Check(Pick(@"simobjects\airplanes\asobo_c172sp_g1000\aircraft.cfg") == "C172", "MSFS + c172 -> C172");
        Check(Pick(@"simobjects\airplanes\otro\aircraft.cfg") == "MSFS comodín", "MSFS + avión sin perfil -> comodín de MSFS");
        Check(Pick("dcs:f-16c_50") == "DCS comodín", "DCS + avión sin perfil -> comodín de DCS (no el de MSFS)");
        Check(Pick(null) == "MSFS comodín", "sin avión todavía -> comodín que no sea de DCS");
        return fails;
    }
}
