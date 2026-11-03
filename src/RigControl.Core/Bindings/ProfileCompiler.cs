namespace RigControl.Core.Bindings;

/// <summary>Valida un ProfileFile y lo compila. Acumula todos los errores en vez de parar en el primero.</summary>
public sealed class ProfileCompiler
{
    private readonly ProfileFile _file;
    private readonly List<string> _errors = [];
    private readonly List<string> _aliases = [];
    private readonly List<string> _keys = [];
    private readonly Dictionary<string, int> _aliasIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<InputRef> _inputs = [];
    private readonly List<string> _inputNames = [];
    private readonly Dictionary<InputRef, int> _slots = [];
    private readonly Dictionary<string, Condition> _layers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _layersInProgress = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Variables del simulador vigiladas (luces y condiciones "state"), sin repetir.</summary>
    private readonly List<SimVarSpec> _vars = [];
    private int _hysteresis;

    private static readonly string[] AxisNames = ["x", "y", "z", "rx", "ry", "rz", "s0", "s1"];

    private ProfileCompiler(ProfileFile file) => _file = file;

    public static (CompiledProfile? Profile, List<string> Errors) Compile(ProfileFile file)
    {
        var c = new ProfileCompiler(file);
        var profile = c.Run();
        return (c._errors.Count == 0 ? profile : null, c._errors);
    }

    /// <summary>
    /// Simulador del perfil: "sim" si lo tiene (comprobando que encaja con "aircraft"), o deducido.
    /// Público para la pantalla Perfiles, que lo muestra aunque el perfil tenga otros errores.
    /// </summary>
    public static SimKind ResolveSim(ProfileFile file, out string? error)
    {
        error = null;
        var patterns = file.Aircraft.Where(a => !string.IsNullOrWhiteSpace(a) && a.Trim() != "*").Select(a => a.Trim()).ToList();
        var dcs = patterns.Where(IsDcs).ToList();
        var msfs = patterns.Where(a => !IsDcs(a)).ToList();

        if (string.IsNullOrWhiteSpace(file.Sim))
        {
            if (dcs.Count > 0 && msfs.Count > 0)
                error = $"aircraft: mezcla aviones de DCS ({string.Join(", ", dcs)}) y de MSFS ({string.Join(", ", msfs)}); " +
                        "haz un perfil para cada simulador.";
            return dcs.Count > 0 ? SimKind.Dcs : msfs.Count > 0 ? SimKind.Msfs : SimKind.General;
        }

        switch (file.Sim.Trim().ToLowerInvariant())
        {
            case "msfs" or "msfs2024":
                if (dcs.Count > 0) error = $"sim es \"msfs\" pero aircraft tiene aviones de DCS: {string.Join(", ", dcs)}.";
                return SimKind.Msfs;
            case "dcs":
                if (msfs.Count > 0)
                    error = $"sim es \"dcs\": los aviones van con el prefijo \"dcs:\" (p. ej. \"dcs:{msfs[0]}\"), no \"{msfs[0]}\".";
                return SimKind.Dcs;
            case "general" or "any" or "*":
                if (patterns.Count > 0)
                    error = "sim es \"general\": no puede llevar aviones concretos en aircraft (solo \"*\" o nada).";
                return SimKind.General;
            default:
                error = $"sim \"{file.Sim}\" desconocido: usa \"msfs\", \"dcs\" o \"general\".";
                return SimKind.General;
        }

        static bool IsDcs(string a) => a.StartsWith("dcs:", StringComparison.OrdinalIgnoreCase);
    }

