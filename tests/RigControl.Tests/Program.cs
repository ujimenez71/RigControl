using RigControl.Core;

namespace RigControl.Tests;

/// <summary>
/// Pruebas de escenarios sin necesidad de simulador: dotnet run --project tests/RigControl.Tests
/// Con --hardware también escribe en el Bravo real (parpadean sus luces un momento).
/// </summary>
internal static class Program
{
    public static bool Hardware { get; private set; }

    private static int Main(string[] args)
    {
        Hardware = args.Contains("--hardware");
        var original = Log.Sink;
        var total = 0;

        foreach (var (name, run) in new (string, Func<int>)[]
                 {
                     ("Ejes", AxisTests.Run),
                     ("Acciones", ActionTests.Run),
                     ("Repetición", RepeatTests.Run),
                     ("Luces", LightTests.Run),
                     ("Pantalla del ICP", IcpTests.Run),
                     ("DCS-BIOS", DcsTests.Run),
                     ("Estado del simulador", StateTests.Run),
                     ("Descripciones", DescribeTests.Run),
                     ("Simulador del perfil", SimTests.Run),
                     ("Guardar perfiles", SaveTests.Run),
                     ("Hangar", HangarTests.Run),
                     ("Mods", ModTests.Run),
                     ("Instantáneas", SnapshotTests.Run),
                 })
        {
            Console.WriteLine($"\n===== {name} =====");
            Log.Sink = _ => { }; // los mensajes del núcleo no interesan aquí
            int fails;
            try { fails = run(); }
            catch (Exception ex) { Console.WriteLine($"FALLO  excepción: {ex}"); fails = 1; }
            Console.WriteLine(fails == 0 ? "-> todo OK" : $"-> {fails} fallo(s)");
            total += fails;
        }

        Log.Sink = original;
        Console.WriteLine(total == 0 ? "\nTODAS LAS PRUEBAS OK" : $"\n{total} FALLO(S) EN TOTAL");
        return total == 0 ? 0 : 1;
    }
}
