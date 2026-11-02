using System.Text.Json;
using RigControl.Core.Bindings;

namespace RigControl.Tests;

/// <summary>Guardar un perfil desde la app: lo que se lee después es lo mismo que se guardó.</summary>
internal static class SaveTests
{
    public static int Run()
    {
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        var repo = FindRepo();
        foreach (var name in new[] { "c172.json", "ejemplo-ga.json" })
        {
            var original = ProfileFile.Load(Path.Combine(repo, "profiles", name));
            var dir = Directory.CreateTempSubdirectory().FullName;
            var path = Path.Combine(dir, name);
            original.Save(path);
            var again = ProfileFile.Load(path);

            // Comparación por contenido: mismo JSON normalizado.
            string Norm(ProfileFile f) => JsonSerializer.Serialize(f);
            Check(Norm(original) == Norm(again), $"{name}: guardar y volver a leer da lo mismo");
            var (p1, _) = ProfileCompiler.Compile(original);
            var (p2, e2) = ProfileCompiler.Compile(again);
            Check(p2 is not null && p1!.Bindings.Length == p2.Bindings.Length && p1.Lights.Length == p2.Lights.Length,
                  $"{name}: compila igual ({p2?.Bindings.Length} bindings, {p2?.Lights.Length} luces) {string.Join(" | ", e2)}");

            var text = File.ReadAllText(path);
            Check(text.Split('\n').Count(l => l.TrimStart().StartsWith("{\"input\"") || l.TrimStart().StartsWith("{\"sequence\"")) == p2!.Bindings.Length,
                  $"{name}: un binding por línea");
            original.Save(path);
            Check(File.Exists(path + ".bak") && !File.Exists(path + ".tmp"), $"{name}: deja copia .bak y no deja el temporal");
            if (name == "c172.json") Console.WriteLine("---- así queda c172.json (inicio):\n" + string.Join("\n", text.Split('\n').Take(14)));
        }
        return fails;
    }

    private static string FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "RigControl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("No encuentro la raíz del repo (RigControl.slnx).");
    }
}
