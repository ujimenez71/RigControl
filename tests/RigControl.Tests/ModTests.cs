using RigControl.Core.Hangar;
using RigControl.Core.Mods;

namespace RigControl.Tests;

/// <summary>Mods con uniones de directorio reales en una carpeta temporal (biblioteca + "Community" falsa).</summary>
internal static class ModTests
{
    public static int Run()
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        var root = Directory.CreateTempSubdirectory("rigmods").FullName;
        try
        {
            var lib = Path.Combine(root, "biblioteca");
            var community = Path.Combine(root, "Community");
            Directory.CreateDirectory(community);
            var running = false;
            var mgr = new ModManager(_ => running);
            var library = new ModLibrary { Name = "MSFS", Library = lib, Target = community, Process = "Sim", Always = ["navigraph"] };

            // Mods de verdad en Community, como ahora en el PC del usuario.
            foreach (var name in new[] { "orbx-png", "gran-canaria", "navigraph", "suelto" })
            {
                Directory.CreateDirectory(Path.Combine(community, name, "sub"));
                File.WriteAllText(Path.Combine(community, name, "sub", "layout.json"), name);
            }

            // Uniones: crear, leer, quitar sin tocar el contenido.
            {
                var target = Path.Combine(root, "dir");
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "a.txt"), "hola");
                var link = Path.Combine(root, "enlace");
                Junction.Create(link, target);
                Check(Junction.IsLink(link) && File.ReadAllText(Path.Combine(link, "a.txt")) == "hola"
                      && Junction.TargetOf(link)!.Equals(target, StringComparison.OrdinalIgnoreCase),
                      "unión: se crea, se lee a través de ella y dice a dónde apunta");
                Junction.Remove(link);
                Check(!Path.Exists(link) && File.Exists(Path.Combine(target, "a.txt")), "unión: quitarla no borra el contenido");
                var refused = false;
                try { Junction.Remove(target); } catch (InvalidOperationException) { refused = true; }
                Check(refused && File.Exists(Path.Combine(target, "a.txt")), "unión: Remove se niega con una carpeta de verdad");
            }

            var scan = mgr.Scan(library);
            Check(scan.Count == 4 && scan.All(e => e.State == ModState.Foreign), "sin biblioteca: todo lo de Community es ajeno");

            // Importar: se mueve a la biblioteca y queda un enlace (sigue activo).
            foreach (var name in new[] { "orbx-png", "gran-canaria", "navigraph" }) mgr.Import(library, name);
            scan = mgr.Scan(library);
            Check(scan.Single(e => e.Name == "orbx-png").State == ModState.Active
                  && File.ReadAllText(Path.Combine(lib, "orbx-png", "sub", "layout.json")) == "orbx-png"
                  && Junction.IsLink(Path.Combine(community, "orbx-png"))
                  && File.ReadAllText(Path.Combine(community, "orbx-png", "sub", "layout.json")) == "orbx-png",
                  "importar: el mod pasa a la biblioteca, sigue activo y se lee igual desde Community");
            Check(scan.Single(e => e.Name == "suelto").State == ModState.Foreign, "importar: lo no importado sigue siendo ajeno");

            // Desactivar / activar.
            mgr.Disable(library, "orbx-png");
            Check(!Path.Exists(Path.Combine(community, "orbx-png")) && File.Exists(Path.Combine(lib, "orbx-png", "sub", "layout.json"))
                  && mgr.Scan(library).Single(e => e.Name == "orbx-png").State == ModState.Inactive,
                  "desactivar: quita el enlace y el mod sigue en la biblioteca");
            mgr.Enable(library, "orbx-png");
            mgr.Enable(library, "orbx-png"); // dos veces = nada
            Check(mgr.Scan(library).Single(e => e.Name == "orbx-png").State == ModState.Active, "activar: vuelve el enlace (y repetirlo no falla)");
            var refusedForeign = false;
            try { mgr.Disable(library, "suelto"); } catch (InvalidOperationException) { refusedForeign = true; }
            Check(refusedForeign && Directory.Exists(Path.Combine(community, "suelto", "sub")), "desactivar se niega con una carpeta de verdad");

            // Conflicto: en la biblioteca y, con el mismo nombre, una carpeta de verdad en Community.
            Directory.CreateDirectory(Path.Combine(lib, "suelto"));
            Check(mgr.Scan(library).Single(e => e.Name == "suelto").State == ModState.Conflict, "conflicto: mismo nombre en biblioteca y carpeta de verdad");
            Directory.Delete(Path.Combine(lib, "suelto"));

            // Conjuntos: lo pedido + "always"; el resto de la biblioteca, fuera; lo ajeno no se toca.
            var file = new ModsFile { Libraries = [library] };
            var set = new ModSet { Name = "Canarias", Mods = { ["MSFS"] = ["gran-canaria", "no-existe"] } };
            file.Sets.Add(set);
            var dry = mgr.ApplySet(file, set, dryRun: true);
            Check(dry.Any(c => c is { Mod: "orbx-png", Action: "desactivar", Error: null }) && Junction.IsLink(Path.Combine(community, "orbx-png")),
                  "conjunto --dry: dice lo que haría sin hacerlo");
            var applied = mgr.ApplySet(file, set);
            scan = mgr.Scan(library);
            Check(scan.Single(e => e.Name == "gran-canaria").State == ModState.Active && scan.Single(e => e.Name == "navigraph").State == ModState.Active
                  && scan.Single(e => e.Name == "orbx-png").State == ModState.Inactive && scan.Single(e => e.Name == "suelto").State == ModState.Foreign,
                  "conjunto: activos los suyos y los «always», el resto desactivado, lo ajeno intacto");
            Check(applied.Any(c => c.Mod == "no-existe" && c.Error is not null), "conjunto: un mod que no está en la biblioteca se avisa");
            Check(applied.Any(c => c.Mod == "suelto" && c.Error!.Contains("impórtalo")) && Directory.Exists(Path.Combine(community, "suelto", "sub")),
                  "conjunto: lo suelto que debería quedar fuera se avisa (sigue activo, no se toca)");
            Check(mgr.Snapshot(file.Libraries)["MSFS"].SequenceEqual(["gran-canaria"]), "instantánea: los activos sin los «always»");

            // Enlace roto: el mod desaparece de la biblioteca.
            mgr.Enable(library, "orbx-png");
            Directory.Move(Path.Combine(lib, "orbx-png"), Path.Combine(root, "orbx-fuera"));
            Check(mgr.Scan(library).Single(e => e.Name == "orbx-png").State == ModState.Broken, "enlace roto: se detecta");
            mgr.ApplySet(file, set);
            Check(!Path.Exists(Path.Combine(community, "orbx-png")) && Directory.Exists(Path.Combine(root, "orbx-fuera", "sub")),
                  "enlace roto: al aplicar un conjunto se quita (solo el enlace)");

            // Simulador abierto: no se cambia nada.
            running = true;
            var locked = mgr.ApplySet(file, new ModSet { Name = "x", Mods = { ["MSFS"] = [] } });
            var refusedRunning = false;
            try { mgr.Enable(library, "gran-canaria"); } catch (InvalidOperationException) { refusedRunning = true; }
            Check(locked.Count == 1 && locked[0].Error!.Contains("abierto") && refusedRunning
                  && mgr.Scan(library).Single(e => e.Name == "gran-canaria").State == ModState.Active,
                  "con el simulador abierto no se cambia nada");
            running = false;

            // Archivo: ida y vuelta, por defecto sin archivo, y errores.
            var path = Path.Combine(root, "mods.json");
            Check(ModsFile.Load(path).Libraries.Count == 3, "sin mods.json: bibliotecas por defecto (MSFS 2024 y DCS)");
            file.Save(path);
            var again = ModsFile.Load(path);
            Check(again.Libraries[0].Always.SequenceEqual(["navigraph"]) && again.FindSet("canarias")!.Mods["MSFS"].SequenceEqual(["gran-canaria", "no-existe"]),
                  "mods.json: guardar y leer da lo mismo");
            foreach (var (bad, what) in new[]
                     {
                         ("""{ "libraries": [ { "name": "a", "library": "C:\\x", "target": "C:\\x\\y" } ] }""", "biblioteca dentro del destino"),
                         ("""{ "libraries": [ { "name": "a", "library": "C:\\x", "target": "C:\\y" }, { "name": "A", "library": "C:\\z", "target": "C:\\w" } ] }""", "biblioteca repetida"),
                         ("""{ "libraries": [], "sets": [ { "name": "s", "mods": { "nada": [] } } ] }""", "conjunto con biblioteca inexistente"),
                         ("{ roto", "JSON roto"),
                     })
            {
                File.WriteAllText(path, bad);
                var rejected = false;
                try { ModsFile.Load(path); } catch (Exception) { rejected = true; }
                Check(rejected, $"mods.json: {what} = error");
            }

            // El ejemplo del repo es válido y el hangar de ejemplo apunta a conjuntos que existen.
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "RigControl.slnx"))) d = d.Parent;
            var sample = ModsFile.Load(Path.Combine(d!.FullName, "profiles", "mods.json"));
            var hangars = HangarFile.Load(Path.Combine(d.FullName, "profiles", "hangar.json"));
            Check(sample.Libraries.Count >= 1 && hangars.Hangars.All(h => h.ModSet is null || sample.FindSet(h.ModSet) is not null),
                  "profiles/mods.json válido y los conjuntos del hangar de ejemplo existen");
        }
        finally
        {
            // Quitar primero los enlaces, por si acaso, y luego todo.
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Where(Junction.IsLink).ToList())
                Junction.Remove(dir);
            Directory.Delete(root, true);
        }

        return fails;
    }
}
