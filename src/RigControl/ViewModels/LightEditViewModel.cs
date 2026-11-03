using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using RigControl.Core;

namespace RigControl.ViewModels;

public enum LightCompare { NonZero, Equal, Greater, GreaterOrEqual, Less, LessOrEqual, Range }

public sealed record CompareOption(LightCompare Value, string Label);

/// <summary>
/// Una condición de luz por partes: [no] A:VARIABLE[, unidades] operador valor. Se compone en el
/// texto del perfil ("!A:BRAKE PARKING POSITION", "A:ELECTRICAL BATTERY LOAD, Amperes>1", "=1..24.5").
/// Si el texto del JSON no encaja en el patrón, se edita como texto libre.
/// </summary>
public sealed partial class LightTermViewModel(LightEditViewModel owner) : ObservableObject
{
    public static readonly CompareOption[] Compares =
    [
        new(LightCompare.NonZero, "≠ 0 (activa)"), new(LightCompare.Equal, "= igual a"),
        new(LightCompare.Greater, "> mayor que"), new(LightCompare.GreaterOrEqual, "≥ mayor o igual"),
        new(LightCompare.Less, "< menor que"), new(LightCompare.LessOrEqual, "≤ menor o igual"),
        new(LightCompare.Range, "entre (incluidos)"),
    ];

    /// <summary>Variables frecuentes (las del C172 y otras habituales) para el desplegable.</summary>
    public static readonly string[] CommonVars =
    [
        "A:AUTOPILOT MASTER", "A:AUTOPILOT HEADING LOCK", "A:AUTOPILOT NAV1 LOCK", "A:AUTOPILOT APPROACH HOLD",
        "A:AUTOPILOT BACKCOURSE HOLD", "A:AUTOPILOT ALTITUDE LOCK", "A:AUTOPILOT VERTICAL HOLD",
        "A:AUTOPILOT FLIGHT LEVEL CHANGE", "A:AUTOPILOT AIRSPEED HOLD", "A:ELECTRICAL MASTER BATTERY",
        "A:ELECTRICAL BATTERY LOAD", "A:ELECTRICAL BATTERY VOLTAGE", "A:ELECTRICAL MAIN BUS VOLTAGE",
        "A:GENERAL ENG MASTER ALTERNATOR:1", "A:GENERAL ENG OIL PRESSURE:1", "A:GENERAL ENG STARTER:1",
        "A:GENERAL ENG FUEL PRESSURE:1", "A:GENERAL ENG COMBUSTION:1", "A:ENG ON FIRE:1", "A:SUCTION PRESSURE",
        "A:BRAKE PARKING POSITION", "A:PITOT HEAT", "A:STRUCTURAL DEICE SWITCH", "A:EXIT OPEN:0",
        "A:GEAR LEFT POSITION", "A:GEAR CENTER POSITION", "A:GEAR RIGHT POSITION", "A:GEAR HANDLE POSITION",
        "A:FUEL TOTAL QUANTITY", "A:GENERAL ENG FUEL PUMP SWITCH:1", "A:APU SWITCH", "A:MASTER WARNING",
        "A:MASTER CAUTION", "A:HYDRAULIC PRESSURE:1", "A:SIM ON GROUND",
    ];

    private bool _negate;
    private string _variable = "";
    private string _unit = "";
    private LightCompare _compare = LightCompare.NonZero;
    private string _value = "";
    private string _value2 = "";
    private string? _raw;
    private string _live = "";

    public LightEditViewModel Owner { get; } = owner;

    public bool Negate { get => _negate; set { if (Set(ref _negate, value)) Changed(); } }
    public string Variable { get => _variable; set { if (Set(ref _variable, (value ?? "").Trim())) Changed(); } }
    public string Unit { get => _unit; set { if (Set(ref _unit, value.Trim())) Changed(); } }

    public LightCompare Compare
    {
        get => _compare;
        set { if (Set(ref _compare, value)) { Raise(nameof(HasValue)); Raise(nameof(IsRange)); Changed(); } }
    }

    public string Value { get => _value; set { if (Set(ref _value, value.Trim().Replace(',', '.'))) Changed(); } }
    public string Value2 { get => _value2; set { if (Set(ref _value2, value.Trim().Replace(',', '.'))) Changed(); } }

    /// <summary>Texto libre (cuando el del JSON no encaja en las partes).</summary>
    public string? Raw { get => _raw; set { if (Set(ref _raw, value)) Changed(); } }
    public bool IsRaw => _raw is not null;
    public bool IsParts => _raw is null;

