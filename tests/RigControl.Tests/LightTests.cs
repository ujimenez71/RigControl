using RigControl.Core;
using RigControl.Core.Bindings;

namespace RigControl.Tests;

/// <summary>Luces del Bravo: reglas y salida.</summary>
internal static class LightTests
{
    public static int Run()
    {
        Log.Sink = _ => { };
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        var vars = new List<SimVarSpec>();
        int Idx(SimVarSpec s) { var i = vars.IndexOf(s); if (i >= 0) return i; vars.Add(s); return vars.Count - 1; }
        LightCondition P(string t) { Check(LightParser.TryParse(t, Idx, out var c, out var e), $"interpreta \"{t}\" {e}"); return c; }

        var ap = P("A:autopilot master");
        var volts = P("A:ELECTRICAL MAIN BUS VOLTAGE, Volts=1..24.5");
        var oil = P("A:GENERAL ENG OIL PRESSURE:1, psi<20");
        var brake = P("!A:BRAKE PARKING POSITION");
        var ge = P("A:X >= 2.5");
        var lv = P("L:Mi_Var=3");
        Check(vars[0] == new SimVarSpec("A:AUTOPILOT MASTER", "Number") && vars[0].DatumName == "AUTOPILOT MASTER", $"A: en mayúsculas y DatumName sin prefijo ({vars[0]})");
        Check(vars[1].Unit == "Volts" && vars[2].Name == "A:GENERAL ENG OIL PRESSURE:1", "unidades e índice :1");
        Check(vars[5].Name == "L:Mi_Var" && vars[5].DatumName == "L:Mi_Var", "L: respeta el nombre y conserva el prefijo");

        var v = new double[vars.Count]; Array.Fill(v, double.NaN);
        Check(!ap.Evaluate(v) && !brake.Evaluate(v), "sin dato (NaN): apagada, también si está negada");
        v[0] = 1; v[1] = 0; v[2] = 50; v[3] = 0; v[4] = 2.5; v[5] = 3;
        Check(ap.Evaluate(v), "AP master = 1 -> encendida");
        Check(!volts.Evaluate(v), "bus a 0 V (todo apagado) -> LowVolts apagada (rango 1..24.5)");
        v[1] = 23; Check(volts.Evaluate(v), "bus a 23 V -> LowVolts encendida");
        v[1] = 28; Check(!volts.Evaluate(v), "bus a 28 V -> LowVolts apagada");
        Check(!oil.Evaluate(v), "aceite 50 psi -> apagada"); v[2] = 10; Check(oil.Evaluate(v), "aceite 10 psi -> encendida");
        Check(brake.Evaluate(v), "!freno de parking con 0 -> encendida");
        Check(ge.Evaluate(v), ">= 2.5 con 2.5"); Check(lv.Evaluate(v), "L:=3 con 3");

        foreach (var bad in new[] { "AUTOPILOT MASTER", "A:", "A:X, Volts<abc", "A:X=5..1", "K:GEAR_UP" })
            Check(!LightParser.TryParse(bad, Idx, out _, out var e) && e is not null, $"rechaza \"{bad}\": {e}");

        // Perfil: nombres de luz, lista = AND, variables compartidas, errores
        (CompiledProfile? Pr, List<string> E) Compile(string lights)
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, $$"""{ "name": "t", "devices": {}, "bindings": [], "lights": { {{lights}} } }""");
            return ProfileCompiler.Compile(ProfileFile.Load(path));
        }
        var (pr, errs) = Compile("""
          "autopilot": "A:AUTOPILOT MASTER",
          "LowVolts": "A:ELECTRICAL MAIN BUS VOLTAGE, Volts=1..24.5",
          "LowOilPressure": [ "A:GENERAL ENG OIL PRESSURE:1, psi<20", "A:ELECTRICAL MAIN BUS VOLTAGE, Volts>1" ]
        """);
        Check(pr is not null, "perfil con luces compila " + string.Join(" | ", errs));
        Check(pr!.Lights.Length == 3 && pr.WatchedVars.Length == 3, $"3 reglas y 3 variables (la tensión se comparte): {pr.WatchedVars.Length}");
        Check(pr.Lights[0].Led == BravoLed.Autopilot, "\"autopilot\" sin distinguir mayúsculas");
        foreach (var (bad, why) in new[] { ("\"Turbo\": \"A:X\"", "luz desconocida"), ("\"Hdg\": 5", "valor no texto"),
                                           ("\"Hdg\": []", "lista vacía"), ("\"Hdg\": \"A:X\", \"hdg\": \"A:Y\"", "luz repetida") })
        {
            var (p2, e2) = Compile(bad);
            Check(p2 is null, $"rechaza {why}: {e2.FirstOrDefault()}");
        }

        // BravoLightsOutput con valores simulados (escribe en el Bravo real): solo con --hardware
        if (!Program.Hardware) { Console.WriteLine("(se omite la prueba con el Bravo real: usa --hardware)"); return fails; }
        using (var outp = new BravoLightsOutput())
        {
            outp.SetProfile(pr);
            var vals = new double[] { 1, 23, 10 };
            outp.Update(vals, simConnected: true, 0);
            Check(outp.Current == (BravoLed.Autopilot | BravoLed.LowVolts | BravoLed.LowOilPressure), $"AP + LowVolts + LowOil: {outp.Current}");
            Thread.Sleep(800);
            vals[0] = 0; vals[1] = 28; vals[2] = 60;
            outp.Update(vals, true, 100);
            Check(outp.Current == BravoLed.None, $"todo normal -> ninguna: {outp.Current}");
            vals[0] = 1;
            outp.Update(vals, false, 200);
            Check(outp.Current == BravoLed.None, "sin simulador -> ninguna");
            outp.SetProfile(CompiledProfile.Empty("x"));
            outp.Update(vals, true, 300);
            Check(!outp.Active && outp.Current == BravoLed.None, "perfil sin luces -> no toca el Bravo");
        }
        return fails;
    }
}
