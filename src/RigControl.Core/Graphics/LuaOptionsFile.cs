using System.Globalization;

namespace RigControl.Core.Graphics;

/// <summary>
/// options.lua de DCS ("options = { ["graphics"] = { ["AA"] = "DLAA", ... }, ... }") leído sin perder nada:
/// de cada valor simple se guarda dónde está en el texto y los cambios se escriben en su sitio, sin tocar
/// el resto (orden, tabulaciones, otras secciones). Claves con punto: "graphics.AA", "VR.pixel_density".
/// Los valores van como texto Lua tal cual: "\"DLAA\"" (con comillas), "0.66", "true".
/// </summary>
public sealed class LuaOptionsFile
{
    private readonly record struct Span(int Start, int Length);

    private readonly Dictionary<string, Span> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Close, int Depth)> _tables = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public string Text { get; private set; }

    public LuaOptionsFile(string text)
    {
        Text = text;
        Parse();
    }

    public static LuaOptionsFile Load(string path) => new(File.ReadAllText(path));

    /// <summary>Claves de valores simples, en el orden del archivo.</summary>
    public IReadOnlyList<string> Keys => _order;

    public bool Contains(string key) => _values.ContainsKey(key);

    /// <summary>Texto Lua del valor o null si no está.</summary>
    public string? Get(string key) => _values.TryGetValue(key, out var s) ? Text.Substring(s.Start, s.Length) : null;

    /// <summary>
    /// Cambia (o añade al final de su tabla, que debe existir) un valor. Texto Lua: "\"DLAA\"", "0.5", "false".
    /// Lanza InvalidDataException si el valor no es un literal simple o la tabla no existe.
    /// </summary>
    public void Set(string key, string luaValue)
    {
        luaValue = luaValue.Trim();
        if (!IsSimpleLiteral(luaValue)) throw new InvalidDataException($"{key}: \"{luaValue}\" no es un valor Lua simple (número, texto entre comillas, true o false).");
        if (_values.TryGetValue(key, out var span))
        {
            Text = Text[..span.Start] + luaValue + Text[(span.Start + span.Length)..];
        }
        else
        {
            var dot = key.LastIndexOf('.');
            var table = dot > 0 ? key[..dot] : "";
            if (!_tables.TryGetValue(table, out var t)) throw new InvalidDataException($"{key}: no existe la tabla \"{table}\" en options.lua.");
            var name = dot > 0 ? key[(dot + 1)..] : key;
            // Antes de la "}" de la tabla, con la sangría de sus otras líneas.
            var lineStart = Text.LastIndexOf('\n', t.Close - 1) + 1;
            var indent = new string('\t', t.Depth + 1);
            var nl = Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var entry = $"{indent}[\"{name}\"] = {luaValue},{nl}";
            // Si la "}" va sola en su línea, se mete delante de esa línea; si no, justo delante de la llave.
            var insertAt = Text[lineStart..t.Close].Trim().Length == 0 ? lineStart : t.Close;
            if (insertAt == t.Close) entry = nl + entry;
            Text = Text[..insertAt] + entry + Text[insertAt..];
        }
        Parse();
    }

    /// <summary>Número, texto entre comillas dobles sin saltos de línea, true o false.</summary>
    public static bool IsSimpleLiteral(string v) =>
        v is "true" or "false"
        || double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
        || (v.Length >= 2 && v[0] == '"' && v[^1] == '"' && !v.Contains('\n') && !v[1..^1].Replace("\\\"", "").Contains('"'));

    /// <summary>Texto legible: sin comillas en los textos.</summary>
    public static string Display(string luaValue) =>
        luaValue.Length >= 2 && luaValue[0] == '"' && luaValue[^1] == '"' ? luaValue[1..^1].Replace("\\\"", "\"") : luaValue;

    // ------------------------------------------------------------------ análisis

    private int _pos;

    private void Parse()
    {
        _values.Clear();
        _tables.Clear();
        _order.Clear();
        _pos = 0;
        // "options = {" (o cualquier "nombre = {"): las claves cuelgan de esa tabla sin prefijo.
        SkipSpace();
        while (_pos < Text.Length && (char.IsLetterOrDigit(Text[_pos]) || Text[_pos] == '_')) _pos++;
        SkipSpace();
        if (_pos < Text.Length && Text[_pos] == '=') { _pos++; SkipSpace(); }
        if (_pos >= Text.Length || Text[_pos] != '{') throw new InvalidDataException("options.lua no empieza por \"options = {\".");
        ParseTable("", 0);
    }

    private void ParseTable(string path, int depth)
    {
        _pos++; // {
        var index = 1;
        while (true)
        {
            SkipSpace();
            if (_pos >= Text.Length) throw new InvalidDataException("options.lua: falta una \"}\".");
            if (Text[_pos] == '}')
            {
                _tables[path] = (_pos, depth);
                _pos++;
                return;
            }

            string key;
            if (Text[_pos] == '[')
            {
                _pos++;
                SkipSpace();
                key = Text[_pos] == '"' || Text[_pos] == '\'' ? Display(ReadString()) : ReadNumber();
                SkipSpace();
                Expect(']');
                SkipSpace();
                Expect('=');
            }
            else
            {
                var save = _pos;
                var ident = ReadIdent();
                SkipSpace();
                if (ident.Length > 0 && _pos < Text.Length && Text[_pos] == '=' && (_pos + 1 >= Text.Length || Text[_pos + 1] != '='))
                {
                    key = ident;
                    _pos++;
                }
                else
                {
                    _pos = save; // valor sin clave (lista)
                    key = (index++).ToString(CultureInfo.InvariantCulture);
                }
            }
            SkipSpace();
            var full = path.Length == 0 ? key : path + "." + key;
            if (Text[_pos] == '{')
                ParseTable(full, depth + 1);
            else
            {
                var start = _pos;
                ReadValue();
                _values[full] = new Span(start, _pos - start);
                _order.Add(full);
            }
            SkipSpace();
            if (_pos < Text.Length && (Text[_pos] == ',' || Text[_pos] == ';')) _pos++;
        }
    }

    private void ReadValue()
    {
        var c = Text[_pos];
        if (c == '"' || c == '\'') { ReadString(); return; }
        if (c == '-' || c == '.' || char.IsDigit(c)) { ReadNumber(); return; }
        var ident = ReadIdent();
        if (ident.Length == 0) throw new InvalidDataException($"options.lua: valor no esperado en la posición {_pos}.");
    }

    private string ReadString()
    {
        var quote = Text[_pos];
        var start = _pos++;
        while (_pos < Text.Length && Text[_pos] != quote)
        {
            if (Text[_pos] == '\\') _pos++;
            _pos++;
        }
        _pos++;
        return Text[start.._pos];
    }

    private string ReadNumber()
    {
        var start = _pos;
        if (Text[_pos] == '-') _pos++;
        while (_pos < Text.Length && (char.IsLetterOrDigit(Text[_pos]) || Text[_pos] == '.'
                                      || (Text[_pos] is '+' or '-' && Text[_pos - 1] is 'e' or 'E')))
            _pos++;
        return Text[start.._pos];
    }

    private string ReadIdent()
    {
        var start = _pos;
        while (_pos < Text.Length && (char.IsLetterOrDigit(Text[_pos]) || Text[_pos] == '_')) _pos++;
        return Text[start.._pos];
    }

    private void Expect(char c)
    {
        if (_pos >= Text.Length || Text[_pos] != c) throw new InvalidDataException($"options.lua: se esperaba '{c}' en la posición {_pos}.");
        _pos++;
    }

    private void SkipSpace()
    {
        while (_pos < Text.Length)
        {
            if (char.IsWhiteSpace(Text[_pos])) { _pos++; continue; }
            if (_pos + 1 < Text.Length && Text[_pos] == '-' && Text[_pos + 1] == '-')
            {
                var nl = Text.IndexOf('\n', _pos);
                _pos = nl < 0 ? Text.Length : nl + 1;
                continue;
            }
            break;
        }
    }

    public override string ToString() => Text;
}
