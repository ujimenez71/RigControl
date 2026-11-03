using System.Collections.ObjectModel;
using RigControl.Core.Bindings;

namespace RigControl.ViewModels;

public enum ConditionMode { None, All, Any }
public enum TermKind { Input, Layer, State }

public sealed record ModeOption(ConditionMode Value, string Label);
public sealed record TermKindOption(TermKind Value, string Label);

/// <summary>
/// Una pieza de una condición: "se mantiene esta entrada", "esta capa está activa" o "estado del
/// simulador" (D:CONTROL=2, A:VAR&gt;5...), opcionalmente negada. En el estado, NO se guarda como "!"
/// dentro del texto (sin datos del simulador no se cumple, ni negada).
/// </summary>
public sealed class ConditionTermViewModel(ConditionEditViewModel owner) : ObservableObject
{
    private TermKind _kind = TermKind.Input;
    private bool _negate;
    private string _input = "";
    private string _layer = "";
    private string _state = "";

    public ConditionEditViewModel Owner { get; } = owner;

    public TermKind Kind
    {
        get => _kind;
        set { if (Set(ref _kind, value)) { Raise(nameof(IsInput)); Raise(nameof(IsLayer)); Raise(nameof(IsState)); Owner.OnChanged(); } }
    }

    public bool Negate { get => _negate; set { if (Set(ref _negate, value)) Owner.OnChanged(); } }
    public string Input { get => _input; set { if (Set(ref _input, value.Trim())) Owner.OnChanged(); } }
    public string Layer { get => _layer; set { if (Set(ref _layer, value ?? "")) Owner.OnChanged(); } }
    public string State { get => _state; set { if (Set(ref _state, value.Trim())) Owner.OnChanged(); } }

    public bool IsInput => _kind == TermKind.Input;
    public bool IsLayer => _kind == TermKind.Layer;
    public bool IsState => _kind == TermKind.State;

    public ConditionFile ToFile()
    {
        if (_kind == TermKind.State) return new ConditionFile { State = (_negate ? "!" : "") + _state };
        var c = _kind == TermKind.Input ? new ConditionFile { Input = _input } : new ConditionFile { Layer = _layer };
        return _negate ? new ConditionFile { Not = c } : c;
    }
}

/// <summary>
/// Editor de una condición "plana": ninguna, o una lista de piezas que deben cumplirse todas (Y) o
/// alguna (O). Es lo que se usa casi siempre (modificadores, capas). Si la condición del JSON es más
/// compleja (anidada), se conserva tal cual y solo se puede quitar.
/// </summary>
public sealed class ConditionEditViewModel : ObservableObject
{
    public static readonly ModeOption[] BindingModes =
    [
        new(ConditionMode.None, "Siempre (sin condición)"),
        new(ConditionMode.All, "Solo si se cumplen todas"),
        new(ConditionMode.Any, "Solo si se cumple alguna"),
    ];

    public static readonly ModeOption[] LayerModes =
    [
        new(ConditionMode.All, "Activa si se cumplen todas"),
        new(ConditionMode.Any, "Activa si se cumple alguna"),
    ];

    public static readonly TermKindOption[] Kinds =
    [
        new(TermKind.Input, "Se mantiene la entrada"),
        new(TermKind.Layer, "Está activa la capa"),
        new(TermKind.State, "Estado del simulador"),
    ];

    private ConditionMode _mode;
    private ConditionFile? _complex;
    private bool _isLayer;

    public event Action? Changed;

    public ObservableCollection<ConditionTermViewModel> Terms { get; } = [];

