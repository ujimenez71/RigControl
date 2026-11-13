using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RigControl.Core.Bindings;

public enum IcpFieldKind : byte { Text, Var, Dcs, Profile, Aircraft, Time }

/// <summary>Un trozo de una línea del ICP: texto fijo o un valor que se rellena al dibujar.</summary>
/// <param name="Text">Texto fijo (Kind = Text) o nombre del control (Dcs, sin "D:").</param>
/// <param name="Var">Índice en CompiledProfile.WatchedVars (Kind = Var).</param>
/// <param name="Format">Formato numérico ("0.0") u horario ("HH:mm"); null = el de por defecto.</param>
/// <param name="From">Recorte del texto, caracteres desde..hasta (base 1, ambos incluidos); 0 = sin recorte.</param>
public sealed record IcpField(IcpFieldKind Kind, string Text, bool Inverted, int Var = -1, string? Format = null,
                              int From = 0, int To = 0);

/// <summary>Una línea de la pantalla del ICP, ya interpretada.</summary>
public sealed record IcpLine(string Source, IcpField[] Fields);

/// <summary>Valores que necesita una línea para dibujarse.</summary>
/// <param name="Dcs">Control de DCS-BIOS por nombre: true si hay dato; texto (salida de texto) o número.</param>
public readonly record struct IcpContext(double[] Values, IcpContext.DcsReader? Dcs, string Profile, string Aircraft,
                                         DateTime Now)
{
    public delegate bool DcsReader(string name, out string? text, out double number);
}

/// <summary>
/// Líneas de "icp" del perfil (25 caracteres x 5 líneas; lo que sobra se corta):
///   texto tal cual, con la fuente de la DED (minúsculas = mayúsculas);
///   [texto] = invertido (fondo encendido), como los campos seleccionados de la DED;
///   {D:CONTROL}            DCS-BIOS: texto (frecuencias, pantallas) o valor entero del control;
///   {A:VAR, unidades} {L:Var}   MSFS, como en las luces;
///   {perfil} {avion} {hora};
///   {X|formato}: número ("0.0", "000"), hora ("HH:mm:ss") o recorte "desde..hasta" (base 1: "{D:PLT_EUFD_LINE1|1..25}").
///   "[[", "]]", "{{", "}}" = el carácter tal cual. Sin dato, el valor sale en blanco.
/// </summary>
public static partial class IcpTemplate
{
    public const int MaxLines = 5;

    [GeneratedRegex(@"^\s*(\d+)\s*\.\.\s*(\d+)\s*$")]
    private static partial Regex RangeRegex();