    private CompiledProfile Run()
    {
        var t = _file.Timing;
        if (t.LongMs <= 0 || t.DoubleMs <= 0 || t.SequenceMs <= 0)
            _errors.Add("timing: longMs, doubleMs y sequenceMs deben ser mayores que 0.");

        if (_file.Aircraft.Any(string.IsNullOrWhiteSpace))
            _errors.Add("aircraft: no puede haber nombres vacíos (usa \"*\" como comodín).");

        var sim = ResolveSim(_file, out var simError);
        if (simError is not null) _errors.Add(simError);

        if (_file.AxisHysteresis is < 0 or > 20)
            _errors.Add("axisHysteresis: debe estar entre 0 y 20 (% del recorrido).");
        _hysteresis = PercentToRaw(Math.Clamp(_file.AxisHysteresis, 0, 20));

        foreach (var (alias, key) in _file.Devices)
        {
            if (alias.Contains(':'))
            {
                _errors.Add($"devices: el alias \"{alias}\" no puede contener ':'.");
                continue;
            }
            _aliasIndex[alias] = _aliases.Count;
            _aliases.Add(alias);
            _keys.Add(key.ToUpperInvariant());
        }

        foreach (var name in _file.Layers.Keys)
            ResolveLayer(name);

        var lights = CompileLights();

        var bindings = new List<Binding>();
        for (var i = 0; i < _file.Bindings.Count; i++)
        {
            var b = CompileBinding(_file.Bindings[i], i + 1);
            if (b is not null) bindings.Add(b);
        }

        CheckVarsMatchSim(sim);

        return new CompiledProfile
        {
            Name = string.IsNullOrWhiteSpace(_file.Name) ? "(sin nombre)" : _file.Name,
            Timing = t,
            AxisHysteresis = _hysteresis,
            Sim = sim,
            Aircraft = [.. _file.Aircraft.Select(a => a.Trim().Replace('/', '\\').ToLowerInvariant())],
            Lights = lights,
            WatchedVars = [.. _vars],
            DeviceAliases = [.. _aliases],
            DeviceKeys = [.. _keys],
            Inputs = [.. _inputs],
            InputNames = [.. _inputNames],
            Bindings = [.. bindings],
        };
    }

    private Binding? CompileBinding(BindingFile f, int number)
    {
        var where = $"binding #{number}";
        var errorsBefore = _errors.Count;

        ActionSpec? output = null;
        if (string.IsNullOrWhiteSpace(f.Action))
        {
            _errors.Add($"{where}: falta \"action\".");
        }
        else
        {
            output = ActionSpec.Parse(f.Action, out var actionError);
            if (actionError is not null) _errors.Add($"{where}: {actionError}");
        }

        Gesture gesture;
        int[] inputs;
        string trigger;

        if (f.Sequence is not null)
        {
            if (f.Input is not null || f.Gesture is not null)
                _errors.Add($"{where}: una secuencia no lleva \"input\" ni \"gesture\".");
            if (f.Sequence.Count < 2)
                _errors.Add($"{where}: una secuencia necesita al menos 2 pasos (para uno, usa \"press\").");

            gesture = Gesture.Sequence;
            inputs = [.. f.Sequence.Select(s => ParseInput(s, where))];
            trigger = $"secuencia {string.Join(" > ", f.Sequence)}";
        }
        else
        {
            if (f.Input is null)
            {
                _errors.Add($"{where}: falta \"input\" (o \"sequence\").");
                return null;
            }

            if (!TryParseGesture(f.Gesture, out gesture))
                _errors.Add($"{where}: gesto \"{f.Gesture}\" desconocido (press, release, short, long, hold, double).");

            inputs = [ParseInput(f.Input, where)];
            trigger = $"{f.Input} {gesture.ToString().ToLowerInvariant()}";
        }

        if (f.RepeatMs is { } repeat)
        {
            if (gesture != Gesture.Hold)
                _errors.Add($"{where}: \"repeatMs\" solo vale con \"gesture\": \"hold\".");
            else if (output is not null && !output.IsExternal)
                _errors.Add($"{where}: \"repeatMs\" solo vale con acciones K:, B:, L: o D: (un key: mantenido ya es una tecla pulsada).");
            if (repeat is < 20 or > 2000)
                _errors.Add($"{where}: \"repeatMs\" debe estar entre 20 y 2000.");
        }

        var when = f.When is null ? null : CompileCondition(f.When, where);

        if (_errors.Count > errorsBefore) return null;
        return new Binding
        {
            Number = number, Gesture = gesture, Inputs = inputs, When = when,
            Action = f.Action, Output = output!, Trigger = trigger, RepeatMs = f.RepeatMs ?? 0,
            Description = string.IsNullOrWhiteSpace(f.Desc) ? null : f.Desc.Trim(),
        };
    }

    // ---------------------------------------------------------------- luces

