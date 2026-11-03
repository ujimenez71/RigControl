namespace RigControl.Core.Bindings;

/// <summary>
/// Condición compilada. Evaluate no asigna memoria: recibe el estado actual de todas las entradas
/// y los valores de las variables vigiladas del simulador (CompiledProfile.WatchedVars; NaN = sin dato).
/// Canonical sirve para comparar condiciones equivalentes (Conflict Guard); Describe, para mostrarlas.
/// </summary>
public abstract class Condition
{
    public abstract bool Evaluate(bool[] down, double[] values);
    public abstract string Canonical { get; }
    public abstract string Describe();
    public abstract void CollectInputs(HashSet<int> slots);
}

public sealed class InputHeldCondition(int slot, string name) : Condition
{
    public override bool Evaluate(bool[] down, double[] values) => down[slot];
    public override string Canonical => name;
    public override string Describe() => name;
    public override void CollectInputs(HashSet<int> slots) => slots.Add(slot);
}

/// <summary>
/// Estado del simulador: "D:PLT_EXTL_ACOL_L_SW=2", "A:GEAR HANDLE POSITION", "!L:Var>3"...
/// Sin dato (simulador desconectado) es falsa, también negada con "!" dentro del texto: así el binding
/// sin condición hace de respaldo. Ojo: {"not": {"state": ...}} sin dato es verdadera (not de falso).
/// </summary>
public sealed class StateCondition(LightCondition condition, string text) : Condition
{
    public LightCondition Inner => condition;
    public override bool Evaluate(bool[] down, double[] values) => condition.Evaluate(values);
    public override string Canonical => "state:" + text.Replace(" ", "").ToUpperInvariant();
    public override string Describe() => text;
    public override void CollectInputs(HashSet<int> slots) { }
}

/// <summary>Referencia a una capa: se evalúa como su definición y se muestra con su nombre.</summary>
public sealed class LayerCondition(string name, Condition inner) : Condition
{
    public override bool Evaluate(bool[] down, double[] values) => inner.Evaluate(down, values);
    public override string Canonical => inner.Canonical;
    public override string Describe() => name;
    public override void CollectInputs(HashSet<int> slots) => inner.CollectInputs(slots);
}

public sealed class NotCondition(Condition inner) : Condition
{
    public override bool Evaluate(bool[] down, double[] values) => !inner.Evaluate(down, values);
    public override string Canonical => $"not({inner.Canonical})";
    public override string Describe() => $"NOT {inner.Describe()}";
    public override void CollectInputs(HashSet<int> slots) => inner.CollectInputs(slots);
}

public sealed class AllCondition(Condition[] items) : Condition
{
    public override bool Evaluate(bool[] down, double[] values)
    {
        foreach (var c in items)
            if (!c.Evaluate(down, values)) return false;
        return true;
    }

    // Ordenado: "a AND b" equivale a "b AND a".
    public override string Canonical => $"all({string.Join(",", items.Select(c => c.Canonical).Order())})";
    public override string Describe() => $"({string.Join(" AND ", items.Select(c => c.Describe()))})";
    public override void CollectInputs(HashSet<int> slots) { foreach (var c in items) c.CollectInputs(slots); }
}

public sealed class AnyCondition(Condition[] items) : Condition
{
    public override bool Evaluate(bool[] down, double[] values)
    {
        foreach (var c in items)
            if (c.Evaluate(down, values)) return true;
        return false;
    }

    public override string Canonical => $"any({string.Join(",", items.Select(c => c.Canonical).Order())})";
    public override string Describe() => $"({string.Join(" OR ", items.Select(c => c.Describe()))})";
    public override void CollectInputs(HashSet<int> slots) { foreach (var c in items) c.CollectInputs(slots); }
}
