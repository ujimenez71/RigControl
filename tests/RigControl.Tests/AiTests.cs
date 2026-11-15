using RigControl.Core;
using RigControl.Core.Ai;
using RigControl.Core.Bindings;

namespace RigControl.Tests;

/// <summary>
/// Generar con IA, sin llamar a la API: la herramienta validar_perfil (lo que le contesta RigControl
/// al modelo) y el contexto que se le manda.
/// </summary>
internal static class AiTests
{
    public static int Run()
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        var catalog = new DeviceCatalog
        {
            Devices =
            [
                new CatalogEntry { Key = "3344:832E:080B2200", Alias = "Colectivo", ProductName = "VPC Rotor TCS", Vid = "3344", Pid = "832E",
                                   Inputs = new() { ["b28"] = "Foco arriba" } },
                new CatalogEntry { Key = "294B:1900", Alias = "Yoke Alpha", ProductName = "Alpha Flight Controls", Vid = "294B", Pid = "1900" },
            ],
        };
        DcsControl[] dcs =
        [
            new("PLT_COLLECTIVE_SEARCHLIGHT", "Collective", "Searchlight Switch", ["set_state"], 0x10, 3, 0, 2, ["STOW", "OFF", "ON"]),
            new("PLT_MASTER_CAUTION_L", "Lights", "Master Caution Light", [], 0x12, 1, 0, 1, []),
        ];

        const string good = """
            {"name":"Prueba","sim":"dcs","aircraft":["dcs:ah-64d_blk_ii"],
             "devices":{"col":"3344:832E:080B2200"},
             "bindings":[
               {"input":"col:b28","gesture":"press","action":"D:PLT_COLLECTIVE_SEARCHLIGHT 2","desc":"Foco ON"},
               {"input":"col:b28","gesture":"release","action":"D:PLT_COLLECTIVE_SEARCHLIGHT 1","desc":"Foco Off"}],
             "lights":{"pto2:MASTER_CAUTION":"D:PLT_MASTER_CAUTION_L"}}
            """;
        var v = ProfileGenerator.Validate(good, catalog, dcs, null);
        Check(v.Ok && v.Text.StartsWith("OK") && v.File?.Bindings.Count == 2, $"perfil correcto -> {v.Text}");

        v = ProfileGenerator.Validate(good.Replace("PLT_COLLECTIVE_SEARCHLIGHT 1", "PLT_FOCO_INVENTADO 1"), catalog, dcs, null);
        Check(!v.Ok && v.Text.Contains("D:PLT_FOCO_INVENTADO no existe"), "control D: que no existe en el avión = error");

        v = ProfileGenerator.Validate(good.Replace("D:PLT_MASTER_CAUTION_L", "D:PLT_NO_HAY_LUZ"), catalog, dcs, null);
        Check(!v.Ok && v.Text.Contains("PLT_NO_HAY_LUZ"), "también en las luces");

        v = ProfileGenerator.Validate(good.Replace("3344:832E:080B2200", "3344:9999"), catalog, dcs, null);
        Check(!v.Ok && v.Text.Contains("no está en el catálogo"), "dispositivo que no está en el catálogo = error");

        v = ProfileGenerator.Validate(good.Replace("3344:832E:080B2200", "3344:832E"), catalog, dcs, null);
        Check(v.Ok, "VID:PID sin serial encaja con el del catálogo");

        v = ProfileGenerator.Validate(good.Replace("\"gesture\":\"release\"", "\"gesture\":\"press\""), catalog, dcs, null);
        Check(!v.Ok && v.Text.Contains("ERROR"), $"binding duplicado = error del Conflict Guard ({v.Text.Split('\n').Last()})");

        v = ProfileGenerator.Validate(good.Replace("col:b28", "col:boton28"), catalog, dcs, null);
        Check(!v.Ok && v.Text.Contains("no válida"), "entrada mal escrita = error del compilador");

        v = ProfileGenerator.Validate("{\"name\": ", catalog, dcs, null);
        Check(!v.Ok && v.File is null && v.Text.StartsWith("JSON no válido"), "JSON roto = error, sin perfil");

