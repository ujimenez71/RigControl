using RigControl.Core;
using RigControl.Core.Bindings;
using RigControl.Core.Sheets;

namespace RigControl.Tests;

/// <summary>Hoja de mandos: qué entradas salen, en qué orden y cómo se juntan las filas (sin fotos).</summary>
internal static class SheetTests
{
    public static int Run()
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        var catalog = new DeviceCatalog
        {
            Devices =
            [
                new CatalogEntry { Key = "FFF1:0001:XYZ", Alias = "Palanca", ProductName = "Palanca de prueba", Vid = "FFF1", Pid = "0001",
                                   Inputs = new() { ["b28"] = "Foco arriba", ["hat1"] = "Trim" } },
            ],
        };
        var file = ProfileFile.Parse("""
            {"name":"Hoja","sim":"msfs","aircraft":["c172"],
             "devices":{"p":"FFF1:0001"},
             "layers":{"shift":{"input":"p:b5"}},
             "bindings":[
               {"input":"p:b28","gesture":"press","action":"K:TOGGLE_NAV_LIGHTS","desc":"Foco <ON>"},
               {"input":"p:b28","gesture":"release","action":"K:TOGGLE_BEACON_LIGHTS","desc":"Foco Off"},
               {"input":"p:b10","gesture":"short","action":"K:AP_MASTER","desc":"AP"},
               {"input":"p:b10","gesture":"short","when":{"layer":"shift"},"action":"K:AUTOPILOT_OFF","desc":"AP off"},
               {"input":"p:hat1.down","gesture":"press","action":"K:ELEV_TRIM_DN","desc":"Trim abajo"},
               {"input":"p:hat1.up","gesture":"press","action":"K:ELEV_TRIM_UP","desc":"Trim arriba"},
               {"input":"p:y>95","gesture":"press","action":"K:FLAPS_UP","desc":"Flaps arriba"},
               {"sequence":["p:b2","p:b3"],"action":"K:PARKING_BRAKES","desc":"Freno"}]}
            """);
        var (_, devices) = ProfileSheet.Collect(file, "hoja.json", catalog);
        Check(devices.Count == 1 && devices[0].Name == "Palanca (Palanca de prueba)", "un mando, con su nombre del catálogo");
        var inputs = devices[0].Inputs;
        var parts = string.Join(",", inputs.Select(i => i.Part));
        Check(parts == "b2,b3,b5,b10,b28,hat1.up,hat1.down,y>95", $"orden natural: botones, hats (arriba antes que abajo), ejes ({parts})");
        Check(inputs.Select(i => i.Number).SequenceEqual(Enumerable.Range(1, inputs.Count)), "números seguidos desde 1");
        var b28 = inputs.First(i => i.Part == "b28");
        Check(b28.Rows.Count == 1 && b28.Rows[0].Release == "Foco Off", "pulsar + soltar en una sola fila");
        Check(b28.Label == "Foco arriba (b28)", $"nombre de la entrada del catálogo ({b28.Label})");
        Check(inputs.First(i => i.Part == "hat1.up").Label.StartsWith("Trim ↑"), "hats con el nombre del hat y la flecha");
        var b10 = inputs.First(i => i.Part == "b10");
        Check(b10.Rows.Count == 2 && b10.Rows.Any(r => r.Condition.Length > 0 && r.Release is null), "condicionados en filas aparte");
        Check(inputs.First(i => i.Part == "b5").Rows[0].Description.Contains("capa shift"), "modificador de una capa: también sale");
        Check(inputs.First(i => i.Part == "b3").Rows[0].Gesture == "secuencia, paso 2 de 2", "secuencia: cada paso en su entrada");
        Check(inputs.All(i => i.Position is null) && devices[0].ImagePath is null, "sin foto: sin posiciones");

        var html = ProfileSheet.BuildHtml(file, "hoja.json", catalog);
        Check(html.Contains("Foco &lt;ON&gt;") && !html.Contains("Foco <ON>"), "HTML: texto escapado");
        Check(html.Contains("layout single") && html.Contains("Sin foto de este mando"), "HTML: sin foto, la tabla a todo lo ancho");

        Check(DeviceLayout.IdFor("346e:1000:ABC") == "346E_1000", "id de la foto = VID_PID");
        var layout = new DeviceLayout { Markers = new(StringComparer.OrdinalIgnoreCase) { ["hat1"] = new(0.5, 0.5), ["y"] = new(0.1, 0.2), ["b3"] = new(0.3, 0.3) } };
        Check(layout.Find("hat1.left") == new MarkerPosition(0.5, 0.5) && layout.Find("y<10") == new MarkerPosition(0.1, 0.2)
              && layout.Find("B3") == new MarkerPosition(0.3, 0.3) && layout.Find("b4") is null,
              "posición: exacta o la del hat/eje entero");

        try
        {
            ProfileSheet.Collect(ProfileFile.Parse("""{"devices":{"p":"FFF1:0001"},"bindings":[{"input":"p:b1","gesture":"rara","action":"K:X"}]}"""), "mal.json", catalog);
            Check(false, "perfil con errores: no hace la hoja");
        }
        catch (InvalidDataException)
        {
            Check(true, "perfil con errores: no hace la hoja");
        }
        return fails;
    }
}
