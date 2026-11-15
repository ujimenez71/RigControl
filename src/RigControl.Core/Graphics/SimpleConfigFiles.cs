using System.Text.RegularExpressions;

namespace RigControl.Core.Graphics;

/// <summary>
/// autoexec.cfg de DCS: líneas "nombre = valor" (Lua). Solo se tocan esas líneas; comentarios ("--") y
/// cualquier otra cosa se conservan. Una clave que no está se añade al final.
/// </summary>
public sealed partial class AutoexecFile
{
    [GeneratedRegex(@"^(?<pre>[ \t]*(?<key>[A-Za-z_][A-Za-z0-9_.]*)[ \t]*=[ \t]*)(?<value>.*?)(?<post>[ \t]*(--.*)?)$")]
    private static partial Regex LineRegex();

    private readonly List<string> _lines;
    private readonly string _newline;
    private readonly bool _finalNewline;

    public AutoexecFile(string text)
    {
        _newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        _finalNewline = text.Length == 0 || text.EndsWith('\n');
        _lines = text.Length == 0 ? [] : [.. text.Replace("\r\n", "\n").Split('\n')];
        if (text.EndsWith('\n')) _lines.RemoveAt(_lines.Count - 1); // salto final
    }

    public static AutoexecFile Load(string path) => new(File.Exists(path) ? File.ReadAllText(path) : "");

    public string Text => _lines.Count == 0 ? "" : string.Join(_newline, _lines) + (_finalNewline ? _newline : "");

    /// <summary>Claves de las líneas "nombre = valor" (las comentadas con "--" no encajan: empiezan por "-").</summary>
    public IEnumerable<string> Keys => _lines.Select(line => LineRegex().Match(line)).Where(m => m.Success).Select(m => m.Groups["key"].Value).Distinct();

    /// <summary>Valor (el último, si se repite: es el que vale) o null.</summary>
    public string? Get(string key)
    {
        string? found = null;
        foreach (var line in _lines)
            if (LineRegex().Match(line) is { Success: true } m && m.Groups["key"].Value == key) found = m.Groups["value"].Value.Trim();
        return found;
    }

    public void Set(string key, string value)
    {
        for (var i = _lines.Count - 1; i >= 0; i--)
            if (LineRegex().Match(_lines[i]) is { Success: true } m && m.Groups["key"].Value == key)
            {
                _lines[i] = m.Groups["pre"].Value + value.Trim() + m.Groups["post"].Value;
                return;
            }
        _lines.Add($"{key} = {value.Trim()}");
    }
}

/// <summary>
/// UserCfg.opt de MSFS 2024: bloques "{Nombre" ... "}" con líneas "Clave Valor" (tabuladas). Claves con
/// punto: "Video.AntiAliasingVR", "GraphicsVR.Shadows.Size". Solo se cambian valores que ya existen
/// (MSFS escribe siempre todas las claves); el resto del archivo queda igual.
/// </summary>
public sealed class UserCfgFile
{
    private readonly List<string> _lines;
    private readonly string _newline;
    private readonly bool _finalNewline;
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public UserCfgFile(string text)
    {
        _newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        _finalNewline = text.EndsWith('\n');
        _lines = [.. text.Replace("\r\n", "\n").Split('\n')];
        if (_finalNewline) _lines.RemoveAt(_lines.Count - 1);
        Index();
    }

    public static UserCfgFile Load(string path) => new(File.ReadAllText(path));

    public string Text => string.Join(_newline, _lines) + (_finalNewline ? _newline : "");

    public IReadOnlyList<string> Keys => _order;

    public string? Get(string key) => _index.TryGetValue(key, out var i) ? Split(_lines[i]).Value : null;

    public void Set(string key, string value)
    {
        if (!_index.TryGetValue(key, out var i)) throw new InvalidDataException($"{key} no está en UserCfg.opt (MSFS escribe todas sus claves; ¿nombre mal escrito?).");
        var (indent, name, _) = Split(_lines[i]);
        _lines[i] = $"{indent}{name} {value.Trim()}";
    }

    private void Index()
    {
        var path = new List<string>();
        for (var i = 0; i < _lines.Count; i++)
        {
            var t = _lines[i].Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith('{')) { path.Add(t[1..].Trim()); continue; }
            if (t == "}") { if (path.Count > 0) path.RemoveAt(path.Count - 1); continue; }
            var (_, name, _) = Split(_lines[i]);
            var key = path.Count == 0 ? name : string.Join('.', path) + "." + name;
            if (_index.TryAdd(key, i)) _order.Add(key);
        }
    }

    private static (string Indent, string Name, string Value) Split(string line)
    {
        var indent = line[..(line.Length - line.TrimStart().Length)];
        var rest = line.TrimStart();
        var space = rest.IndexOfAny([' ', '\t']);
        return space < 0 ? (indent, rest, "") : (indent, rest[..space], rest[(space + 1)..].Trim());
    }
}