        const string msfs = """
            {"name":"C172","sim":"msfs","aircraft":["c172"],"devices":{"yoke":"294B:1900"},
             "bindings":[{"input":"yoke:b15","gesture":"press","action":"B:ELECTRICAL_BATTERY_1=1"},
                         {"input":"yoke:b16","gesture":"press","action":"B:BATERIA_INVENTADA=0"}]}
            """;
        var events = new HashSet<string>(["ELECTRICAL_BATTERY_1"], StringComparer.OrdinalIgnoreCase);
        v = ProfileGenerator.Validate(msfs, catalog, null, events);
        Check(!v.Ok && v.Text.Contains("B:BATERIA_INVENTADA no existe") && !v.Text.Contains("ELECTRICAL_BATTERY_1 no"),
              "B: que no existe en el avión de MSFS = error (y el bueno no)");
        v = ProfileGenerator.Validate(msfs, catalog, null, null);
        Check(v.Ok, "sin lista de B: (MSFS cerrado) no se comprueban los nombres");

        // Contexto: lleva los mandos con su clave y nombres, los controles del avión, el perfil de partida y lo pedido.
        var request = new GenerationRequest("dcs", "dcs:ah-64d_blk_ii", "Quiero el foco en el colectivo",
                                            BaseProfileJson: good, ConnectedKeys: new HashSet<string> { "294B:1900" });
        var ctx = ProfileGenerator.BuildContext(request, catalog, dcs);
        Check(ctx.Contains("clave \"3344:832E:080B2200\"") && ctx.Contains("b28 = Foco arriba"), "contexto: mandos con clave y nombres de entradas");
        Check(ctx.Contains("Yoke Alpha") && ctx.Contains("· conectado") && ctx.Contains("NO conectado"), "contexto: qué está conectado");
        Check(ctx.Contains("PLT_COLLECTIVE_SEARCHLIGHT | Collective | Searchlight Switch | set_state | 0=STOW, 1=OFF, 2=ON"),
              "contexto: controles de DCS-BIOS con sus posiciones");
        Check(ctx.Contains("PLT_MASTER_CAUTION_L | Lights | Master Caution Light | solo salida"), "contexto: luces = solo salida");
        Check(ctx.Contains("# Perfil de partida") && ctx.Contains("Quiero el foco en el colectivo"), "contexto: perfil de partida y preferencias");
        Check(ctx.Contains("pto2:MASTER_CAUTION") && ctx.Contains("GearLeftGreen"), "contexto: luces de WinWing y del Bravo");

        var ctxMsfs = ProfileGenerator.BuildContext(new GenerationRequest("msfs", "c172", "", events), catalog, null);
        Check(ctxMsfs.Contains("Controles B: del avión cargado en MSFS (1)") && ctxMsfs.Contains("ELECTRICAL_BATTERY_1"),
              "contexto MSFS: lista de B:");
        var noEvents = new GenerationRequest("msfs", "c172", "");
        Check(ProfileGenerator.BuildContext(noEvents, catalog, null, canAskForSim: true).Contains("pídela con pedir_simulador"),
              "contexto MSFS sin B:: ofrece pedir_simulador si se puede");
        Check(!ProfileGenerator.BuildContext(noEvents, catalog, null).Contains("pedir_simulador"),
              "contexto MSFS sin B:: no la nombra si no se puede (RigProbe sin consola, pruebas)");
        Check(ProfileGenerator.BuildContext(new GenerationRequest("dcs", "dcs:no-existe", ""), catalog, [], canAskForSim: true)
                  .Contains("con pedir_simulador se lee el avión cargado en DCS"),
              "contexto DCS sin controles: ofrece pedir_simulador");

        Check(SimSnapshot.AircraftFolder(@"SimObjects\Airplanes\Asobo_C172SP_G1000\aircraft.cfg") == "asobo_c172sp_g1000",
              $"carpeta del avión de MSFS: {SimSnapshot.AircraftFolder(@"SimObjects\Airplanes\Asobo_C172SP_G1000\aircraft.cfg")}");
        return fails;
    }
}
