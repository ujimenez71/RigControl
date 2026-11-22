using System.Globalization;

namespace RigControl.Core.Bindings;

/// <summary>
/// Variable del simulador que se vigila: "A:NOMBRE" o "L:Nombre" (MSFS, con sus unidades) o
/// "D:CONTROL" (DCS-BIOS: valor entero del control en la imagen de la cabina, sin unidades).
/// </summary>
public sealed record SimVarSpec(string Name, string Unit)
{
    /// <summary>Viene de DCS-BIOS, no de SimConnect.</summary>
    public bool IsDcs => Name.StartsWith("D:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Viene de la telemetría de IL-2 ("I:GEAR"), no de SimConnect.</summary>
    public bool IsIl2 => Name.StartsWith("I:", StringComparison.OrdinalIgnoreCase);

    /// <summary>La pide SimConnect (A: y L: de MSFS).</summary>
    public bool IsMsfs => !IsDcs && !IsIl2;

    /// <summary>Texto para SimConnect_AddToDataDefinition, p. ej. "A:AUTOPILOT MASTER" -> "AUTOPILOT MASTER".</summary>
    public string DatumName => Name.StartsWith("A:", StringComparison.OrdinalIgnoreCase) ? Name[2..] : Name;
    public override string ToString() => $"{Name}, {Unit}";
}

public enum LightOp : byte { NonZero, Greater, GreaterOrEqual, Less, LessOrEqual, Equal, Range }

/// <summary>Una condición sobre una variable vigilada. Evaluate no asigna memoria.</summary>
public readonly record struct LightCondition(int Var, LightOp Op, double A, double B, bool Negate)
{
    public bool Evaluate(double[] values)
    {
        if ((uint)Var >= (uint)values.Length) return false; // sin valores (p. ej. sin simulador)
        var v = values[Var];
        if (double.IsNaN(v)) return false; // todavía sin dato: apagada (también si está negada)
        var r = Op switch
        {
            LightOp.NonZero => Math.Abs(v) > 1e-9,
            LightOp.Greater => v > A,
            LightOp.GreaterOrEqual => v >= A,
            LightOp.Less => v < A,
            LightOp.LessOrEqual => v <= A,
            LightOp.Equal => Math.Abs(v - A) < 1e-6,
            LightOp.Range => v >= A && v <= B,
            _ => false,
        };
        return r != Negate;
    }
}

/// <summary>
/// Una luz y las condiciones que deben cumplirse todas para encenderla: del Bravo (<paramref name="Led"/>)
/// o de un WinWing (<paramref name="WinWing"/>, y entonces Led = None).
/// </summary>
public sealed record LightRule(BravoLed Led, string Name, LightCondition[] Conditions, string Text,
                               RigControl.Core.WinWing.WinWingLight? WinWing = null)
{
    public bool Evaluate(double[] values)
    {
        foreach (var c in Conditions)
            if (!c.Evaluate(values)) return false;
        return true;
    }
}

/// <summary>
/// Interpreta las condiciones sobre el estado del simulador (luces y "state" de los bindings):
///   "A:AUTOPILOT MASTER"                           -> encendida si el valor no es 0
///   "A:ELECTRICAL MAIN BUS VOLTAGE, Volts&lt;24.5"  -> unidades tras la coma ("Number" por defecto)
///   "A:GEAR CENTER POSITION=0.01..0.99"            -> rango (ambos incluidos)
///   operadores: &gt; &gt;= &lt; &lt;= = ; "!" delante niega; también "L:Variable".
///   "D:PLT_EXTL_ACOL_L_SW=2"                       -> DCS-BIOS: valor entero del control (sin unidades)
///   "I:GEAR>=0.99", "I:RPM:2>2000"                 -> telemetría de IL-2 (sin unidades; ver Il2Telemetry.Vars)
/// </summary>
public static class LightParser
{
    public static bool TryParse(string text, Func<SimVarSpec, int> varIndex, out LightCondition condition, out string? error)
    {
        condition = default;
        error = null;
        var t = text.Trim();

        var negate = t.StartsWith('!');
        if (negate) t = t[1..].TrimStart();

        var prefix = t.Length >= 3 && t[1] == ':' ? char.ToUpperInvariant(t[0]) : ' ';
        if (prefix is not ('A' or 'L' or 'D' or 'I'))
        {
            error = $"\"{text}\": se espera una variable \"A:NOMBRE\" o \"L:Nombre\" (MSFS), \"D:CONTROL\" (DCS-BIOS) o \"I:NOMBRE\" (IL-2).";
            return false;
        }

        var opAt = t.IndexOfAny(['<', '>', '=']);
        var left = (opAt < 0 ? t : t[..opAt]).Trim();
        var right = opAt < 0 ? "" : t[opAt..].Trim();

        var comma = left.IndexOf(',');
        var name = (comma < 0 ? left : left[..comma]).Trim();
        var unit = comma < 0 ? "Number" : left[(comma + 1)..].Trim();
        if (name.Length <= 2 || unit.Length == 0)
        {
            error = $"\"{text}\": falta el nombre de la variable o las unidades.";
            return false;
        }
        if (prefix == 'I')
        {
            if (comma >= 0)
            {
                error = $"\"{text}\": las variables de IL-2 (I:) no llevan unidades.";
                return false;
            }
            if (!Il2Telemetry.TryParseVar(name, out _, out var il2Error)) { error = il2Error; return false; }
            name = "I:" + string.Concat(name[2..].Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        }
        else if (prefix == 'D')
        {
            if (comma >= 0)
            {
                error = $"\"{text}\": los controles de DCS-BIOS (D:) no llevan unidades.";
                return false;
            }
            name = name.ToUpperInvariant();
            if (name.AsSpan(2).ContainsAnyExcept(DcsNameChars))
            {
                error = $"\"{text}\": nombre de control de DCS-BIOS no válido (letras, números y _).";
                return false;
            }
        }
        // Las A: (simvars) y D: (controles) van en mayúsculas; las L: respetan el nombre.
        else name = prefix == 'A' ? name.ToUpperInvariant() : "L:" + name[2..];

        LightOp op;
        double a = 0, b = 0;
        if (right.Length == 0) op = LightOp.NonZero;
        else if (right.StartsWith(">=")) { op = LightOp.GreaterOrEqual; if (!Num(right[2..], out a)) return Bad(text, out error); }
        else if (right.StartsWith("<=")) { op = LightOp.LessOrEqual; if (!Num(right[2..], out a)) return Bad(text, out error); }
        else if (right[0] == '>') { op = LightOp.Greater; if (!Num(right[1..], out a)) return Bad(text, out error); }
        else if (right[0] == '<') { op = LightOp.Less; if (!Num(right[1..], out a)) return Bad(text, out error); }
        else
        {
            var body = right[1..];
            var dots = body.IndexOf("..", StringComparison.Ordinal);
            if (dots < 0) { op = LightOp.Equal; if (!Num(body, out a)) return Bad(text, out error); }
            else
            {
                op = LightOp.Range;
                if (!Num(body[..dots], out a) || !Num(body[(dots + 2)..], out b)) return Bad(text, out error);
                if (a > b) { error = $"\"{text}\": el rango está al revés."; return false; }
            }
        }

        condition = new LightCondition(varIndex(new SimVarSpec(name, unit)), op, a, b, negate);
        return true;
    }

    private static readonly System.Buffers.SearchValues<char> DcsNameChars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_");

    private static bool Num(string s, out double v) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static bool Bad(string text, out string? error)
    {
        error = $"\"{text}\": valor no válido (operadores > >= < <= = y rangos \"=a..b\", con punto decimal).";
        return false;
    }
}
