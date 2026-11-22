using RigControl.Core.WinWing;

namespace RigControl.ViewModels;

/// <summary>Un trozo de la vista previa de una línea del ICP: texto normal o invertido.</summary>
public sealed record IcpRun(string Text, bool Inverted);

/// <summary>
/// Una de las 5 líneas de la pantalla del ICP en el editor: el texto del perfil ("UHF {D:UHF_RADIO}"), su error y
/// la vista previa con los valores de ahora (MSFS o DCS).
/// </summary>
public sealed class IcpLineViewModel(int number) : ObservableObject
{
    private string _text = "";
    private string _error = "";
    private IReadOnlyList<IcpRun> _preview = [new(new string(' ', IcpFont.Columns), false)];

    public int Number { get; } = number;
    public string Label => $"Línea {Number}";

    public string Text { get => _text; set { if (Set(ref _text, value ?? "")) Changed?.Invoke(); } }

    public string Error { get => _error; set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    /// <summary>Trozos de la línea tal como se verían en la pantalla (siempre 25 caracteres).</summary>
    public IReadOnlyList<IcpRun> Preview { get => _preview; private set => Set(ref _preview, value); }

    public event Action? Changed;

    /// <summary>Pone la vista previa a partir de las celdas dibujadas; solo avisa a la vista si cambia algo.</summary>
    public void SetPreview(ReadOnlySpan<char> cells, ReadOnlySpan<bool> inverted)
    {
        var runs = new List<IcpRun>();
        var start = 0;
        for (var i = 1; i <= cells.Length; i++)
        {
            if (i < cells.Length && inverted[i] == inverted[start]) continue;
            runs.Add(new IcpRun(new string(cells[start..i]).ToUpperInvariant(), inverted[start]));
            start = i;
        }
        if (runs.Count != _preview.Count || !runs.SequenceEqual(_preview)) Preview = runs;
    }
}

/// <summary>Un valor que se puede insertar en una línea del ICP: lo que se escribe ("{D:UHF_RADIO}") y su descripción.</summary>
public sealed record IcpFieldOption(string Text, string Label)
{
    public override string ToString() => Label;

    public static readonly IcpFieldOption[] Fixed =
    [
        new("{hora}", "{hora} · hora (HH:mm)"),
        new("{hora|HH:mm:ss}", "{hora|HH:mm:ss} · hora con segundos"),
        new("{perfil}", "{perfil} · nombre del perfil"),
        new("{avion}", "{avion} · avión cargado"),
        new("{A:FUEL TOTAL QUANTITY, gallons|0}", "{A:FUEL TOTAL QUANTITY, gallons|0} · MSFS: combustible"),
        new("{A:INDICATED ALTITUDE, feet|0}", "{A:INDICATED ALTITUDE, feet|0} · MSFS: altitud"),
        new("{A:AIRSPEED INDICATED, knots|0}", "{A:AIRSPEED INDICATED, knots|0} · MSFS: velocidad"),
        new("{A:COM ACTIVE FREQUENCY:1, MHz|0.000}", "{A:COM ACTIVE FREQUENCY:1, MHz|0.000} · MSFS: COM1"),
        new("{A:NAV ACTIVE FREQUENCY:1, MHz|0.00}", "{A:NAV ACTIVE FREQUENCY:1, MHz|0.00} · MSFS: NAV1"),
        new("{A:TRANSPONDER CODE:1|0000}", "{A:TRANSPONDER CODE:1|0000} · MSFS: transpondedor"),
    ];

    /// <summary>Los fijos sin los de MSFS y las variables de la telemetría de IL-2.</summary>
    public static readonly IcpFieldOption[] Il2 =
    [
        .. Fixed.Take(4),
        new("{I:IAS|0}", "{I:IAS|0} · IL-2: velocidad indicada, km/h"),
        new("{I:IAS_MPH|0}", "{I:IAS_MPH|0} · IL-2: velocidad indicada, mph"),
        new("{I:AGL|0}", "{I:AGL|0} · IL-2: altura sobre el terreno, m"),
        new("{I:AGL_FT|0}", "{I:AGL_FT|0} · IL-2: altura sobre el terreno, pies"),
        new("{I:RPM|0}", "{I:RPM|0} · IL-2: RPM del motor 1"),
        new("{I:RPM:2|0}", "{I:RPM:2|0} · IL-2: RPM del motor 2"),
        new("{I:MANIFOLD|0.00}", "{I:MANIFOLD|0.00} · IL-2: presión de admisión (unidades sin verificar)"),
        new("{I:FLAPS|0.0}", "{I:FLAPS|0.0} · IL-2: flaps 0..1"),
        new("{I:GEAR|0.0}", "{I:GEAR|0.0} · IL-2: tren 0..1"),
    ];

    /// <summary>Los fijos y, con controles de DCS-BIOS, primero los de texto (frecuencias, pantallas) y luego los numéricos.</summary>
    public static IcpFieldOption[] For(IReadOnlyList<RigControl.Core.Bindings.DcsControl> controls)
    {
        if (controls.Count == 0) return Fixed;
        var texts = controls.Where(c => c.TextAddress is not null).DistinctBy(c => c.Name).OrderBy(c => c.Name)
            .Select(c => new IcpFieldOption($"{{D:{c.Name}}}", $"{{D:{c.Name}}} · {c.Description} (texto, {c.TextLength})"));
        var numbers = controls.Where(c => c.Address is not null && c.TextAddress is null).DistinctBy(c => c.Name).OrderBy(c => c.Name)
            .Select(c => new IcpFieldOption($"{{D:{c.Name}}}", $"{{D:{c.Name}}} · {c.Description}"));
        return [.. Fixed.Take(4), .. texts, .. numbers];
    }
}