    public bool HasValue => _compare != LightCompare.NonZero;
    public bool IsRange => _compare == LightCompare.Range;

    /// <summary>Valor actual que llega de MSFS (vista en vivo).</summary>
    public string Live { get => _live; set => Set(ref _live, value); }

    public string Text
    {
        get
        {
            if (_raw is not null) return _raw;
            var v = _variable.Contains(':') ? _variable : "A:" + _variable;
            var unit = _unit.Length == 0 || _unit.Equals("Number", StringComparison.OrdinalIgnoreCase) ? "" : $", {_unit}";
            var op = _compare switch
            {
                LightCompare.Equal => "=" + _value,
                LightCompare.Greater => ">" + _value,
                LightCompare.GreaterOrEqual => ">=" + _value,
                LightCompare.Less => "<" + _value,
                LightCompare.LessOrEqual => "<=" + _value,
                LightCompare.Range => $"={_value}..{_value2}",
                _ => "",
            };
            return (_negate ? "!" : "") + v + unit + op;
        }
    }

    [GeneratedRegex(@"^\s*(!)?\s*([AaLlDd]:[^,<>=]+?)\s*(?:,\s*([^<>=]+?))?\s*(?:(>=|<=|>|<|=)\s*(-?\d+(?:\.\d+)?)(?:\.\.(-?\d+(?:\.\d+)?))?)?\s*$")]
    private static partial Regex Pattern();

    public static LightTermViewModel FromText(LightEditViewModel owner, string text)
    {
        var t = new LightTermViewModel(owner);
        var m = Pattern().Match(text);
        if (!m.Success)
        {
            t._raw = text;
            return t;
        }
        t._negate = m.Groups[1].Success;
        t._variable = m.Groups[2].Value.Trim();
        t._unit = m.Groups[3].Success ? m.Groups[3].Value.Trim() : "";
        var op = m.Groups[4].Value;
        t._value = m.Groups[5].Value;
        if (m.Groups[6].Success) { t._compare = LightCompare.Range; t._value2 = m.Groups[6].Value; }
        else t._compare = op switch
        {
            "=" => LightCompare.Equal, ">" => LightCompare.Greater, ">=" => LightCompare.GreaterOrEqual,
            "<" => LightCompare.Less, "<=" => LightCompare.LessOrEqual, _ => LightCompare.NonZero,
        };
        return t;
    }

    private void Changed()
    {
        Raise(nameof(Text)); Raise(nameof(IsRaw)); Raise(nameof(IsParts));
        Owner.OnChanged();
    }
}

/// <summary>Una luz del Bravo y sus condiciones (deben cumplirse todas).</summary>
public sealed class LightEditViewModel : ObservableObject
{
    /// <summary>Nombres de las luces tal como van en el perfil (los de BravoLed).</summary>
    public static readonly string[] LedNames = [.. Enum.GetNames<BravoLed>().Where(n => n != nameof(BravoLed.None))];

    private string _led;
    private string _liveState = "";
    private bool? _liveOn;

    public LightEditViewModel(string led) => _led = led;

    public event Action? Changed;

    public string Led { get => _led; set { if (Set(ref _led, value)) OnChanged(); } }
    public ObservableCollection<LightTermViewModel> Terms { get; } = [];

    /// <summary>Vista en vivo: "encendida", "apagada" o vacío si no hay datos.</summary>
    public string LiveState { get => _liveState; set => Set(ref _liveState, value); }
    public bool? LiveOn { get => _liveOn; set => Set(ref _liveOn, value); }

    private string _error = "";
    public string Error { get => _error; set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    public void AddTerm(string text = "A:")
    {
        var t = LightTermViewModel.FromText(this, text);
        if (text == "A:") t.Variable = "";
        Terms.Add(t);
        OnChanged();
    }

    public void RemoveTerm(LightTermViewModel t)
    {
        Terms.Remove(t);
        OnChanged();
    }

    /// <summary>Lo que va al JSON: un texto si es una condición, una lista si son varias.</summary>
    public object ToJsonValue() => Terms.Count == 1 ? Terms[0].Text : Terms.Select(t => t.Text).ToArray();

    internal void OnChanged() => Changed?.Invoke();

    public static string Format(double v) => double.IsNaN(v) ? "sin dato" : v.ToString("0.##", CultureInfo.CurrentCulture);
}
