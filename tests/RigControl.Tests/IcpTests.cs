using System.Text.Json;
using RigControl.Core;
using RigControl.Core.Bindings;
using RigControl.Core.WinWing;

namespace RigControl.Tests;

/// <summary>Pantalla del ICP desde el perfil: plantillas, compilación y salida con una pantalla falsa.</summary>
internal static class IcpTests
{
    private sealed class FakeScreen : IIcpScreen
    {
        public readonly List<string[]> Shown = [];
        public bool Fail;
        public bool Disposed;

        public void Show(ReadOnlySpan<char> cells, ReadOnlySpan<bool> inverted)
        {
            if (Fail) throw new IOException("desconectado");
            var lines = new string[IcpFont.Rows];
            for (var r = 0; r < IcpFont.Rows; r++)
            {
                var sb = new System.Text.StringBuilder();
                for (var c = 0; c < IcpFont.Columns; c++)
                {
                    var i = r * IcpFont.Columns + c;
                    sb.Append(inverted[i] ? char.ToLowerInvariant(cells[i]) : cells[i]); // invertido = minúscula, para verlo
                }
                lines[r] = sb.ToString().TrimEnd();
            }
            Shown.Add(lines);
        }

        public void Dispose() => Disposed = true;
    }

    public static int Run()
    {
        Log.Sink = _ => { };
        var fails = 0;
        void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FALLO")} {what}"); if (!ok) fails++; }

        var vars = new List<SimVarSpec>();
        int Idx(SimVarSpec s) { var i = vars.IndexOf(s); if (i >= 0) return i; vars.Add(s); return vars.Count - 1; }
        IcpLine L(string t) { Check(IcpTemplate.TryParse(t, Idx, out var l, out var e), $"interpreta \"{t}\" {e}"); return l!; }

        var dcs = new Dictionary<string, (string? Text, double Number)>
        {
            ["UHF_RADIO"] = ("242.000", double.NaN),
            ["PLT_EUFD_LINE1"] = ("ABCDEFGHIJ0123456789KLMNOPQRSTUVWXYZ", double.NaN),
            ["PLT_MASTER_CAUTION_L"] = (null, 1),
        };
        bool ReadDcs(string name, out string? text, out double number)
        {
            var ok = dcs.TryGetValue(name, out var v);
            text = v.Text;
            number = ok ? v.Number : double.NaN;
            return ok;
        }

        string Draw(IcpLine line, double[] values, IcpContext.DcsReader? reader = null)
        {
            var cells = new char[IcpFont.Columns];
            var inv = new bool[IcpFont.Columns];
            IcpTemplate.Render(line, new IcpContext(values, reader, "Apache", "AH-64D_BLK_II", new DateTime(2026, 11, 13, 21, 5, 9)), cells, inv);
            return new string(cells.Select((c, i) => inv[i] ? char.ToLowerInvariant(c) : c).ToArray()).TrimEnd();
        }

        var uhf = L("UHF {D:uhf_radio}");
        var fuel = L("FUEL {A:FUEL TOTAL QUANTITY, gallons|0.0} GAL");
        var mc = L("[MC] {D:PLT_MASTER_CAUTION_L}");
        var cut = L("{D:PLT_EUFD_LINE1|11..20}");
        var info = L("{perfil} {hora} {avion|1..6}");
        var esc = L("[[X]] {{Y}}");
        Check(vars.Count == 1 && vars[0] == new SimVarSpec("A:FUEL TOTAL QUANTITY", "gallons"), "A: con unidades va a las variables vigiladas; D: no");

        var values = new[] { double.NaN };
        Check(Draw(uhf, values, ReadDcs) == "UHF 242.000", "texto de DCS-BIOS (frecuencia)");
        Check(Draw(uhf, values) == "UHF", "sin DCS: el valor en blanco");
        Check(Draw(fuel, values) == "FUEL  GAL", "sin dato de MSFS: en blanco");
        values[0] = 42.25;
        Check(Draw(fuel, values) == "FUEL 42.3 GAL", "formato \"0.0\"");
        Check(Draw(mc, values, ReadDcs) == "mc 1", "invertido entre corchetes + valor entero de DCS-BIOS");
        Check(Draw(cut, values, ReadDcs) == "0123456789", "recorte desde..hasta (base 1)");
        Check(Draw(info, values) == "Apache 21:05 AH-64D", "perfil, hora (HH:mm) y avión recortado");
        Check(Draw(esc, values) == "[X] {Y}", "\"[[\" y \"{{\" escriben el carácter");
        Check(Draw(L("{D:PLT_EUFD_LINE1}{D:PLT_EUFD_LINE1}"), values, ReadDcs).Length == IcpFont.Columns, "lo que no cabe se corta a 25");

        foreach (var bad in new[] { "[sin cerrar", "cierra]", "{D:X", "{Z:X}", "{A:X>3}", "{D:X-Y}", "{D:X|5..2}", "{perfil|0.0}", "x}" })
        {
            var ok = IcpTemplate.TryParse(bad, Idx, out _, out var e);
            Check(!ok && e is not null, $"rechaza \"{bad}\": {e}");
        }

        // Texto de DCS-BIOS: salida "string" del catálogo y lectura de la imagen (ceros del final fuera)
        var radio = new DcsControl("UHF_RADIO", "Radio", "UHF", [], null, 0, 0, 0, [], TextAddress: 4, TextLength: 7);
        var image = new byte[16];
        "251.0"u8.CopyTo(image.AsSpan(4));
        Check(radio.ReadText(image) == "251.0", "texto de la imagen hasta el primer 0");
        if (DcsBiosCatalog.JsonDir() is not null)
        {
            var real = DcsBiosCatalog.ForAircraft("AH-64D_BLK_II").FirstOrDefault(c => c.Name == "UHF_RADIO");
            Check(real is { TextAddress: not null, TextLength: 7 }, $"catálogo real del AH-64D: UHF_RADIO es texto de 7 ({real?.TextAddress}, {real?.TextLength})");
            var mcl = DcsBiosCatalog.ForAircraft("AH-64D_BLK_II").FirstOrDefault(c => c.Name == "PLT_MASTER_CAUTION_L");
            Check(mcl is { Address: not null, TextAddress: null }, "y PLT_MASTER_CAUTION_L sigue siendo numérico");
        }

        // Perfil: "icp" se compila, MSFS no admite D:, más de 5 líneas = error
        (CompiledProfile? Pr, List<string> E) Compile(string sim, params string[] lines)
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, $$"""{ "name": "t", "sim": "{{sim}}", "devices": {}, "bindings": [], "icp": {{JsonSerializer.Serialize(lines)}} }""");
            return ProfileCompiler.Compile(ProfileFile.Load(path));
        }
        var (p, errs) = Compile("dcs", "UHF {D:UHF_RADIO}", "[{perfil}]");
        Check(p is not null && errs.Count == 0 && p.Icp.Length == 2, $"perfil de DCS con 2 líneas de icp ({string.Join("; ", errs)})");
        var (_, e2) = Compile("msfs", "{D:UHF_RADIO}");
        Check(e2.Any(e => e.Contains("DCS-BIOS")), $"D: en perfil de MSFS: error ({string.Join("; ", e2)})");
        var (_, e3) = Compile("dcs", "{A:FUEL TOTAL QUANTITY}");
        Check(e3.Any(e => e.Contains("MSFS")), $"A: en perfil de DCS: error ({string.Join("; ", e3)})");
        var (_, e4) = Compile("dcs", "1", "2", "3", "4", "5", "6");
        Check(e4.Any(e => e.Contains("5 líneas")), $"6 líneas: error ({string.Join("; ", e4)})");

        // Guardar desde el editor conserva "icp"; sin "icp" no se escribe la clave
        var savePath = Path.Combine(Path.GetTempPath(), $"icp-{Guid.NewGuid():N}.json");
        new ProfileFile { Name = "t", Icp = ["UHF {D:UHF_RADIO}"] }.Save(savePath);
        Check(ProfileFile.Load(savePath).Icp is ["UHF {D:UHF_RADIO}"], "guardar y leer conserva \"icp\"");
        new ProfileFile { Name = "t" }.Save(savePath);
        Check(!File.ReadAllText(savePath).Contains("\"icp\""), "sin líneas no se escribe \"icp\"");
        File.Delete(savePath);
        File.Delete(savePath + ".bak");

        // Salida: escribe al cambiar, como mucho cada RenderMs, en blanco al quitar "icp" y al salir
        var screen = new FakeScreen();
        var opens = 0;
        var output = new IcpTextOutput(() => { opens++; return screen; });
        var (pa, _) = Compile("dcs", "UHF {D:UHF_RADIO}", "[MC]");
        var (pn, _) = Compile("dcs");
        output.Update(new IcpContext([], ReadDcs, "", "", default), 0);
        Check(screen.Shown.Count == 0 && opens == 0, "sin \"icp\" en el perfil no abre ni toca la pantalla");

        output.SetProfile(pa!);
        var ctx = new IcpContext([], ReadDcs, "Apache", "", default);
        output.Update(ctx, 1000);
        Check(screen.Shown.Count == 1 && screen.Shown[0][0] == "UHF 242.000" && screen.Shown[0][1] == "mc",
              $"primera escritura ({string.Join(" | ", screen.Shown.LastOrDefault() ?? [])})");
        Check(output.Text is ["UHF 242.000", "MC"], "Text = lo que hay en pantalla");
        var snap = output.Shown;
        Check(snap is not null && snap.Cells.Length == IcpFont.Columns * IcpFont.Rows && snap.Inverted[IcpFont.Columns] && !snap.Inverted[0]
              && new string(snap.Cells, IcpFont.Columns, 2) == "MC", "Shown = celdas y marca de invertido (para la pantalla Ejecución)");
        output.Update(ctx, 1050);
        output.Update(ctx, 1200);
        Check(screen.Shown.Count == 1, "sin cambios no se vuelve a escribir");
        dcs["UHF_RADIO"] = ("251.000", double.NaN);
        output.Update(ctx, 1250);
        Check(screen.Shown.Count == 1, "antes de RenderMs no se dibuja");
        output.Update(ctx, 1300);
        Check(screen.Shown.Count == 2 && screen.Shown[1][0] == "UHF 251.000", "al cambiar la frecuencia se reescribe");
        Check(!ReferenceEquals(snap, output.Shown), "Shown es un objeto nuevo al cambiar la pantalla");
        snap = output.Shown;
        output.Update(ctx, 1400 - 1); // sin cambios (y antes de RenderMs)
        Check(ReferenceEquals(snap, output.Shown), "sin cambios, el mismo Shown");

        screen.Fail = true;
        dcs["UHF_RADIO"] = ("225.000", double.NaN);
        output.Update(ctx, 1400);
        Check(screen.Disposed && opens == 1, "error al escribir: se suelta la pantalla");
        screen.Fail = false; screen.Disposed = false;
        output.Update(ctx, 3000);
        Check(opens == 1, "no se reabre antes de 5 s");
        output.Update(ctx, 6500);
        Check(opens == 2 && screen.Shown[^1][0] == "UHF 225.000", "se reabre a los 5 s y escribe lo actual");

        output.SetProfile(pn!);
        output.Update(new IcpContext([], null, "", "", default), 6700);
        Check(screen.Shown[^1].All(l => l.Length == 0) && !output.Active, "perfil sin \"icp\": la deja en blanco una vez");
        var count = screen.Shown.Count;
        output.Update(new IcpContext([], null, "", "", default), 7000);
        Check(screen.Shown.Count == count, "y ya no la vuelve a tocar");

        output.SetProfile(pa!);
        output.Update(ctx, 8000);
        count = screen.Shown.Count;
        output.Dispose();
        Check(screen.Shown.Count == count + 1 && screen.Shown[^1].All(l => l.Length == 0) && screen.Disposed, "al salir, en blanco y cerrada");

        return fails;
    }
}
