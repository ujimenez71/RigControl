using RigControl.Core.Bindings;

namespace RigControl.ViewModels;

/// <summary>A dónde se envía una acción (el prefijo de la acción en el JSON).</summary>
public enum ActionTarget { Cabin, Event, Variable, Key, Dcs, Text }

public sealed record TargetOption(ActionTarget Value, string Label, string Help);
public sealed record GestureOption(string Value, string Label);

/// <summary>
/// Un binding en el editor. La acción se edita por partes (destino + nombre + valor) y se compone
/// en el texto del JSON ("B:NOMBRE=valor", "key:CTRL+G", "D:CONTROL VALOR"...). Lo que el editor no
/// sabe editar (secuencias, condiciones) se conserva tal cual.
/// </summary>
public sealed class BindingEditViewModel : ObservableObject
{
    public static readonly TargetOption[] Targets =
    [
        new(ActionTarget.Cabin, "MSFS (cabina del avión)", "B: un control de la cabina del avión cargado: interruptores, botones, mandos. La lista sale del avión que tengas cargado en MSFS."),
        new(ActionTarget.Event, "MSFS (evento general)", "K: un evento de MSFS que vale para cualquier avión (trim, tren, autopiloto...)."),
        new(ActionTarget.Variable, "MSFS (variable del avión)", "L: una variable local del avión; la usan aviones con sistemas propios. Necesita un valor."),
        new(ActionTarget.Key, "Teclado", "Simula una tecla o combinación en la ventana que tenga el foco."),
        new(ActionTarget.Dcs, "DCS (cabina)", "D: un control de la cabina de DCS a través de DCS-BIOS. Sin valor = pulsador (1 al pulsar, 0 al soltar)."),
        new(ActionTarget.Text, "Nada (solo registro)", "No envía nada: el texto solo aparece en el registro de Ejecución."),
    ];

    public static readonly GestureOption[] Gestures =
    [
        new("press", "Al pulsar"), new("release", "Al soltar"), new("short", "Pulsación corta"),
        new("long", "Pulsación larga"), new("hold", "Mientras se mantiene"), new("double", "Doble toque"),
    ];

    private string _input = "";
    private string _gesture = "press";
    private ActionTarget _target = ActionTarget.Cabin;
    private string _name = "";
    private string _value = "";
    private string _desc = "";
    private string _repeatMs = "";
    private string _inputText = "";
    private string _kindText = "";
    private string _error = "";

    /// <summary>Se dispara con cualquier cambio que afecte al perfil (para validar y marcar "sin guardar").</summary>
    public event Action? Changed;

    public List<string>? Sequence { get; private init; }
    public ConditionFile? When { get; private init; }
    private string _conditionText = "";
    public string ConditionText { get => _conditionText; set => Set(ref _conditionText, value); }

    public bool IsSequence => Sequence is not null;
    public bool IsNotSequence => Sequence is null;
    public bool HasCondition => When is not null;
    public string SequenceText => Sequence is null ? "" : string.Join("  >  ", Sequence);

    public string Input { get => _input; set { if (Set(ref _input, value.Trim())) OnChanged(); } }

    public string Gesture
    {
        get => _gesture;
        set { if (Set(ref _gesture, value)) { Raise(nameof(GestureText)); Raise(nameof(ShowRepeat)); OnChanged(); } }
    }

    public ActionTarget Target
    {
        get => _target;
        set
        {
            if (!Set(ref _target, value)) return;
            if (value == ActionTarget.Key) Name = ""; // la tecla se captura de nuevo
            Raise(nameof(TargetHelp)); Raise(nameof(NameLabel)); Raise(nameof(HasValue)); Raise(nameof(ValueHint));
            Raise(nameof(IsKey)); Raise(nameof(IsNotKey)); Raise(nameof(ShowRepeat));
            OnActionChanged();
        }
    }

    public string Name { get => _name; set { if (Set(ref _name, value.Trim())) OnActionChanged(); } }
    public string Value { get => _value; set { if (Set(ref _value, value.Trim())) OnActionChanged(); } }

    public string Desc { get => _desc; set { if (Set(ref _desc, value)) { Raise(nameof(Description)); OnChanged(); } } }

    /// <summary>Texto: vacío = sin repetición.</summary>
    public string RepeatMs { get => _repeatMs; set { if (Set(ref _repeatMs, value.Trim())) OnChanged(); } }

    public bool IsKey => _target == ActionTarget.Key;
    public bool IsNotKey => _target != ActionTarget.Key;
    public bool HasValue => _target is ActionTarget.Cabin or ActionTarget.Event or ActionTarget.Variable or ActionTarget.Dcs;
    /// <summary>"repeatMs" solo vale en un hold con acción de MSFS.</summary>
    public bool ShowRepeat => _gesture == "hold" && _target is ActionTarget.Cabin or ActionTarget.Event or ActionTarget.Variable;
    public string TargetHelp => Targets.First(t => t.Value == _target).Help;

