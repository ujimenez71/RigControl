using System.IO.Compression;
using System.Text.Json;
using RigControl.Core.Mods;
using RigControl.Core.Snapshots;

namespace RigControl.Tests;

/// <summary>Instantáneas en una carpeta temporal: crear, saltar si no cambió, podar, restaurar y protecciones.</summary>
internal static class SnapshotTests
{
    public static int Run()
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        var root = Directory.CreateTempSubdirectory("rigsnap").FullName;
        try
        {
            var game = Path.Combine(root, "Saved Games", "DCS");
            Write(game, @"Config\Options.lua", "options = 1");
            Write(game, @"Config\Input\AH-64D\joystick\moza.diff.lua", "local diff = {}");
            Write(game, @"Config\authdata.bin", "SECRETO");
            Write(game, @"Config\oculto.lua", "oculto");
            File.SetAttributes(Path.Combine(game, @"Config\oculto.lua"), FileAttributes.Hidden);
            Write(game, @"Scripts\Export.lua", "dofile(x)");
            Write(game, @"Scripts\DCS-BIOS\enorme.lua", "no se guarda");
            Write(game, @"Logs\dcs.log", "no se guarda");
            // Un mod enlazado dentro de Config: no se debe seguir.
            Write(root, @"biblioteca\mod\grande.bin", "mod");
            Junction.Create(Path.Combine(game, @"Config\enlace-mod"), Path.Combine(root, @"biblioteca\mod"));

            var running = false;
            var store = new SnapshotStore(Path.Combine(root, "store"), _ => running, keepAuto: 3);
            var group = new SnapshotGroup
            {
                Name = "DCS",
                Root = game,
                Include = ["Config", @"Scripts\Export.lua", @"Scripts\Hooks"],
                Exclude = ["authdata.bin"],
                Process = "DCS",
            };

            var files = SnapshotStore.Collect(group);
            Check(files.Keys.SequenceEqual([@"Config\Input\AH-64D\joystick\moza.diff.lua", @"Config\oculto.lua", @"Config\Options.lua", @"Scripts\Export.lua"], StringComparer.OrdinalIgnoreCase),
                  $"qué se guarda: Config + Export.lua, con ocultos, sin credenciales, sin lo no incluido ni uniones ({string.Join(", ", files.Keys)})");

            var manual = store.Create(group, "antes de reasignar", auto: false)!;
            Check(manual is { FileCount: 4, Auto: false, Name: "antes de reasignar" } && File.Exists(manual.Path)
                  && Path.GetFileName(manual.Path).Contains("antes de reasignar"), "manual: se crea con su nombre");
            Check(store.Create(group, "hangar", auto: true) is null, "automática: sin cambios no se crea");
            Check(store.Create(group, "otra", auto: false) is not null, "manual: se crea aunque no haya cambios");

            // Cambios: automáticas, y se conservan solo keepAuto (3); las manuales no se tocan.
            for (var i = 0; i < 5; i++)
            {
                Write(game, @"Config\Options.lua", $"options = {i + 2}");
                Check(store.Create(group, "hangar", auto: true) is not null || i < 0, $"automática {i + 1}: con cambios se crea");
            }
            var all = store.List("DCS");
            Check(all.Count(s => s.Auto) == 3 && all.Count(s => !s.Auto) == 2, $"poda: 3 automáticas y las 2 manuales ({all.Count(s => s.Auto)} / {all.Count(s => !s.Auto)})");
            Check(all.Zip(all.Skip(1)).All(p => p.First.Created >= p.Second.Created), "la lista va de la más reciente a la más antigua");

            // Comparar con lo actual.
            Write(game, @"Config\nuevo.lua", "nuevo");
            var diff = store.Compare(group, manual);
            Check(diff.Any(d => d.Rel == @"Config\Options.lua" && d.State == "cambiado") && diff.Any(d => d.Rel == @"Config\nuevo.lua" && d.State.StartsWith("nuevo"))
                  && !diff.Any(d => d.Rel.Contains("moza")), "comparar: cambiado y nuevo; lo igual no sale");

            // Restaurar: sobrescribe, no borra lo nuevo, y deja antes una automática.
            File.SetAttributes(Path.Combine(game, @"Config\Options.lua"), FileAttributes.ReadOnly);
            File.Delete(Path.Combine(game, @"Config\Input\AH-64D\joystick\moza.diff.lua"));
            var restored = store.Restore(group, manual);
            Check(Read(game, @"Config\Options.lua") == "options = 1" && Read(game, @"Config\Input\AH-64D\joystick\moza.diff.lua") == "local diff = {}",
                  "restaurar: vuelven los archivos (también uno de solo lectura y uno borrado)");
            Check(File.Exists(Path.Combine(game, @"Config\nuevo.lua")) && Read(game, @"Config\authdata.bin") == "SECRETO",
                  "restaurar: lo que no estaba en la instantánea no se toca");
            Check(restored.Files == 4 && restored.Before is { Auto: true } before && before.Name.StartsWith("antes de restaurar"),
                  "restaurar: antes se guarda el estado actual como automática");
            store.Restore(group, restored.Before!);
            // Vuelve Options.lua; moza.diff.lua (que antes faltaba) se queda: restaurar nunca borra.
            Check(Read(game, @"Config\Options.lua") == "options = 6" && File.Exists(Path.Combine(game, @"Config\Input\AH-64D\joystick\moza.diff.lua")),
                  "deshacer: restaurar la de «antes de restaurar» devuelve lo que había (sin borrar nada)");

            // Simulador abierto: no se restaura.
            running = true;
            var refused = false;
            try { store.Restore(group, manual); } catch (InvalidOperationException) { refused = true; }
            Check(refused && Read(game, @"Config\Options.lua") == "options = 6", "con el programa abierto no se restaura");
            Check(store.Create(group, "con DCS abierto", auto: false) is not null, "con el programa abierto sí se puede guardar");
            running = false;

            // Una instantánea manipulada con «..» no escribe fuera de root.
            var evil = Path.Combine(root, "store", "DCS", "malvada.zip");
            using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
            {
                using (var e = new StreamWriter(zip.CreateEntry("files/../../fuera.txt").Open())) e.Write("x");
                using var m = zip.CreateEntry("rigcontrol-snapshot.json").Open();
                JsonSerializer.Serialize(m, new { Group = "DCS", Name = "mala", Auto = false, Created = DateTime.Now, Hash = "x", Root = game, Files = new[] { "x" } });
            }
            var bad = store.List("DCS").First(s => s.Name == "mala");
            var blocked = false;
            try { store.Restore(group, bad); } catch (InvalidDataException) { blocked = true; }
            Check(blocked && !File.Exists(Path.Combine(root, "fuera.txt")) && !File.Exists(Path.Combine(game, "..", "fuera.txt")),
                  "una ruta con «..» en el .zip se rechaza");

            // Borrar y grupos vacíos.
            store.Delete(manual);
            Check(!File.Exists(manual.Path), "borrar una instantánea");
            var empty = new SnapshotGroup { Name = "MSFS", Root = Path.Combine(root, "no-existe"), Include = ["UserCfg.opt"] };
            var auto = store.AutoAll(new SnapshotsFile { Groups = [group, empty] }, "hangar");
            Check(auto.Count == 2 && auto.All(a => a.Error is null) && auto[1].Created is null && SnapshotStore.Describe(auto).Contains("sin cambios"),
                  $"AutoAll: grupo sin archivos se salta sin error ({SnapshotStore.Describe(auto)})");

            // Archivo de configuración.
            var path = Path.Combine(root, "snapshots.json");
            var def = SnapshotsFile.Load(path);
            Check(def.Groups.Select(g => g.Name).SequenceEqual(["DCS", "MSFS 2024", "RigControl"]) && def.KeepAuto == 10,
                  "sin snapshots.json: DCS, MSFS 2024 y RigControl, 10 automáticas");
            def.Save(path);
            Check(SnapshotsFile.Load(path).FindGroup("rigcontrol")!.Exclude.Contains(@"snapshots\*"), "snapshots.json: guardar y leer");
            foreach (var (json, what) in new[]
                     {
                         ("""{ "groups": [ { "name": "a", "root": "C:\\x", "include": ["..\\fuera"] } ] }""", "include con «..»"),
                         ("""{ "groups": [ { "name": "a", "root": "C:\\x", "include": ["C:\\abs"] } ] }""", "include absoluto"),
                         ("""{ "groups": [ { "name": "a/b", "root": "C:\\x", "include": ["y"] } ] }""", "nombre con barra"),
                         ("""{ "keepAuto": 0, "groups": [] }""", "keepAuto 0"),
                     })
            {
                File.WriteAllText(path, json);
                var rejected = false;
                try { SnapshotsFile.Load(path); } catch (Exception) { rejected = true; }
                Check(rejected, $"snapshots.json: {what} = error");
            }

            // El grupo RigControl por defecto no se mete en su propia carpeta de instantáneas.
            var rc = Path.Combine(root, "RigControl");
            Write(rc, "hangar.json", "{}");
            Write(rc, "hangar.json.bak", "{}");
            Write(rc, @"profiles\c172.json", "{}");
            Write(rc, @"snapshots\DCS\vieja.zip", "zip");
            var rcGroup = SnapshotsFile.CreateDefault().FindGroup("RigControl")!;
            rcGroup.Root = rc;
            Check(SnapshotStore.Collect(rcGroup).Keys.SequenceEqual(["hangar.json", @"profiles\c172.json"], StringComparer.OrdinalIgnoreCase),
                  "RigControl: sin la carpeta de instantáneas ni los .bak");

            // El ejemplo del repo es válido.
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "RigControl.slnx"))) d = d.Parent;
            Check(SnapshotsFile.Load(Path.Combine(d!.FullName, "profiles", "snapshots.json")).Groups.Count == 3, "profiles/snapshots.json válido");
        }
        finally
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Where(Junction.IsLink).ToList())
                Junction.Remove(dir);
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        return fails;
    }

    private static void Write(string root, string rel, string text)
    {
        var full = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (File.Exists(full)) File.SetAttributes(full, FileAttributes.Normal);
        File.WriteAllText(full, text);
    }

    private static string Read(string root, string rel) => File.ReadAllText(Path.Combine(root, rel));
}