    public ConditionMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            if (value != ConditionMode.None && Terms.Count == 0 && _complex is null) AddTerm();
            RaiseShape();
            OnChanged();
        }
    }

    /// <summary>La condición del JSON no cabe en "todas/alguna de piezas simples": se conserva tal cual.</summary>
    public bool IsComplex => _complex is not null;
    public bool IsNotComplex => _complex is null;
    public ModeOption[] ModeOptions => _isLayer ? LayerModes : BindingModes;
    public bool ShowTerms => _complex is null && _mode != ConditionMode.None;
    public string ComplexText => _complex is null ? "" : System.Text.Json.JsonSerializer.Serialize(_complex,
        new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        });

    public ConditionTermViewModel AddTerm()
    {
        var t = new ConditionTermViewModel(this);
        Terms.Add(t);
        OnChanged();
        return t;
    }

    public void RemoveTerm(ConditionTermViewModel t)
    {
        Terms.Remove(t);
        if (Terms.Count == 0 && !_isLayer) _mode = ConditionMode.None; // una capa sin piezas queda como error visible
        RaiseShape();
        OnChanged();
    }

    /// <summary>Quita una condición compleja (para poder hacer otra con el editor).</summary>
    public void ClearComplex()
    {
        _complex = null;
        _mode = ConditionMode.None;
        Terms.Clear();
        RaiseShape();
        OnChanged();
    }

    /// <summary>Cambia el nombre de una capa en las piezas que la usan.</summary>
    public void RenameLayer(string from, string to)
    {
        foreach (var t in Terms.Where(t => t.Kind == TermKind.Layer && t.Layer == from)) t.Layer = to;
    }

    public bool UsesLayer(string name) => Terms.Any(t => t.Kind == TermKind.Layer && t.Layer == name)
                                          || (ComplexText.Contains($"\"layer\":\"{name}\"", StringComparison.Ordinal));

    public static ConditionEditViewModel FromFile(ConditionFile? c, bool isLayer = false)
    {
        var vm = new ConditionEditViewModel { _isLayer = isLayer };
        if (c is null)
        {
            vm._mode = isLayer ? ConditionMode.All : ConditionMode.None;
            return vm;
        }

        // Una pieza suelta, una lista plana (all / any) de piezas, o "compleja".
        if (TryTerm(vm, c, out var single))
        {
            vm._mode = ConditionMode.All;
            vm.Terms.Add(single);
            return vm;
        }
        var list = c.All ?? c.Any;
        var onlyList = (c.All is null) != (c.Any is null) && c.Input is null && c.Layer is null && c.State is null && c.Not is null;
        if (onlyList && list!.Count > 0)
        {
            var terms = new List<ConditionTermViewModel>();
            foreach (var item in list)
            {
                if (!TryTerm(vm, item, out var t)) { terms = null; break; }
                terms.Add(t);
            }
            if (terms is not null)
            {
                vm._mode = c.All is not null ? ConditionMode.All : ConditionMode.Any;
                foreach (var t in terms) vm.Terms.Add(t);
                return vm;
            }
        }
        vm._complex = c;
        vm._mode = ConditionMode.All;
        return vm;
    }

    public ConditionFile? ToFile()
    {
        if (_complex is not null) return _complex;
        if (_mode == ConditionMode.None || Terms.Count == 0) return null;
        if (Terms.Count == 1) return Terms[0].ToFile();
        var items = Terms.Select(t => t.ToFile()).ToList();
        return _mode == ConditionMode.All ? new ConditionFile { All = items } : new ConditionFile { Any = items };
    }

    private static bool TryTerm(ConditionEditViewModel owner, ConditionFile c, out ConditionTermViewModel term)
    {
        term = new ConditionTermViewModel(owner);
        var negate = false;
        if (c.Not is not null && c.Input is null && c.Layer is null && c.State is null && c.All is null && c.Any is null)
        {
            // {"not": {"state": ...}} se cumple sin datos (no es lo mismo que "!"): se deja como compleja.
            if (c.Not.State is not null) return false;
            negate = true;
            c = c.Not;
        }
        if (c.All is not null || c.Any is not null || c.Not is not null) return false;
        var set = (c.Input is not null ? 1 : 0) + (c.Layer is not null ? 1 : 0) + (c.State is not null ? 1 : 0);
        if (set != 1) return false;
        if (c.Input is not null) { term.Input = c.Input; term.Kind = TermKind.Input; }
        else if (c.Layer is not null) { term.Layer = c.Layer; term.Kind = TermKind.Layer; }
        else
        {
            var text = c.State!.Trim();
            negate = text.StartsWith('!');
            term.State = negate ? text[1..].TrimStart() : text;
            term.Kind = TermKind.State;
        }
        term.Negate = negate;
        return true;
    }

    private void RaiseShape()
    {
        Raise(nameof(Mode)); Raise(nameof(IsComplex)); Raise(nameof(IsNotComplex)); Raise(nameof(ShowTerms)); Raise(nameof(ComplexText));
    }

    internal void OnChanged() => Changed?.Invoke();
}

/// <summary>Una capa con nombre: una condición reutilizable (típicamente un modificador mantenido).</summary>
public sealed class LayerEditViewModel : ObservableObject
{
    private string _name;

    public LayerEditViewModel(string name, ConditionEditViewModel condition)
    {
        _name = name;
        Condition = condition;
        Condition.Changed += () => Changed?.Invoke();
    }

    /// <summary>Cambio de nombre (antiguo, nuevo): el editor lo propaga a las condiciones que la usan.</summary>
    public event Action<string, string>? Renamed;
    public event Action? Changed;

    public ConditionEditViewModel Condition { get; }

    public string Name
    {
        get => _name;
        set
        {
            var v = value.Trim();
            var old = _name;
            if (!Set(ref _name, v)) return;
            Renamed?.Invoke(old, v);
            Changed?.Invoke();
        }
    }
}
