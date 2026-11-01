using System.Globalization;
using System.Runtime.InteropServices;

namespace RigControl.Core.Bindings;

public enum ActionKind { Print, SimEvent, InputEvent, LVar, Key }

/// <summary>
/// Acción compilada (se interpreta al cargar el perfil, nunca en el bucle caliente).
///   "K:GEAR_UP", "K:HEADING_BUG_SET=180"   -> evento clásico de MSFS (valor entero opcional)
///   "B:AUTOPILOT_HEADING_HOLD", "B:X=0"     -> InputEvent de MSFS 2024 (valor por defecto 1)
///   "L:MiVar=1"                             -> variable local del avión (valor obligatorio)
///   "key:CTRL+SHIFT+G"                      -> teclado con SendInput (respaldo)
///   cualquier otro texto                    -> solo se imprime
/// </summary>
public sealed class ActionSpec
{
    public required ActionKind Kind { get; init; }

    /// <summary>Nombre del evento o variable, sin prefijo. Vacío en Print y Key.</summary>
    public string Name { get; init; } = "";

    public double Value { get; init; }
    public bool HasValue { get; init; }

    public KeyChord? Keys { get; init; }

    public bool IsSim => Kind is ActionKind.SimEvent or ActionKind.InputEvent or ActionKind.LVar;

    /// <summary>Devuelve null y un mensaje si el texto tiene prefijo conocido pero está mal escrito.</summary>
    public static ActionSpec? Parse(string text, out string? error)
    {
        error = null;
        var t = text.Trim();

        if (t.StartsWith("key:", StringComparison.OrdinalIgnoreCase))
        {
            var chord = KeyChord.Parse(t[4..], out error);
            return chord is null ? null : new ActionSpec { Kind = ActionKind.Key, Keys = chord };
        }

        var kind = t.Length >= 2 && t[1] == ':' ?char.ToUpperInvariant(t[0]) switch
        {
            'K' => ActionKind.SimEvent,
            'B' => ActionKind.InputEvent,
            'L' => ActionKind.LVar,
            _ => ActionKind.Print,
        } : ActionKind.Print;

        if (kind == ActionKind.Print) return new ActionSpec { Kind = ActionKind.Print };

        var body = t[2..];
        var eq = body.IndexOf('=');
        var name = (eq < 0 ? body : body[..eq]).Trim();
        var valueText = eq < 0 ? null : body[(eq + 1)..].Trim();

        if (name.Length == 0 || name.Any(char.IsWhiteSpace))
        {
            error = $"acción \"{text}\": falta el nombre o tiene espacios.";
            return null;
        }

        double value = 0;
        if (valueText is not null &&
            !double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            error = $"acción \"{text}\": el valor \"{valueText}\" no es un número (usa punto decimal).";
            return null;
        }

        switch (kind)
        {
            case ActionKind.SimEvent when valueText is not null && (value != Math.Floor(value) || value < int.MinValue || value > uint.MaxValue):
                error = $"acción \"{text}\": los eventos K: llevan un valor entero.";
                return null;
            case ActionKind.LVar when valueText is null:
                error = $"acción \"{text}\": una L:var necesita valor, p. ej. \"L:{name}=1\".";
                return null;
        }

        if (kind == ActionKind.InputEvent && valueText is null) value = 1;

        return new ActionSpec
        {
            Kind = kind,
            // Los eventos van en mayúsculas; las L:vars respetan el nombre tal cual.
            Name = kind == ActionKind.LVar ? name : name.ToUpperInvariant(),
            Value = value,
            HasValue = valueText is not null || kind == ActionKind.InputEvent,
        };
    }
}

/// <summary>
/// Combinación de teclas en scancodes: modificadores primero, la tecla principal al final.
/// Se envía por scancode porque los simuladores leen el teclado con DirectInput/RawInput.
/// </summary>
public sealed class KeyChord
{
    public required ushort[] ScanCodes { get; init; }
    public required bool[] Extended { get; init; }
    public required string Text { get; init; }

