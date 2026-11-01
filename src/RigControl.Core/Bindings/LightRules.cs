using System.Globalization;

namespace RigControl.Core.Bindings;

/// <summary>Variable del simulador que se vigila: "A:NOMBRE" o "L:Nombre", con sus unidades.</summary>
public sealed record SimVarSpec(string Name, string Unit)
{
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

/// <summary>Una luz del Bravo y las condiciones que deben cumplirse todas para encenderla.</summary>
public sealed record LightRule(BravoLed Led, string Name, LightCondition[] Conditions, string Text)
{
    public bool Evaluate(double[] values)
    {
        foreach (var c in Conditions)
            if (!c.Evaluate(values)) return false;
        return true;
    }
}

/// <summary>
/// Interpreta las condiciones de luces del perfil:
///   "A:AUTOPILOT MASTER"                           -> encendida si el valor no es 0
///   "A:ELECTRICAL MAIN BUS VOLTAGE, Volts&lt;24.5"  -> unidades tras la coma ("Number" por defecto)
///   "A:GEAR CENTER POSITION=0.01..0.99"            -> rango (ambos incluidos)
///   operadores: &gt; &gt;= &lt; &lt;= = ; "!" delante niega; también "L:Variable".
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

        if (t.Length < 3 || t[1] != ':' || char.ToUpperInvariant(t[0]) is not ('A' or 'L'))
        {
            error = $"luz \"{text}\": se espera una variable \"A:NOMBRE\" o \"L:Nombre\".";
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
            error = $"luz \"{text}\": falta el nombre de la variable o las unidades.";
            return false;
        }
        // Las A: (simvars) van en mayúsculas; las L: respetan el nombre.
        name = char.ToUpperInvariant(name[0]) == 'A' ? name.ToUpperInvariant() : "L:" + name[2..];

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
                if (a > b) { error = $"luz \"{text}\": el rango está al revés."; return false; }
            }
        }

        condition = new LightCondition(varIndex(new SimVarSpec(name, unit)), op, a, b, negate);
        return true;
    }

    private static bool Num(string s, out double v) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static bool Bad(string text, out string? error)
    {
        error = $"luz \"{text}\": valor no válido (operadores > >= < <= = y rangos \"=a..b\", con punto decimal).";
        return false;
    }
}
