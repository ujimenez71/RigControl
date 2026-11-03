namespace RigControl.Core.Bindings;

public interface IActionSink
{
    void OnAction(Binding binding, ActionPhase phase, long timeMs);

    /// <summary>Se llama en cada ciclo del bucle (p. ej. para soltar teclas de un toque).</summary>
    void Tick(long timeMs) { }

    /// <summary>Se llama al arrancar y en cada recarga, antes de usar el perfil.</summary>
    void ProfileLoaded(CompiledProfile profile) { }

    /// <summary>Valores actuales de CompiledProfile.WatchedVars, para las condiciones "state" (NaN = sin dato).</summary>
    double[] Values => [];
}

/// <summary>
/// Reconocedor de gestos. No sabe nada de DirectInput: recibe cambios de entrada con marca de
/// tiempo (SetInput) y un Tick periódico, así que se puede probar sin hardware.
///
/// Reglas:
///  - press/hold-inicio se disparan al pulsar; release/hold-fin, al soltar.
///  - short: al soltar antes de longMs. Si la entrada no tiene "long", cualquier duración vale.
///    Si tiene "double", el short espera doubleMs por si llega el segundo toque.
///  - long: al alcanzar longMs sin soltar (una vez por pulsación).
///  - double: segunda pulsación dentro de doubleMs tras soltar la primera; anula el short.
///  - sequence: pasos en orden, cada uno dentro de sequenceMs del anterior. Pulsar otra entrada
///    con bindings la rompe; las entradas que solo son modificadores no.
///  - Las condiciones (entradas, capas y estado del simulador) se evalúan al pulsar. Entre bindings del mismo disparador, los condicionados
///    que se cumplen tienen prioridad; si no se cumple ninguno, se disparan los que no tienen condición.
/// Sin asignaciones en SetInput/Tick.
/// </summary>
public sealed class BindingEngine
{
    private static readonly int GestureCount = Enum.GetValues<Gesture>().Length;

    private readonly IActionSink _sink;
    private readonly long _longMs, _doubleMs, _sequenceMs;

    private readonly bool[] _down;
    private readonly long[] _downAt;
    private readonly long[] _upAt;
    private readonly bool[] _longFired;
    private readonly bool[] _pendingShort;
    /// <summary>Esta pulsación ya no puede dar short/long: fue un doble toque o ya estaba pulsada al arrancar.</summary>
    private readonly bool[] _consumed;
    private readonly bool[] _isTrigger;

    /// <summary>[hueco][gesto] -> bindings de ese disparador.</summary>
    private readonly Binding[][][] _byInput;
    private readonly SequenceGroup[] _sequences;

    private sealed class SequenceGroup(int[] steps, Binding[] bindings)
    {
        public int[] Steps { get; } = steps;
        public Binding[] Bindings { get; } = bindings;
        public int Progress;
        public long LastAt;
    }

    public BindingEngine(CompiledProfile profile, IActionSink sink)
    {
        _sink = sink;
        _longMs = profile.Timing.LongMs;
        _doubleMs = profile.Timing.DoubleMs;
        _sequenceMs = profile.Timing.SequenceMs;

        var n = profile.Inputs.Length;
        _down = new bool[n];
        _downAt = new long[n];
        _upAt = new long[n];
        _longFired = new bool[n];
        _pendingShort = new bool[n];
        _consumed = new bool[n];
        _isTrigger = new bool[n];

        _byInput = new Binding[n][][];
        for (var slot = 0; slot < n; slot++)
        {
            _byInput[slot] = new Binding[GestureCount][];
            for (var g = 0; g < GestureCount; g++)
            {
                var gesture = (Gesture)g;
                var s = slot;
                _byInput[slot][g] = gesture == Gesture.Sequence
                    ? []
                    : [.. profile.Bindings.Where(b => b.Gesture == gesture && b.Inputs[0] == s)];
            }
        }

        // Secuencias con los mismos pasos forman un grupo (para aplicar la prioridad por condición).
        _sequences = [.. profile.Bindings
            .Where(b => b.Gesture == Gesture.Sequence)
            .GroupBy(b => string.Join(",", b.Inputs))
            .Select(g => new SequenceGroup(g.First().Inputs, [.. g]))];

        foreach (var b in profile.Bindings)
            foreach (var slot in b.Inputs)
                _isTrigger[slot] = true;
    }

    public bool IsDown(int slot) => _down[slot];

    /// <summary>
    /// Emite el FIN de las acciones hold que sigan activas. Se llama antes de descartar el motor
    /// (cambio de perfil), para que ninguna acción mantenida quede colgada.
    /// </summary>
    public void EndHolds(long timeMs)
    {
        foreach (var gestures in _byInput)
            foreach (var b in gestures[(int)Gesture.Hold])
            {
                if (!b.HoldActive) continue;
                b.HoldActive = false;
                _sink.OnAction(b, ActionPhase.End, timeMs);
            }
    }