    /// <summary>Índice de una variable vigilada (la añade si no estaba). Luces y condiciones comparten la lista.</summary>
    private int VarIndex(SimVarSpec spec)
    {
        var i = _vars.FindIndex(v => v.Name.Equals(spec.Name, StringComparison.OrdinalIgnoreCase) &&
                                     v.Unit.Equals(spec.Unit, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) return i;
        _vars.Add(spec);
        return _vars.Count - 1;
    }

    /// <summary>D: solo en perfiles de DCS (o generales); A:/L: solo en los de MSFS (o generales).</summary>
    private void CheckVarsMatchSim(SimKind sim)
    {
        if (sim == SimKind.Msfs && _vars.FirstOrDefault(v => v.IsDcs) is { } d)
            _errors.Add($"{d.Name}: es un control de DCS-BIOS y el perfil es de MSFS.");
        if (sim == SimKind.Dcs && _vars.FirstOrDefault(v => !v.IsDcs) is { } m)
            _errors.Add($"{m.Name}: es una variable de MSFS y el perfil es de DCS (usa \"D:CONTROL\").");
    }

    /// <summary>"lights": { "NombreLuz": "condición" | ["condición", ...] } -> reglas (las variables van a _vars).</summary>
    private LightRule[] CompileLights()
    {
        var rules = new List<LightRule>();
        var seen = BravoLed.None;
        foreach (var (name, value) in _file.Lights)
        {
            var where = $"lights.{name}";
            if (!Enum.TryParse<BravoLed>(name, ignoreCase: true, out var led) || led == BravoLed.None ||
                !Enum.IsDefined(led))
            {
                _errors.Add($"{where}: luz desconocida. Valen: {string.Join(", ", Enum.GetNames<BravoLed>().Skip(1))}.");
                continue;
            }
            if ((seen & led) != 0)
            {
                _errors.Add($"{where}: la luz está repetida.");
                continue;
            }
            seen |= led;

            List<string> texts;
            if (value.ValueKind == System.Text.Json.JsonValueKind.String) texts = [value.GetString()!];
            else if (value.ValueKind == System.Text.Json.JsonValueKind.Array &&
                     value.EnumerateArray().All(e => e.ValueKind == System.Text.Json.JsonValueKind.String))
                texts = [.. value.EnumerateArray().Select(e => e.GetString()!)];
            else
            {
                _errors.Add($"{where}: se espera una condición (texto) o una lista de condiciones.");
                continue;
            }
            if (texts.Count == 0)
            {
                _errors.Add($"{where}: la lista de condiciones está vacía.");
                continue;
            }

            var conditions = new List<LightCondition>();
            foreach (var text in texts)
            {
                if (LightParser.TryParse(text, VarIndex, out var c, out var error)) conditions.Add(c);
                else _errors.Add($"{where}: {error}");
            }
            if (conditions.Count == texts.Count)
                rules.Add(new LightRule(led, Enum.GetName(led)!, [.. conditions], string.Join(" Y ", texts)));
        }

        return [.. rules];
    }

    private static bool TryParseGesture(string? text, out Gesture gesture)
    {
        gesture = Gesture.Press;
        if (text is null) return false;
        return Enum.TryParse(text, ignoreCase: true, out gesture) && gesture != Gesture.Sequence;
    }

    // ---------------------------------------------------------- condiciones

    private Condition? ResolveLayer(string name)
    {
        if (_layers.TryGetValue(name, out var done)) return done;
        if (!_file.Layers.TryGetValue(name, out var def)) return null;

        if (!_layersInProgress.Add(name))
        {
            _errors.Add($"layers: la capa \"{name}\" forma un ciclo de referencias entre capas.");
            return null;
        }

        var inner = CompileCondition(def, $"capa \"{name}\"");
        _layersInProgress.Remove(name);
        if (inner is null) return null;

        var layer = new LayerCondition(name, inner);
        _layers[name] = layer;
        return layer;
    }

    private Condition? CompileCondition(ConditionFile c, string where)
    {
        var set = (c.Input is not null ? 1 : 0) + (c.Layer is not null ? 1 : 0) + (c.State is not null ? 1 : 0) +
                  (c.All is not null ? 1 : 0) + (c.Any is not null ? 1 : 0) + (c.Not is not null ? 1 : 0);
        if (set != 1)
        {
            _errors.Add($"{where}: cada condición debe tener exactamente una de input, layer, state, all, any o not.");
            return null;
        }

        if (c.State is not null)
        {
            if (!LightParser.TryParse(c.State, VarIndex, out var state, out var error))
            {
                _errors.Add($"{where}: {error}");
                return null;
            }
            return new StateCondition(state, c.State.Trim());
        }

        if (c.Input is not null)
        {
            var slot = ParseInput(c.Input, where);
            return slot < 0 ? null : new InputHeldCondition(slot, _inputNames[slot]);
        }

        if (c.Layer is not null)
        {
            if (!_file.Layers.ContainsKey(c.Layer))
            {
                _errors.Add($"{where}: la capa \"{c.Layer}\" no existe.");
                return null;
            }
            return ResolveLayer(c.Layer);
        }

        if (c.Not is not null)
        {
            var inner = CompileCondition(c.Not, where);
            return inner is null ? null : new NotCondition(inner);
        }

        var list = (c.All ?? c.Any)!;
        if (list.Count == 0)
        {
            _errors.Add($"{where}: \"all\"/\"any\" no puede estar vacío.");
            return null;
        }

        var items = list.Select(i => CompileCondition(i, where)).ToArray();
        if (items.Any(i => i is null)) return null;
        return c.All is not null ? new AllCondition(items!) : new AnyCondition(items!);
    }

    // -------------------------------------------------------------- entradas

    /// <summary>Convierte "alias:b12", "alias:hat1.up" o "alias:z>90" en un hueco del motor. Devuelve -1 si hay error.</summary>
    private int ParseInput(string text, string where)
    {
        var colon = text.IndexOf(':');
        if (colon <= 0)
        {
            _errors.Add($"{where}: entrada \"{text}\" mal formada; se espera \"alias:b12\", \"alias:hat1.up\" o \"alias:z>90\".");
            return -1;
        }

        var alias = text[..colon];
        var part = text[(colon + 1)..].ToLowerInvariant();

        if (!_aliasIndex.TryGetValue(alias, out var device))
        {
            _errors.Add($"{where}: el dispositivo \"{alias}\" no está declarado en \"devices\".");
            return -1;
        }

        InputRef input;
        if (part.IndexOfAny(['<', '>', '=']) > 0)
        {
            if (!TryParseAxisZone(part, device, text, where, out input)) return -1;
        }
        else if (part.StartsWith("hat"))
        {
            var dot = part.IndexOf('.');
            if (dot < 0 || !int.TryParse(part[3..dot], out var hat) || hat is < 1 or > 4 ||
                !TryParseHatDirection(part[(dot + 1)..], out var kind))
            {
                _errors.Add($"{where}: hat \"{text}\" no válido; se espera hat1..hat4 con .up/.right/.down/.left.");
                return -1;
            }
            input = new InputRef(device, kind, hat - 1);
        }
        else if (part.StartsWith('b') && int.TryParse(part[1..], out var button) && button is >= 1 and <= 128)
        {
            input = new InputRef(device, InputKind.Button, button - 1);
        }
        else
        {
            _errors.Add($"{where}: entrada \"{text}\" no válida; los botones van de b1 a b128.");
            return -1;
        }

        if (_slots.TryGetValue(input, out var slot)) return slot;

        slot = _inputs.Count;
        _slots[input] = slot;
        _inputs.Add(input);
        _inputNames.Add($"{_aliases[device]}:{part}");
        return slot;
    }

    /// <summary>"z>90" (90..100 %), "z<10" (0..10 %) o "z=40..60". Porcentajes con punto decimal.</summary>
    private bool TryParseAxisZone(string part, int device, string text, string where, out InputRef input)
    {
        input = default;
        var op = part.IndexOfAny(['<', '>', '=']);
        var axis = Array.IndexOf(AxisNames, part[..op]);
        var arg = part[(op + 1)..];

        double lo = 0, hi = 100;
        var ok = part[op] switch
        {
            '>' => TryParsePercent(arg, out lo),
            '<' => TryParsePercent(arg, out hi),
            _ => arg.Split("..") is [var a, var b] && TryParsePercent(a, out lo) && TryParsePercent(b, out hi),
        };

        if (axis < 0 || !ok)
        {
            _errors.Add($"{where}: zona de eje \"{text}\" no válida; se espera p. ej. \"alias:z>90\", \"alias:z<10\" o " +
                        $"\"alias:z=40..60\" (ejes {string.Join(", ", AxisNames)}; porcentajes de 0 a 100).");
            return false;
        }

        var rawLo = PercentToRaw(lo);
        var rawHi = PercentToRaw(hi);
        if (rawLo >= rawHi)
        {
            _errors.Add($"{where}: zona de eje \"{text}\" vacía: el límite inferior debe ser menor que el superior.");
            return false;
        }
        if (!AxisZone.IsReachable(rawLo, rawHi, _hysteresis))
        {
            _errors.Add($"{where}: zona de eje \"{text}\" demasiado estrecha para la histéresis " +
                        $"({_file.AxisHysteresis}% por cada borde interior); ensánchala o baja \"axisHysteresis\".");
            return false;
        }

        input = new InputRef(device, InputKind.AxisZone, axis, rawLo, rawHi);
        return true;
    }

    private static bool TryParsePercent(string text, out double value) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)
        && value is >= 0 and <= 100;

    private static int PercentToRaw(double percent) => (int)Math.Round(percent * AxisZone.Max / 100);

    private static bool TryParseHatDirection(string text, out InputKind kind)
    {
        kind = text switch
        {
            "up" => InputKind.HatUp,
            "right" => InputKind.HatRight,
            "down" => InputKind.HatDown,
            "left" => InputKind.HatLeft,
            _ => InputKind.Button,
        };
        return kind != InputKind.Button;
    }
}