    public string NameLabel => _target switch
    {
        ActionTarget.Cabin => "Control de cabina (B:)",
        ActionTarget.Event => "Evento (K:)",
        ActionTarget.Variable => "Variable (L:)",
        ActionTarget.Key => "Tecla (haz clic aquí y pulsa la combinación)",
        ActionTarget.Dcs => "Control de DCS-BIOS",
        _ => "Texto para el registro",
    };

    public string ValueHint => _target switch
    {
        ActionTarget.Cabin => "Valor (vacío = 1). Interruptores: 1 encender, 0 apagar.",
        ActionTarget.Event => "Valor (opcional, número entero).",
        ActionTarget.Variable => "Valor (obligatorio).",
        ActionTarget.Dcs => "Valor: vacío = pulsador; INC, DEC, TOGGLE o un número.",
        _ => "",
    };

    /// <summary>La acción compuesta, tal como irá al JSON.</summary>
    public string Action => _target switch
    {
        ActionTarget.Cabin => "B:" + _name + (_value.Length > 0 ? "=" + _value : ""),
        ActionTarget.Event => "K:" + _name + (_value.Length > 0 ? "=" + _value : ""),
        ActionTarget.Variable => "L:" + _name + "=" + _value,
        ActionTarget.Key => "key:" + _name,
        ActionTarget.Dcs => "D:" + _name + (_value.Length > 0 ? " " + _value : ""),
        _ => _name,
    };

    public string AutoDescription => ActionDescriber.Describe(Action);
    public string Description => string.IsNullOrWhiteSpace(_desc) ? AutoDescription : _desc;
    public string GestureText => IsSequence ? "secuencia" : ProfileInspector.GestureName(ParseGesture(_gesture));

    // Lo que calcula la validación del perfil completo (nombres del catálogo, destino, errores).
    public string InputText { get => _inputText; set => Set(ref _inputText, value); }
    public string KindText { get => _kindText; set => Set(ref _kindText, value); }
    public string Error { get => _error; set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    public static BindingEditViewModel FromFile(BindingFile f)
    {
        var vm = new BindingEditViewModel
        {
            _input = f.Input ?? "",
            _gesture = f.Gesture?.ToLowerInvariant() ?? "press",
            Sequence = f.Sequence is null ? null : [.. f.Sequence],
            When = f.When,
            _desc = f.Desc ?? "",
            _repeatMs = f.RepeatMs?.ToString() ?? "",
        };
        vm.SplitAction(f.Action);
        vm._inputText = vm.IsSequence ? vm.SequenceText : vm._input;
        return vm;
    }

    public BindingFile ToFile() => new()
    {
        Input = IsSequence ? null : _input,
        Gesture = IsSequence ? null : _gesture,
        Sequence = Sequence is null ? null : [.. Sequence],
        When = When,
        Action = Action,
        Desc = string.IsNullOrWhiteSpace(_desc) ? null : _desc.Trim(),
        RepeatMs = int.TryParse(_repeatMs, out var r) && ShowRepeat ? r : null,
    };

    public BindingEditViewModel Clone() => FromFile(ToFile());

    /// <summary>"B:NOMBRE=1" -> destino Cabina, nombre NOMBRE, valor 1 (y así con cada prefijo).</summary>
    private void SplitAction(string action)
    {
        var a = action.Trim();
        var colon = a.IndexOf(':');
        var prefix = colon > 0 ? a[..colon].ToUpperInvariant() : "";
        var body = colon > 0 ? a[(colon + 1)..] : a;
        (string N, string V) SplitAt(char sep)
        {
            var i = body.IndexOf(sep);
            return i < 0 ? (body.Trim(), "") : (body[..i].Trim(), body[(i + 1)..].Trim());
        }

        switch (prefix)
        {
            case "B": _target = ActionTarget.Cabin; (_name, _value) = SplitAt('='); break;
            case "K": _target = ActionTarget.Event; (_name, _value) = SplitAt('='); break;
            case "L": _target = ActionTarget.Variable; (_name, _value) = SplitAt('='); break;
            case "KEY": _target = ActionTarget.Key; _name = body.Trim(); break;
            case "D": _target = ActionTarget.Dcs; (_name, _value) = SplitAt(' '); break;
            default: _target = ActionTarget.Text; _name = a; break;
        }
    }

    private static RigControl.Core.Bindings.Gesture ParseGesture(string g) =>
        Enum.TryParse<RigControl.Core.Bindings.Gesture>(g, ignoreCase: true, out var v) ? v : RigControl.Core.Bindings.Gesture.Press;

    private void OnActionChanged()
    {
        Raise(nameof(Action)); Raise(nameof(AutoDescription)); Raise(nameof(Description));
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke();
}