    /// <summary>Fija el estado inicial sin generar eventos (interruptores que ya están activados al arrancar).</summary>
    public void Prime(int slot, bool value)
    {
        _down[slot] = value;
        _consumed[slot] = value;
    }

    public void SetInput(int slot, bool value, long timeMs)
    {
        if (_down[slot] == value) return;
        _down[slot] = value;
        if (value) OnDown(slot, timeMs);
        else OnUp(slot, timeMs);
    }

    public void Tick(long timeMs)
    {
        for (var slot = 0; slot < _down.Length; slot++)
        {
            if (_pendingShort[slot] && timeMs - _upAt[slot] > _doubleMs)
            {
                _pendingShort[slot] = false;
                Fire(_byInput[slot][(int)Gesture.Short], ActionPhase.Trigger, timeMs);
            }

            if (_down[slot] && !_longFired[slot] && !_consumed[slot] && timeMs - _downAt[slot] >= _longMs)
                FireLong(slot, timeMs);
        }
    }

    private void OnDown(int slot, long t)
    {
        var g = _byInput[slot];

        var isDouble = false;
        if (_pendingShort[slot])
        {
            _pendingShort[slot] = false;
            if (t - _upAt[slot] <= _doubleMs)
                isDouble = true;
            else
                Fire(g[(int)Gesture.Short], ActionPhase.Trigger, t); // venció antes del Tick: con las condiciones de su pulsación
        }

        var values = _sink.Values;
        foreach (var list in g)
            foreach (var b in list)
                b.Matched = b.When?.Evaluate(_down, values) ?? true;

        _downAt[slot] = t;
        _longFired[slot] = false;
        _consumed[slot] = isDouble;

        Fire(g[(int)Gesture.Press], ActionPhase.Trigger, t);
        Fire(g[(int)Gesture.Hold], ActionPhase.Start, t);
        if (isDouble) Fire(g[(int)Gesture.Double], ActionPhase.Trigger, t);

        if (_isTrigger[slot]) AdvanceSequences(slot, t);
    }

    private void OnUp(int slot, long t)
    {
        var g = _byInput[slot];

        Fire(g[(int)Gesture.Release], ActionPhase.Trigger, t);
        foreach (var b in g[(int)Gesture.Hold])
        {
            if (!b.HoldActive) continue;
            b.HoldActive = false;
            _sink.OnAction(b, ActionPhase.End, t);
        }

        if (_consumed[slot])
        {
            _consumed[slot] = false;
            return;
        }

        var longs = g[(int)Gesture.Long];
        if (longs.Length > 0)
        {
            // Soltado justo al cumplirse longMs, antes de que llegara el Tick: sigue siendo larga.
            if (!_longFired[slot] && t - _downAt[slot] >= _longMs) FireLong(slot, t);
            if (_longFired[slot]) return;
        }

        var shorts = g[(int)Gesture.Short];
        if (shorts.Length == 0) return;

        if (g[(int)Gesture.Double].Length > 0)
        {
            _pendingShort[slot] = true;
            _upAt[slot] = t;
        }
        else
        {
            Fire(shorts, ActionPhase.Trigger, t);
        }
    }

    private void FireLong(int slot, long t)
    {
        _longFired[slot] = true;
        Fire(_byInput[slot][(int)Gesture.Long], ActionPhase.Trigger, t);
    }

    private void AdvanceSequences(int slot, long t)
    {
        foreach (var seq in _sequences)
        {
            if (seq.Progress > 0 && t - seq.LastAt > _sequenceMs) seq.Progress = 0;

            if (seq.Steps[seq.Progress] == slot)
            {
                seq.Progress++;
                seq.LastAt = t;
                if (seq.Progress < seq.Steps.Length) continue;

                seq.Progress = 0;
                foreach (var b in seq.Bindings)
                    b.Matched = b.When?.Evaluate(_down, _sink.Values) ?? true;
                Fire(seq.Bindings, ActionPhase.Trigger, t);
            }
            else
            {
                // Paso equivocado: se rompe, salvo que esta pulsación sea el inicio de la secuencia.
                seq.Progress = seq.Steps[0] == slot ? 1 : 0;
                seq.LastAt = t;
            }
        }
    }

    /// <summary>Dispara los condicionados que se cumplen; si no hay ninguno, los que no tienen condición.</summary>
    private void Fire(Binding[] bindings, ActionPhase phase, long t)
    {
        if (bindings.Length == 0) return;

        var anyConditional = false;
        foreach (var b in bindings)
        {
            if (b.When is null || !b.Matched) continue;
            anyConditional = true;
            Emit(b, phase, t);
        }

        if (anyConditional) return;
        foreach (var b in bindings)
            if (b.When is null) Emit(b, phase, t);
    }

    private void Emit(Binding b, ActionPhase phase, long t)
    {
        if (phase == ActionPhase.Start) b.HoldActive = true;
        _sink.OnAction(b, phase, t);
    }
}
