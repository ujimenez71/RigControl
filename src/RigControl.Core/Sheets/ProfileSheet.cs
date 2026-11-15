using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using RigControl.Core.Bindings;

namespace RigControl.Core.Sheets;

/// <summary>Una entrada de un mando en la hoja: número del marcador, nombre y lo que hace.</summary>
public sealed record SheetInput(int Number, string Part, string Label, MarkerPosition? Position, List<SheetRow> Rows);

/// <summary>Una fila: gesto, condición y qué hace.</summary>
/// <param name="Release">Si al soltar hace algo (sin condición), va en la misma fila: "al soltar: Off".</param>
public sealed record SheetRow(string Gesture, string Condition, string Description, string Action, string? Release = null);

/// <summary>Un mando del perfil con sus entradas usadas y su foto (si la hay).</summary>
public sealed record SheetDevice(string Alias, string Key, string Name, string? ImagePath, List<SheetInput> Inputs);

/// <summary>
/// Hoja de mandos de un perfil: cada mando con su foto (fotos\VVVV_PPPP.json, ver DeviceLayout) y un número
/// sobre cada entrada usada, más la tabla de lo que hace. HTML autocontenido (la foto va dentro) que se
/// puede imprimir; el PDF lo hace Edge sin ventana (--headless --print-to-pdf).
/// </summary>
public static class ProfileSheet
{
    /// <summary>Carpeta de las hojas generadas.</summary>
    public static string Dir { get; } = Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "hojas");

    /// <summary>Mandos y entradas del perfil, listos para pintar. Lanza InvalidDataException si el perfil tiene errores.</summary>
    public static (ProfileReport Report, List<SheetDevice> Devices) Collect(ProfileFile file, string path, DeviceCatalog catalog)
    {
        var report = ProfileInspector.Inspect(file, path, catalog);
        if (report.Errors.Count > 0)
            throw new InvalidDataException("El perfil tiene errores; corrígelos antes de hacer la hoja:\n" + string.Join("\n", report.Errors));

        // alias -> (parte -> filas), en el orden del JSON.
        var byAlias = new Dictionary<string, Dictionary<string, List<SheetRow>>>(StringComparer.OrdinalIgnoreCase);
        void Add(string input, SheetRow row)
        {
            var colon = input.IndexOf(':');
            if (colon <= 0) return;
            var alias = input[..colon].Trim();
            var part = input[(colon + 1)..].Trim().ToLowerInvariant();
            if (!byAlias.TryGetValue(alias, out var parts)) byAlias[alias] = parts = new(StringComparer.OrdinalIgnoreCase);
            if (!parts.TryGetValue(part, out var rows)) parts[part] = rows = [];
            rows.Add(row);
        }

        (string Part, List<SheetRow> Rows)? LastRow(string input)
        {
            var colon = input.IndexOf(':');
            if (colon <= 0 || !byAlias.TryGetValue(input[..colon].Trim(), out var parts)) return null;
            return parts.TryGetValue(input[(colon + 1)..].Trim(), out var rows) && rows.Count > 0 ? (input, rows) : null;
        }
        var pressName = ProfileInspector.GestureName(Gesture.Press);

        foreach (var b in report.Bindings)
        {
            var f = file.Bindings[b.Number - 1];
            var row = new SheetRow(b.GestureText, b.Condition, b.Description, b.Action);
            // "al soltar" detrás de un "al pulsar" de la misma entrada y condición: misma fila (interruptores con muelle).
            if (f.Input is not null && b.Gesture == "release" && LastRow(f.Input) is { } prev && prev.Rows[^1] is { Gesture: var g } p
                && g == pressName && p.Condition == b.Condition && p.Release is null)
            {
                prev.Rows[^1] = p with { Release = b.Description };
                continue;
            }
            if (f.Sequence is { Count: > 0 } seq)
                for (var i = 0; i < seq.Count; i++)
                    Add(seq[i], row with { Gesture = $"secuencia, paso {i + 1} de {seq.Count}" });
            else if (f.Input is not null)
                Add(f.Input, row);
        }

        // Entradas que solo se usan como modificador (en condiciones o capas): también se marcan.
        var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in file.Bindings) CollectInputs(f.When, modifiers);
        foreach (var l in file.Layers.Values) CollectInputs(l, modifiers);
        foreach (var m in modifiers)
        {
            var colon = m.IndexOf(':');
            if (colon <= 0) continue;
            var alias = m[..colon];
            var part = m[(colon + 1)..].ToLowerInvariant();
            if (byAlias.TryGetValue(alias, out var parts) && parts.ContainsKey(part)) continue;
            var layers = file.Layers.Where(l => Mentions(l.Value, m)).Select(l => l.Key).ToList();
            Add(m, new SheetRow("mantener", "", layers.Count > 0 ? $"Modificador (capa {string.Join(", ", layers)})" : "Modificador", ""));
        }

        var devices = new List<SheetDevice>();
        foreach (var (alias, key) in file.Devices)
        {
            if (!byAlias.TryGetValue(alias, out var parts)) continue;
            var k0 = key.Trim();
            var entry = catalog.Devices.FirstOrDefault(d => d.Key.Equals(k0, StringComparison.OrdinalIgnoreCase)
                                                            || d.Key.StartsWith(k0 + ":", StringComparison.OrdinalIgnoreCase));
            var layout = DeviceLayout.Load(key);
            var number = 0;
            var inputs = parts.OrderBy(p => SortKey(p.Key), StringComparer.Ordinal)
                .Select(p => new SheetInput(++number, p.Key, LabelFor(entry, p.Key), layout.Find(p.Key), p.Value))
                .ToList();
            devices.Add(new SheetDevice(alias, key, entry is null ? alias : $"{entry.Alias} ({entry.ProductName})",
                                        layout.ImagePath, inputs));
        }
        return (report, devices);
    }

    private static string LabelFor(CatalogEntry? entry, string part)
    {
        var name = entry?.InputLabel(part);
        return name is null ? part : $"{name} ({part})";
    }

    /// <summary>Orden natural: botones por número, luego hats, luego ejes.</summary>
    private static string SortKey(string part)
    {
        var group = DeviceLayout.GroupOf(part);
        var kind = group.StartsWith('b') ? "0" : group.StartsWith("hat", StringComparison.Ordinal) ? "1" : "2";
        var digits = new string(group.Where(char.IsDigit).ToArray());
        var n = int.TryParse(digits, out var v) ? v : 0;
        var dir = part.Length > group.Length ? part[group.Length..] : "";
        dir = dir switch { ".up" => "0", ".right" => "1", ".down" => "2", ".left" => "3", _ => dir };
        return $"{kind}{group.TrimEnd("0123456789".ToCharArray())}{n:D4}{dir}";
    }

    private static void CollectInputs(ConditionFile? c, HashSet<string> into)
    {
        if (c is null) return;
        if (c.Input is { Length: > 0 } i) into.Add(i.Trim());
        foreach (var x in c.All ?? []) CollectInputs(x, into);
        foreach (var x in c.Any ?? []) CollectInputs(x, into);
        CollectInputs(c.Not, into);
    }

    private static bool Mentions(ConditionFile c, string input)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectInputs(c, set);
        return set.Contains(input);
    }

    // ------------------------------------------------------------------ HTML

    public static string BuildHtml(ProfileFile file, string path, DeviceCatalog catalog)
    {
        var (report, devices) = Collect(file, path, catalog);
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        static string Pct(double v) => (Math.Clamp(v, 0, 1) * 100).ToString("0.###", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>{E(report.Name)} · hoja de mandos</title>");
        sb.AppendLine("<style>").AppendLine(Css).AppendLine("</style></head><body>");

        sb.AppendLine("<header>");
        sb.AppendLine($"<h1>{E(report.Name)}</h1>");
        sb.Append("<p class=\"meta\">").Append(E(report.SimName));
        if (report.Aircraft.Length > 0) sb.Append(" · ").Append(E(string.Join(", ", report.Aircraft)));
        sb.Append(" · ").Append(E(Path.GetFileName(path)));
        sb.Append(" · ").Append(DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)).AppendLine("</p>");
        if (report.Notes.Length > 0) sb.AppendLine($"<p class=\"notes\">{E(report.Notes)}</p>");
        sb.AppendLine("</header>");

        if (devices.Count == 0) sb.AppendLine("<p>El perfil no tiene bindings.</p>");

        foreach (var d in devices)
        {
            sb.AppendLine("<section class=\"device\">");
            sb.AppendLine($"<h2>{E(d.Name)} <span class=\"alias\">{E(d.Alias)}</span></h2>");
            sb.AppendLine(d.ImagePath is null ? "<div class=\"layout single\">" : "<div class=\"layout\">");
            if (d.ImagePath is not null)
            {
                sb.AppendLine("<div class=\"photo\">");
                sb.AppendLine($"<img alt=\"{E(d.Name)}\" src=\"data:{Mime(d.ImagePath)};base64,{Convert.ToBase64String(File.ReadAllBytes(d.ImagePath))}\">");
                foreach (var i in d.Inputs.Where(i => i.Position is not null))
                {
                    var tip = string.Join(" · ", i.Rows.Select(r => r.Description));
                    sb.AppendLine($"<span class=\"marker\" style=\"left:{Pct(i.Position!.X)}%;top:{Pct(i.Position.Y)}%\" title=\"{E(i.Label)}: {E(tip)}\">{i.Number}</span>");
                }
                sb.AppendLine("</div>");
            }
            else
                sb.AppendLine("<p class=\"nophoto\">Sin foto de este mando (RigControl → Perfiles → Fotos de los mandos).</p>");

            sb.AppendLine("<table><thead><tr><th>#</th><th>Entrada</th><th>Gesto</th><th>Qué hace</th></tr></thead><tbody>");
            foreach (var i in d.Inputs)
            {
                for (var r = 0; r < i.Rows.Count; r++)
                {
                    var row = i.Rows[r];
                    sb.Append("<tr").Append(r == 0 ? " class=\"first\"" : "").Append('>');
                    if (r == 0)
                    {
                        var cls = d.ImagePath is not null && i.Position is null ? "num unplaced" : "num";
                        sb.Append($"<td rowspan=\"{i.Rows.Count}\" class=\"{cls}\"><span>{i.Number}</span></td>");
                        sb.Append($"<td rowspan=\"{i.Rows.Count}\" class=\"input\">{E(i.Label)}</td>");
                    }
                    sb.Append($"<td class=\"gesture\">{E(row.Gesture)}</td><td>{E(row.Description)}");
                    if (row.Release is not null) sb.Append($"<div class=\"release\">al soltar: {E(row.Release)}</div>");
                    if (row.Condition.Length > 0) sb.Append($"<div class=\"cond\">si {E(row.Condition)}</div>");
                    if (row.Action.Length > 0) sb.Append($"<div class=\"action\">{E(row.Action)}</div>");
                    sb.AppendLine("</td></tr>");
                }
            }
            sb.AppendLine("</tbody></table></div></section>");
        }

        if (report.Lights.Count > 0 || file.Icp.Count > 0)
        {
            sb.AppendLine("<section class=\"extra\">");
            if (report.Lights.Count > 0)
            {
                sb.AppendLine("<h2>Luces</h2><table><thead><tr><th>Luz</th><th>Se enciende si</th></tr></thead><tbody>");
                foreach (var l in report.Lights) sb.AppendLine($"<tr><td class=\"input\">{E(l.Light)}</td><td>{E(l.Rule)}</td></tr>");
                sb.AppendLine("</tbody></table>");
            }
            if (file.Icp.Count > 0)
            {
                sb.AppendLine("<h2>Pantalla del ICP</h2><pre class=\"icp\">");
                foreach (var line in file.Icp) sb.AppendLine(E(line));
                sb.AppendLine("</pre>");
            }
            sb.AppendLine("</section>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };

    private const string Css = """
:root { color-scheme: light; --accent: #d9480f; --line: #d0d4da; --muted: #5b6270; }
* { box-sizing: border-box; }
body { margin: 0 auto; max-width: 1200px; padding: 16px; font: 14px/1.4 "Segoe UI", system-ui, sans-serif; color: #1b1f24; background: #fff; }
h1 { margin: 0 0 4px; font-size: 26px; }
h2 { margin: 24px 0 8px; font-size: 19px; border-bottom: 2px solid var(--accent); padding-bottom: 4px; }
.alias { font: 13px Consolas, monospace; color: var(--muted); font-weight: normal; }
.meta { margin: 0; color: var(--muted); }
.notes { white-space: pre-wrap; background: #f4f5f7; padding: 8px 12px; border-radius: 6px; }
.layout { display: grid; grid-template-columns: minmax(0, 1.1fr) minmax(0, 1fr); gap: 16px; align-items: start; }
.layout.single { grid-template-columns: 1fr; }
.photo { position: relative; }
.release { color: var(--muted); font-size: 12px; }
.photo img { display: block; width: 100%; height: auto; border-radius: 6px; }
.marker { position: absolute; transform: translate(-50%, -50%); min-width: 24px; height: 24px; padding: 0 5px;
  border-radius: 12px; background: var(--accent); color: #fff; font: bold 13px/24px "Segoe UI", sans-serif;
  text-align: center; border: 2px solid #fff; box-shadow: 0 1px 3px rgba(0,0,0,.5); cursor: default; }
.nophoto { color: var(--muted); font-style: italic; margin: 0; }
table { width: 100%; border-collapse: collapse; font-size: 13px; }
th { text-align: left; color: var(--muted); font-weight: 600; border-bottom: 1px solid var(--line); padding: 4px 6px; }
td { padding: 4px 6px; vertical-align: top; }
tr.first td { border-top: 1px solid var(--line); }
td.num span { display: inline-block; min-width: 22px; height: 22px; border-radius: 11px; background: var(--accent);
  color: #fff; text-align: center; font-weight: bold; font-size: 12px; line-height: 22px; padding: 0 4px; }
td.num.unplaced span { background: #8a919c; }
td.input { font-weight: 600; }
td.gesture { color: var(--muted); white-space: nowrap; }
.cond { color: #1864ab; font-size: 12px; }
.action { color: var(--muted); font: 11px Consolas, monospace; }
pre.icp { background: #000; color: #4cff6a; padding: 10px 14px; border-radius: 6px; display: inline-block; font: 15px Consolas, monospace; }
@media screen and (min-width: 801px) { .photo { position: sticky; top: 8px; } }
@media (max-width: 800px) { .layout { grid-template-columns: 1fr; } }
@page { size: A4 landscape; margin: 10mm; }
@media print {
  body { max-width: none; padding: 0; }
  * { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
  .device + .device, .extra { break-before: page; }
  h2 { margin-top: 0; }
  tr { break-inside: avoid; }
  .photo img { max-height: 170mm; object-fit: contain; }
}
""";

    // ------------------------------------------------------------------ archivos

    /// <summary>Escribe la hoja en hojas\&lt;perfil&gt;.html y devuelve la ruta.</summary>
    public static string WriteHtml(string profilePath, DeviceCatalog? catalog = null)
    {
        var file = ProfileFile.Load(profilePath);
        var html = BuildHtml(file, profilePath, catalog ?? DeviceCatalog.Load());
        Directory.CreateDirectory(Dir);
        var output = Path.Combine(Dir, Path.GetFileNameWithoutExtension(profilePath) + ".html");
        File.WriteAllText(output, html, new UTF8Encoding(false));
        return output;
    }

    /// <summary>Edge (viene con Windows 11), para imprimir a PDF sin ventana; null si no está.</summary>
    public static string? EdgePath()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var p = Path.Combine(Environment.GetFolderPath(root), "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Hace la hoja en HTML y la pasa a PDF con Edge sin ventana. Devuelve la ruta del PDF.</summary>
    public static string WritePdf(string profilePath, DeviceCatalog? catalog = null, CancellationToken cancel = default)
    {
        var html = WriteHtml(profilePath, catalog);
        var edge = EdgePath() ?? throw new InvalidOperationException("No se encuentra Microsoft Edge para hacer el PDF: abre el HTML e imprímelo a PDF.");
        var pdf = Path.ChangeExtension(html, ".pdf");
        if (File.Exists(pdf)) File.Delete(pdf);
        // Carpeta de datos propia: si no, Edge abierto atiende la orden en su ventana y no hace el PDF.
        var userData = Path.Combine(Path.GetTempPath(), "rigcontrol-edge-pdf");
        var psi = new ProcessStartInfo(edge)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "--headless", "--disable-gpu", "--no-first-run", "--no-pdf-header-footer",
                                  $"--user-data-dir={userData}", $"--print-to-pdf={pdf}", new Uri(html).AbsoluteUri })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo arrancar Edge.");
        p.StandardOutput.ReadToEndAsync(cancel);
        p.StandardError.ReadToEndAsync(cancel);
        var clock = Stopwatch.StartNew();
        while (!p.WaitForExit(200))
        {
            if (cancel.IsCancellationRequested || clock.ElapsedMilliseconds > 60_000)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                cancel.ThrowIfCancellationRequested();
                throw new TimeoutException("Edge no terminó el PDF en 60 s.");
            }
        }
        if (!File.Exists(pdf)) throw new InvalidOperationException($"Edge no generó el PDF (código {p.ExitCode}).");
        return pdf;
    }
}