    public static bool TryParse(string line, Func<SimVarSpec, int> varIndex, out IcpLine? result, out string? error)
    {
        result = null;
        error = null;
        var fields = new List<IcpField>();
        var text = new StringBuilder();
        var inverted = false;

        void FlushText()
        {
            if (text.Length > 0) fields.Add(new IcpField(IcpFieldKind.Text, text.ToString(), inverted));
            text.Clear();
        }

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            var doubled = i + 1 < line.Length && line[i + 1] == c;
            if (c is '[' or ']' or '{' or '}' && doubled) { text.Append(c); i++; continue; }
            switch (c)
            {
                case '[':
                    if (inverted) { error = $"\"{line}\": corchetes dentro de corchetes (\"[[\" escribe un \"[\")."; return false; }
                    FlushText(); inverted = true;
                    break;
                case ']':
                    if (!inverted) { error = $"\"{line}\": \"]\" sin \"[\" (\"]]\" escribe un \"]\")."; return false; }
                    FlushText(); inverted = false;
                    break;
                case '{':
                    var end = line.IndexOf('}', i + 1);
                    if (end < 0) { error = $"\"{line}\": falta la \"}}\" de cierre (\"{{{{\" escribe una \"{{\")."; return false; }
                    FlushText();
                    if (!TryParseField(line[(i + 1)..end], inverted, varIndex, out var field, out var fe))
                    {
                        error = $"\"{line}\": {fe}";
                        return false;
                    }
                    fields.Add(field!);
                    i = end;
                    break;
                case '}':
                    error = $"\"{line}\": \"}}\" sin \"{{\" (\"}}}}\" escribe una \"}}\").";
                    return false;
                default:
                    text.Append(c);
                    break;
            }
        }
        if (inverted) { error = $"\"{line}\": falta la \"]\" de cierre."; return false; }
        FlushText();
        result = new IcpLine(line, [.. fields]);
        return true;
    }

    private static bool TryParseField(string body, bool inverted, Func<SimVarSpec, int> varIndex, out IcpField? field, out string? error)
    {
        field = null;
        error = null;
        var bar = body.IndexOf('|');
        var name = (bar < 0 ? body : body[..bar]).Trim();
        var format = bar < 0 ? null : body[(bar + 1)..];
        int from = 0, to = 0;
        if (format is not null && RangeRegex().Match(format) is { Success: true } m)
        {
            from = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            to = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (from < 1 || to < from) { error = $"{{{body}}}: el recorte va de 1 en adelante y \"desde\" no puede pasar de \"hasta\"."; return false; }
            format = null;
        }
        else if (format is not null && format.Trim().Length == 0)
        {
            error = $"{{{body}}}: formato vacío tras \"|\".";
            return false;
        }

        var lower = name.ToLowerInvariant();
        if (lower is "perfil" or "avion" or "avión")
        {
            if (format is not null) { error = $"{{{body}}}: el texto solo admite recorte \"desde..hasta\"."; return false; }
            field = new IcpField(lower == "perfil" ? IcpFieldKind.Profile : IcpFieldKind.Aircraft, "", inverted, From: from, To: to);
            return true;
        }
        if (lower == "hora")
        {
            format ??= "HH:mm";
            try { _ = DateTime.Now.ToString(format, CultureInfo.InvariantCulture); }
            catch (FormatException) { error = $"{{{body}}}: formato de hora no válido (p. ej. \"HH:mm:ss\")."; return false; }
            field = new IcpField(IcpFieldKind.Time, "", inverted, Format: format, From: from, To: to);
            return true;
        }

        if (format is not null)
        {
            try { _ = 1234.5.ToString(format, CultureInfo.InvariantCulture); }
            catch (FormatException) { error = $"{{{body}}}: formato numérico no válido (p. ej. \"0.0\" o \"000\")."; return false; }
        }

        if (name.Length > 2 && name[1] == ':' && char.ToUpperInvariant(name[0]) == 'D')
        {
            var control = name[2..].Trim().ToUpperInvariant();
            if (control.Length == 0 || control.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '_')))
            {
                error = $"{{{body}}}: nombre de control de DCS-BIOS no válido (letras, números y _).";
                return false;
            }
            field = new IcpField(IcpFieldKind.Dcs, control, inverted, Format: format, From: from, To: to);
            return true;
        }
        if (name.Length > 2 && name[1] == ':' && char.ToUpperInvariant(name[0]) is 'A' or 'L')
        {
            if (name.IndexOfAny(['<', '>', '=', '!']) >= 0)
            {
                error = $"{{{body}}}: entre llaves va solo la variable (sin operadores).";
                return false;
            }
            if (!LightParser.TryParse(name, varIndex, out var c, out var le)) { error = le; return false; }
            field = new IcpField(IcpFieldKind.Var, "", inverted, Var: c.Var, Format: format, From: from, To: to);
            return true;
        }
        error = $"{{{body}}}: se espera D:CONTROL, A:VARIABLE[, unidades], L:Variable, perfil, avion u hora.";
        return false;
    }

    /// <summary>Dibuja una línea en las celdas de una fila (Columns caracteres; lo que sobra se corta, el resto queda en blanco).</summary>
    public static void Render(IcpLine line, in IcpContext ctx, Span<char> cells, Span<bool> inverted)
    {
        cells.Fill(' ');
        inverted.Clear();
        var col = 0;
        foreach (var f in line.Fields)
        {
            if (col >= cells.Length) break;
            var s = Value(f, ctx);
            if (f.From > 0)
                s = f.From > s.Length ? "" : s.Substring(f.From - 1, Math.Min(s.Length, f.To) - f.From + 1);
            for (var i = 0; i < s.Length && col < cells.Length; i++, col++)
            {
                cells[col] = s[i];
                inverted[col] = f.Inverted;
            }
        }
    }

    private static string Value(IcpField f, in IcpContext ctx)
    {
        switch (f.Kind)
        {
            case IcpFieldKind.Text: return f.Text;
            case IcpFieldKind.Profile: return ctx.Profile;
            case IcpFieldKind.Aircraft: return ctx.Aircraft;
            case IcpFieldKind.Time: return ctx.Now.ToString(f.Format, CultureInfo.InvariantCulture);
            case IcpFieldKind.Var:
                return (uint)f.Var < (uint)ctx.Values.Length ? Number(ctx.Values[f.Var], f.Format) : "";
            case IcpFieldKind.Dcs:
                if (ctx.Dcs is null || !ctx.Dcs(f.Text, out var text, out var number)) return "";
                return text ?? Number(number, f.Format);
            default: return "";
        }
    }

    private static string Number(double v, string? format) =>
        double.IsNaN(v) ? ""
        : v.ToString(format ?? (Math.Abs(v - Math.Round(v)) < 1e-9 ? "0" : "0.##"), CultureInfo.InvariantCulture);
}
