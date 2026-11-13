using RigControl.Core;
using RigControl.Core.Bindings;
using RigControl.Core.WinWing;

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

        fails += WinWing(Compile, Check);

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

    /// <summary>WinWing falso: apunta cada escritura (PID, LED, valor).</summary>
    private sealed class FakeLeds(int pid, List<(int Pid, byte Led, byte Value)> writes) : IWinWingLeds
    {
        public bool Fail;
        public void Set(byte led, byte value, int? module = null)
        {
            if (Fail) throw new IOException("desconectado");
            writes.Add((pid, led, value));
        }
        public void Dispose() { }
    }

    /// <summary>Luces de WinWing: nombres, reglas y WinWingLightsOutput con dispositivos falsos.</summary>
    private static int WinWing(Func<string, (CompiledProfile?, List<string>)> compile, Action<bool, string> check)
    {
        var check2 = check; // el Check de Run ya cuenta los fallos
        void Check(bool ok, string what) => check2(ok, what);

        Check(WinWingPanel.FindLight("PTO2:gear_nose") is { ProductId: 0xBF05, Led: 12, Name: "pto2:GEAR_NOSE" },
              "\"PTO2:gear_nose\" -> BF05, LED 12, nombre normalizado");
        Check(WinWingPanel.FindLight("pto2:TURBO") is null && WinWingPanel.FindLight("icp:BACKLIGHT") is null, "luz o dispositivo desconocido -> null");
        Check(WinWingPanel.DefaultModule(0xBEE2) == 0xBE0D && WinWingPanel.DefaultModule(0xBD64) == 0xBE60 &&
              WinWingPanel.DefaultModule(0xBF05) == 0xBF05, "módulos: MFD BE0D, acelerador BE60, PTO 2 su PID");

        var (pr, errs) = compile("""
          "Hdg": "A:AUTOPILOT HEADING LOCK",
          "pto2:gear_nose": "A:GEAR CENTER POSITION=0.99..1",
          "pto2:MASTER_CAUTION": "A:MASTER CAUTION ACTIVE",
          "throttle:A_A": [ "A:MASTER CAUTION ACTIVE", "!A:AUTOPILOT HEADING LOCK" ]
        """);
        Check(pr is not null, "perfil con luces del Bravo y de WinWing compila " + string.Join(" | ", errs));
        Check(pr!.Lights.Count(l => l.WinWing is not null) == 3 && pr.Lights.Single(l => l.WinWing is null).Led == BravoLed.Hdg,
              "3 de WinWing (Led = None) + 1 del Bravo");
        foreach (var (bad, why) in new[] { ("\"pto2:TURBO\": \"A:X\"", "luz de WinWing desconocida"),
                                           ("\"pto2:HOOK\": \"A:X\", \"PTO2:hook\": \"A:Y\"", "repetida con otras mayúsculas") })
        {
            var (p2, e2) = compile(bad);
            Check(p2 is null, $"rechaza {why}: {e2.FirstOrDefault()}");
        }

        var writes = new List<(int Pid, byte Led, byte Value)>();
        var fakes = new Dictionary<int, FakeLeds>();
        var available = new HashSet<int> { 0xBF05 }; // el acelerador "no está" al principio
        using (var outp = new WinWingLightsOutput(pid => available.Contains(pid) ? fakes[pid] = new FakeLeds(pid, writes) : null))
        {
            outp.SetProfile(pr);
            Check(outp.Active, "Active con luces de WinWing");
            // Variables: 0 heading lock, 1 gear center, 2 master caution
            var v = new double[] { 0, 1, 1 };
            outp.Update(v, simConnected: true, 0);
            Check(writes.SequenceEqual([(0xBF05, (byte)12, (byte)255), (0xBF05, (byte)4, (byte)255)]),
                  $"tren abajo + master caution -> LED 12 y 4 a 255 (el acelerador no está): {Fmt(writes)}");
            Check(outp.Lit.SequenceEqual(["pto2:GEAR_NOSE", "pto2:MASTER_CAUTION", "throttle:A_A"]), $"Lit: {string.Join(", ", outp.Lit)}");

            writes.Clear();
            outp.Update(v, true, 100);
            Check(writes.Count == 0, "sin cambios -> no escribe");

            available.Add(0xBD64);
            outp.Update(v, true, 1000);
            Check(writes.Count == 0, "acelerador aparece pero no se reintenta antes de 5 s");
            outp.Update(v, true, 5000);
            Check(writes.SequenceEqual([(0xBD64, (byte)1, (byte)255)]), $"a los 5 s se abre el acelerador y enciende A-A: {Fmt(writes)}");

            writes.Clear();
            v[1] = 0.5;
            outp.Update(v, true, 5100);
            Check(writes.SequenceEqual([(0xBF05, (byte)12, (byte)0)]), $"tren en tránsito -> solo LED 12 a 0: {Fmt(writes)}");

            writes.Clear();
            fakes[0xBF05].Fail = true;
            v[2] = 0;
            outp.Update(v, true, 5200);
            Check(!writes.Any(w => w.Pid == 0xBF05) && writes.SequenceEqual([(0xBD64, (byte)1, (byte)0)]),
                  $"error al escribir en el PTO 2: se suelta; el acelerador sí apaga A-A: {Fmt(writes)}");
            fakes[0xBF05].Fail = false;
            outp.Update(v, true, 10300);
            Check(writes.Contains((0xBF05, (byte)4, (byte)0)) && writes.Contains((0xBF05, (byte)12, (byte)0)),
                  $"al reabrir el PTO 2 se reescribe todo su estado: {Fmt(writes)}");

            writes.Clear();
            outp.Update(v, simConnected: false, 10400);
            Check(outp.Lit.Count == 0 && writes.Count == 0, "sin simulador -> todas apagadas (ya lo estaban: nada que escribir)");

            // Perfil nuevo que solo nombra el gancho: se suelta lo demás (a 0) y nada del Bravo.
            writes.Clear();
            var (p3, _) = compile("\"pto2:HOOK\": \"A:X\"");
            outp.SetProfile(p3!);
            outp.Update([1], true, 10500);
            Check(writes.Contains((0xBF05, (byte)17, (byte)255)) && writes.Contains((0xBF05, (byte)12, (byte)0)) &&
                  writes.Contains((0xBD64, (byte)1, (byte)0)) && writes.Contains((0xBF05, (byte)4, (byte)0)),
                  $"cambio de perfil: gancho encendido y las anteriores soltadas: {Fmt(writes)}");

            writes.Clear();
            outp.SetProfile(CompiledProfile.Empty("x"));
            outp.Update([], true, 10600);
            Check(!outp.Active && writes.SequenceEqual([(0xBF05, (byte)17, (byte)0)]), $"perfil sin luces: suelta el gancho y nada más: {Fmt(writes)}");
            writes.Clear();
            outp.Update([], true, 10700);
            Check(writes.Count == 0, "y después no toca nada");

            var (p4, _) = compile("\"pto2:BACKLIGHT\": \"A:X\"");
            outp.SetProfile(p4!);
            outp.Update([0], true, 10800);
            writes.Clear();
        }
        Check(writes.SequenceEqual([(0xBF05, (byte)0, (byte)255)]), $"al salir, la retroiluminación vuelve a 255 (no a 0): {Fmt(writes)}");
        return 0;
    }

    private static string Fmt(List<(int Pid, byte Led, byte Value)> w) => string.Join(" ", w.Select(x => $"{x.Pid:X4}/{x.Led}={x.Value}"));
}
