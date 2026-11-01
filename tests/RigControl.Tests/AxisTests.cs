using RigControl.Core;
using RigControl.Core.Bindings;

namespace RigControl.Tests;

/// <summary>Ejes: zonas, histéresis y motor.</summary>
internal static class AxisTests
{
    public static int Run()
    {

        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }
        int Raw(double p) => (int)Math.Round(p * 65535 / 100);

        (CompiledProfile? P, List<string> E) Compile(string bindingsJson, double hyst = 2)
        {
            var json = $$"""
            { "name": "t", "axisHysteresis": {{hyst.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
              "devices": { "bravo": "294B:1901" },
              "layers": { "trenArriba": { "input": "bravo:b31" } },
              "bindings": [ {{bindingsJson}} ] }
            """;
            var path = Path.GetTempFileName();
            File.WriteAllText(path, json);
            return ProfileCompiler.Compile(ProfileFile.Load(path));
        }

        // ---- AxisZone puro
        var h = Raw(2);
        int lo = Raw(90), hi = 65535;
        Check(!AxisZone.Next(false, Raw(90.5), lo, hi, h), ">90: 90.5% no entra (falta margen)");
        Check(AxisZone.Next(false, Raw(92.5), lo, hi, h), ">90: 92.5% entra");
        Check(AxisZone.Next(true, Raw(90.5), lo, hi, h), ">90: activo a 90.5% sigue dentro");
        Check(!AxisZone.Next(true, Raw(89.5), lo, hi, h), ">90: activo a 89.5% sale");
        Check(AxisZone.Next(false, 65535, lo, hi, h), ">90: tope 100% entra (sin margen en el extremo)");
        Check(AxisZone.Next(false, 0, 0, Raw(10), h), "<10: 0% entra");
        Check(!AxisZone.IsReachable(Raw(40), Raw(43), h), "40..43 con 2% de histéresis es inalcanzable");

        // ---- Compilador
        var (p, e) = Compile("""
          { "input": "bravo:x>90", "gesture": "press", "action": "Gases a fondo" },
          { "input": "bravo:x>90", "gesture": "release", "action": "Sale de fondo" },
          { "input": "bravo:y=0..33", "gesture": "press", "action": "Flaps 0" },
          { "input": "bravo:y=33..66", "gesture": "press", "action": "Flaps 1" },
          { "input": "bravo:y=66..100", "gesture": "press", "action": "Flaps 2" },
          { "input": "bravo:x<20", "gesture": "press", "when": { "layer": "trenArriba" }, "action": "AVISO tren" },
          { "input": "bravo:b1", "gesture": "short", "when": { "input": "bravo:x>90" }, "action": "b1 con gases a fondo" },
          { "input": "bravo:b1", "gesture": "short", "action": "b1 normal" }
        """);
        Check(p is not null, "perfil válido compila" + (e.Count > 0 ? ": " + string.Join(" | ", e) : ""));

        foreach (var (bad, why) in new[] {
            ("bravo:q>50", "eje desconocido"), ("bravo:x>150", "porcentaje > 100"), ("bravo:x=60..40", "rango invertido"),
            ("bravo:x=40..42", "zona demasiado estrecha"), ("bravo:x>=50", "operador >="), ("bravo:x=40", "rango sin .."), })
        {
            var (bp, be) = Compile($$"""{ "input": "{{bad}}", "gesture": "press", "action": "a" }""");
            Check(bp is null && be.Count > 0, $"rechaza {why}: {be.FirstOrDefault()}");
        }
        Check(Compile("""{ "input": "bravo:x>50", "gesture": "press", "action": "a" }""", 30).P is null, "rechaza axisHysteresis 30");

        // ---- Motor + histéresis simulando el runner
        var log = new List<string>();
        var sink = new Sink(log);
        var engine = new BindingEngine(p!, sink);
        long t = 0;

        void Axis(int device, int axis, double pct)
        {
            t += 16;
            for (var s = 0; s < p!.Inputs.Length; s++)
            {
                var inp = p.Inputs[s];
                if (inp.Kind != InputKind.AxisZone || inp.Index != axis) continue;
                engine.SetInput(s, AxisZone.Next(engine.IsDown(s), Raw(pct), inp.Lo, inp.Hi, p.AxisHysteresis), t);
            }
            engine.Tick(t);
        }
        void Button(string name, bool v) { t += 16; engine.SetInput(Array.IndexOf(p!.InputNames, name), v, t); engine.Tick(t); }
        void Wait(int ms) { for (var i = 0; i < ms / 16; i++) { t += 16; engine.Tick(t); } }
        List<string> Take() { var r = log.ToList(); log.Clear(); return r; }

        // Estado inicial: x=50, y=10 (flaps 0), tren abajo.
        for (var s = 0; s < p!.Inputs.Length; s++)
        {
            var inp = p.Inputs[s];
            var pct = inp.Index == 0 ? 50 : 10;
            engine.Prime(s, inp.Kind == InputKind.AxisZone && AxisZone.Next(false, Raw(pct), inp.Lo, inp.Hi, p.AxisHysteresis));
        }

        foreach (var v in new[] { 60, 80, 89, 90.5, 91, 93, 95, 100 }) Axis(0, 0, v);
        var got = Take();
        Check(got.SequenceEqual(["Gases a fondo"]), $"subir gases: un único 'Gases a fondo' ({string.Join(", ", got)})");

        foreach (var v in new[] { 91, 89.9, 90.5, 91.5, 90.2, 89.5, 91 }) Axis(0, 0, v);
        got = Take();
        Check(got.SequenceEqual(["Sale de fondo"]), $"ruido junto al 90%: sale una vez y no vuelve a entrar ({string.Join(", ", got)})");

        Axis(0, 0, 95); Take();
        Button("bravo:b1", true); Button("bravo:b1", false);
        got = Take();
        Check(got.SequenceEqual(["b1 con gases a fondo"]), $"condición sobre eje: b1 con gases a fondo ({string.Join(", ", got)})");
        Axis(0, 0, 50); Take();
        Button("bravo:b1", true); Button("bravo:b1", false);
        Check(Take().SequenceEqual(["b1 normal"]), "b1 sin gases a fondo -> normal");

        foreach (var v in new[] { 20, 30, 33, 34, 35, 36, 50, 65, 67, 68, 69, 80, 60, 40, 20 }) Axis(0, 1, v);
        got = Take();
        Check(got.SequenceEqual(["Flaps 1", "Flaps 2", "Flaps 1", "Flaps 0"]), $"flaps por zonas contiguas ({string.Join(", ", got)})");

        Axis(0, 0, 10); got = Take();
        Check(got.Count == 0, "gases al ralentí con tren abajo: sin aviso");
        Axis(0, 0, 50); Button("bravo:b31", true); Take();
        Axis(0, 0, 10); got = Take();
        Check(got.SequenceEqual(["AVISO tren"]), $"gases al ralentí con tren arriba: aviso ({string.Join(", ", got)})");

        return fails;
    }
}

sealed class Sink(List<string> log) : IActionSink
{
    public void OnAction(Binding b, ActionPhase phase, long t) => log.Add(b.Action);
}
