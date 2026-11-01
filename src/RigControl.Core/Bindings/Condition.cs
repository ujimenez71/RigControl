namespace RigControl.Core.Bindings;

/// <summary>
/// Condición compilada. Evaluate no asigna memoria: recibe el estado actual de todas las entradas.
/// Canonical sirve para comparar condiciones equivalentes (Conflict Guard); Describe, para mostrarlas.
/// </summary>
public abstract class Condition
{
    public abstract bool Evaluate(bool[] down);
    public abstract string Canonical { get; }
    public abstract string Describe();
    public abstract void CollectInputs(HashSet<int> slots);
}

public sealed class InputHeldCondition(int slot, string name) : Condition
{
    public override bool Evaluate(bool[] down) => down[slot];
    public override string Canonical => name;
    public override string Describe() => name;
    public override void CollectInputs(HashSet<int> slots) => slots.Add(slot);
}

/// <summary>Referencia a una capa: se evalúa como su definición y se muestra con su nombre.</summary>
public sealed class LayerCondition(string name, Condition inner) : Condition
{
    public override bool Evaluate(bool[] down) => inner.Evaluate(down);
    public override string Canonical => inner.Canonical;
    public override string Describe() => name;
    public override void CollectInputs(HashSet<int> slots) => inner.CollectInputs(slots);
}

public sealed class NotCondition(Condition inner) : Condition
{
    public override bool Evaluate(bool[] down) => !inner.Evaluate(down);
    public override string Canonical => $"not({inner.Canonical})";
    public override string Describe() => $"NOT {inner.Describe()}";
    public override void CollectInputs(HashSet<int> slots) => inner.CollectInputs(slots);
}

public sealed class AllCondition(Condition[] items) : Condition
{
    public override bool Evaluate(bool[] down)
    {
        foreach (var c in items)
            if (!c.Evaluate(down)) return false;
        return true;
    }

    // Ordenado: "a AND b" equivale a "b AND a".
    public override string Canonical => $"all({string.Join(",", items.Select(c => c.Canonical).Order())})";
    public override string Describe() => $"({string.Join(" AND ", items.Select(c => c.Describe()))})";
    public override void CollectInputs(HashSet<int> slots) { foreach (var c in items) c.CollectInputs(slots); }
}

public sealed class AnyCondition(Condition[] items) : Condition
{
    public override bool Evaluate(bool[] down)
    {
        foreach (var c in items)
            if (c.Evaluate(down)) return true;
        return false;
    }

    public override string Canonical => $"any({string.Join(",", items.Select(c => c.Canonical).Order())})";
    public override string Describe() => $"({string.Join(" OR ", items.Select(c => c.Describe()))})";
    public override void CollectInputs(HashSet<int> slots) { foreach (var c in items) c.CollectInputs(slots); }
}