    private static readonly Dictionary<string, (byte Vk, bool Extended)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CTRL"] = (0x11, false), ["LCTRL"] = (0xA2, false), ["RCTRL"] = (0xA3, true),
        ["SHIFT"] = (0x10, false), ["LSHIFT"] = (0xA0, false), ["RSHIFT"] = (0xA1, false),
        ["ALT"] = (0x12, false), ["LALT"] = (0xA4, false), ["RALT"] = (0xA5, true),
        ["WIN"] = (0x5B, true),
        ["SPACE"] = (0x20, false), ["ENTER"] = (0x0D, false), ["TAB"] = (0x09, false),
        ["ESC"] = (0x1B, false), ["BACKSPACE"] = (0x08, false),
        ["UP"] = (0x26, true), ["DOWN"] = (0x28, true), ["LEFT"] = (0x25, true), ["RIGHT"] = (0x27, true),
        ["HOME"] = (0x24, true), ["END"] = (0x23, true), ["PGUP"] = (0x21, true), ["PGDN"] = (0x22, true),
        ["INS"] = (0x2D, true), ["DEL"] = (0x2E, true),
        ["PAUSE"] = (0x13, false), ["CAPSLOCK"] = (0x14, false), ["SCROLLLOCK"] = (0x91, false),
        ["NUM*"] = (0x6A, false), ["NUM+"] = (0x6B, false), ["NUM-"] = (0x6D, false),
        ["NUM."] = (0x6E, false), ["NUM/"] = (0x6F, true), ["NUMENTER"] = (0x0D, true),
    };

    private static readonly HashSet<byte> Modifiers = [0x11, 0xA2, 0xA3, 0x10, 0xA0, 0xA1, 0x12, 0xA4, 0xA5, 0x5B];

    public static KeyChord? Parse(string text, out string? error)
    {
        error = null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        // "NUM+" contiene el separador: se reconstruye si la última parte quedó vacía.
        if (parts.Length >= 2 && parts[^1].Length == 0 && parts[^2].Equals("NUM", StringComparison.OrdinalIgnoreCase))
            parts = [.. parts[..^2], "NUM+"];

        if (parts.Length == 0 || parts.Any(p => p.Length == 0))
        {
            error = $"tecla \"key:{text}\" mal formada; se espera p. ej. \"key:CTRL+SHIFT+G\".";
            return null;
        }

        var keys = new List<(byte Vk, bool Extended)>();
        foreach (var p in parts)
        {
            if (!TryKey(p, out var key))
            {
                error = $"tecla \"{p}\" desconocida en \"key:{text}\". Valen letras, dígitos, F1..F24, NUM0..NUM9, " +
                        $"{string.Join(", ", Named.Keys)}.";
                return null;
            }
            keys.Add(key);
        }

        if (keys.Take(keys.Count - 1).Any(k => !Modifiers.Contains(k.Vk)))
        {
            error = $"\"key:{text}\": solo la última tecla puede ser no modificadora (CTRL, SHIFT, ALT, WIN).";
            return null;
        }

        var scans = new ushort[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            scans[i] = (ushort)MapVirtualKey(keys[i].Vk, 0 /* MAPVK_VK_TO_VSC */);
            if (scans[i] == 0)
            {
                error = $"\"key:{text}\": la tecla \"{parts[i]}\" no tiene scancode en esta distribución de teclado.";
                return null;
            }
        }

        return new KeyChord
        {
            ScanCodes = scans,
            Extended = [.. keys.Select(k => k.Extended)],
            Text = string.Join("+", parts.Select(p => p.ToUpperInvariant())),
        };
    }

    private static bool TryKey(string p, out (byte Vk, bool Extended) key)
    {
        if (Named.TryGetValue(p, out key)) return true;

        if (p.Length == 1 && char.IsAsciiLetterOrDigit(p[0]))
        {
            key = ((byte)char.ToUpperInvariant(p[0]), false);
            return true;
        }

        if ((p[0] is 'F' or 'f') && int.TryParse(p[1..], out var f) && f is >= 1 and <= 24)
        {
            key = ((byte)(0x70 + f - 1), false);
            return true;
        }

        if (p.StartsWith("NUM", StringComparison.OrdinalIgnoreCase) && p.Length == 4 && char.IsAsciiDigit(p[3]))
        {
            key = ((byte)(0x60 + p[3] - '0'), false);
            return true;
        }

        key = default;
        return false;
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
