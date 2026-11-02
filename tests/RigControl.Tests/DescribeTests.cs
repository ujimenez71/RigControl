using RigControl.Core.Bindings;

namespace RigControl.Tests;

/// <summary>Descripciones en español de las acciones (pantalla Perfiles) y "desc" del perfil.</summary>
internal static class DescribeTests
{
    public static int Run()
    {
        var fails = 0;
        void Check(string action, string expected)
        {
            var got = ActionDescriber.Describe(action);
            var ok = got == expected;
            Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {action,-45} -> {got}{(ok ? "" : $"   (esperado: {expected})")}");
            if (!ok) fails++;
        }

        Check("B:ELECTRICAL_BATTERY_1=1", "Batería 1: encender");
        Check("B:ELECTRICAL_ALTERNATOR_1=0", "Alternador 1: apagar");
        Check("B:ELECTRICAL_LINE_BUS_2_TO_AVIONICS_BUS_2=1", "Bus de aviónica 2: encender");
        Check("B:ENGINE_MAGNETO_1=0", "Magnetos (motor 1): OFF");
        Check("B:ENGINE_MAGNETO_1=4", "Magnetos (motor 1): START (arranque)");
        Check("B:LIGHTING_BEACON_1=1", "Luz anticolisión (beacon) 1: encender");
        Check("B:LIGHTING_NAV_0=0", "Luces de navegación: apagar");
        Check("B:AS1000_AUTOPILOT_HEADING_PFD", "Autopiloto: modo rumbo (HDG)");
        Check("K:ELEV_TRIM_DN", "Trim de profundidad: morro abajo");
        Check("K:TOGGLE_NAV_LIGHTS", "Luces de navegación: alternar");
        Check("K:TOGGLE_FUEL_VALVE_ENG1", "Combustible válvula eng1: alternar");
        Check("B:LIGHTING_PANEL_1=0.5", "Iluminación del panel 1 = 0.5");
        Check("B:FOO_BAR_2=1", "Foo bar 2: encender");
        Check("key:CTRL+SHIFT+G", "Tecla CTRL+SHIFT+G");
        Check("D:UFC_1", "DCS: UFC_1 (pulsar)");
        Check("D:HDG_KNOB INC", "DCS: HDG_KNOB, subir un paso");
        Check("L:Mi_Var=3", "Variable local Mi_Var = 3");
        Check("Tren abajo", "Tren abajo");

        // "desc" del perfil gana a la automática
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            { "name": "t", "devices": { "y": "294B:1900" }, "bindings": [
              { "input": "y:b1", "gesture": "press", "action": "K:GEAR_UP", "desc": "Subir el tren" },
              { "input": "y:b2", "gesture": "press", "action": "K:GEAR_DOWN" } ] }
            """);
        var r = ProfileInspector.Inspect(path);
        var ok = r.Ok && r.Bindings[0] is { Description: "Subir el tren", CustomDescription: true }
                      && r.Bindings[1] is { Description: "Tren: bajar", CustomDescription: false };
        Console.WriteLine($"{(ok ? "OK  " : "FALLO")} \"desc\" del perfil y descripción automática en el informe");
        if (!ok) fails++;
        return fails;
    }
}
