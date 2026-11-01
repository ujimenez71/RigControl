namespace RigControl.Core.Bindings;

public enum Severity { Error, Warning, Info }

public sealed record Finding(Severity Severity, string Message)
{
    public override string ToString() => Severity switch
    {
        Severity.Error => $"ERROR  {Message}",
        Severity.Warning => $"AVISO  {Message}",
        _ => $"INFO   {Message}",
    };
}

/// <summary>
/// Detecta bindings duplicados (mismo disparador y condición equivalente: ERROR) y combinaciones
/// que probablemente no hacen lo que esperas (AVISO / INFO).
/// </summary>
public static class ConflictGuard
{
    private static readonly Gesture[] Timed = [Gesture.Short, Gesture.Long, Gesture.Double];

    public static List<Finding> Check(CompiledProfile profile)
    {
        var findings = new List<Finding>();

        // Duplicados: mismo disparador + misma condición (canónica; "a AND b" == "b AND a").
        var duplicates = profile.Bindings
            .GroupBy(b => (Trigger: TriggerKey(b), Condition: b.When?.Canonical ?? ""))
            .Where(g => g.Count() > 1);
        foreach (var g in duplicates)
        {
            var list = string.Join(", ", g.Select(b => $"#{b.Number} \"{b.Action}\""));
            findings.Add(new(Severity.Error, $"Duplicado en {g.First()}: {list}"));
        }

        foreach (var slotGroup in profile.Bindings.Where(b => b.Gesture != Gesture.Sequence).GroupBy(b => b.Inputs[0]))
        {
            var name = profile.InputNames[slotGroup.Key];
            var gestures = slotGroup.Select(b => b.Gesture).ToHashSet();

            var immediate = gestures.Where(g => g is Gesture.Press or Gesture.Hold).ToList();
            var timed = gestures.Where(Timed.Contains).ToList();
            if (immediate.Count > 0 && timed.Count > 0)
                findings.Add(new(Severity.Warning,
                    $"{name}: {Names(immediate)} se dispara siempre al pulsar, también cuando la pulsación acaba siendo {Names(timed)}."));

            if (gestures.Contains(Gesture.Short) && gestures.Contains(Gesture.Double))
                findings.Add(new(Severity.Info,
                    $"{name}: tiene short y double, así que el short se retrasa {profile.Timing.DoubleMs} ms para esperar el segundo toque."));
        }

        // Modificadores que además tienen bindings propios: se dispararán al usarlos como modificador.
        var triggers = profile.Bindings.SelectMany(b => b.Inputs).ToHashSet();
        var modifiers = new HashSet<int>();
        foreach (var b in profile.Bindings) b.When?.CollectInputs(modifiers);
        foreach (var slot in modifiers.Where(triggers.Contains).Order())
            findings.Add(new(Severity.Warning,
                $"{profile.InputNames[slot]}: se usa en condiciones y también tiene bindings propios. Normal en un interruptor; " +
                "en un botón modificador, sus bindings se dispararán cada vez que lo uses como modificador."));

        var printOnly = profile.Bindings.Where(b => b.Output.Kind == ActionKind.Print).ToList();
        if (printOnly.Count > 0)
            findings.Add(new(Severity.Info,
                $"{printOnly.Count} acción(es) sin prefijo (K:, B:, L:, key:): solo se imprimen. " +
                $"Bindings {string.Join(", ", printOnly.Select(b => $"#{b.Number}"))}."));

        foreach (var b in profile.Bindings.Where(b => b.Gesture == Gesture.Hold && b.Output.IsSim && b.RepeatMs == 0))
            findings.Add(new(Severity.Info,
                $"#{b.Number} {b.Trigger}: en un hold, \"{b.Action}\" se envía una vez al pulsar " +
                "(añade \"repeatMs\" para que se repita mientras lo mantienes)."));

        return [.. findings.OrderBy(f => f.Severity)];
    }

    private static string TriggerKey(Binding b) => $"{b.Gesture}:{string.Join(",", b.Inputs)}";

    private static string Names(IEnumerable<Gesture> gestures) =>
        string.Join("/", gestures.Select(g => g.ToString().ToLowerInvariant()));
}
