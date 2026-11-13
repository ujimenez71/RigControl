using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using RigControl.Core;
using RigControl.Core.Bindings;
using RigControl.Core.WinWing;
using Vortice.DirectInput;

namespace RigControl.ViewModels;

public sealed record ProblemItem(Severity Severity, string Text);

/// <summary>
/// Editor gráfico de un perfil: datos del perfil y bindings (añadir, duplicar, borrar, editar).
/// Valida en vivo con el compilador y el Conflict Guard (lo mismo que "check"). Captura entradas
/// pulsando el mando, ofrece los B: del avión cargado en MSFS y "usar el avión actual".
/// Lo que no edita (capas, luces, timing, condiciones, secuencias) lo conserva tal cual.
/// </summary>
public sealed class ProfileEditorViewModel : ObservableObject, IDisposable
{
    private const int CaptureTimeoutMs = 15000;

    private readonly ProfileFile _original;
    private readonly IntPtr _window;
    private readonly Dictionary<string, string> _devices;
    private readonly DispatcherTimer _validateTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _captureTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _simTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly SimConnectOutput _sim = new();
    private readonly DcsBiosOutput _dcs = new();
    private readonly DeviceCatalog _catalog = DeviceCatalog.Load();

    private IDirectInput8? _di;
    private DeviceMonitor? _monitor;
    private InputCapture? _capture;
    private long _captureStart;
    private List<string> _inputEvents = [];

    private string _name;
    private string _sim0;
    private string _aircraftText;
    private string _notes;
    private BindingEditViewModel? _selected;
    private bool _dirty;
    private bool _isCapturing;
    private string _captureStatus = "";
    private string _status = "";
    private bool _ok = true;
    private string _simStatus = "MSFS: sin conexión";

    public ProfileEditorViewModel(string path, ProfileFile file, IntPtr window)
    {
        Path = path;
        _original = file;
        _window = window;
        _devices = new Dictionary<string, string>(file.Devices, StringComparer.OrdinalIgnoreCase);
        _name = file.Name;
        _sim0 = string.IsNullOrWhiteSpace(file.Sim) ? SimFromFile(file) : file.Sim.Trim().ToLowerInvariant();
        _aircraftText = string.Join(", ", file.Aircraft);
        _notes = file.Notes ?? "";

        foreach (var b in file.Bindings) Add(BindingEditViewModel.FromFile(b));
        Selected = Bindings.FirstOrDefault();
        foreach (var (name, def) in file.Layers) AddLayer(new LayerEditViewModel(name, ConditionEditViewModel.FromFile(def, isLayer: true)));
        foreach (var (led, value) in file.Lights) AddLight(LightFromJson(led, value));
        for (var i = 0; i < IcpLines.Length; i++)
        {
            if (i < file.Icp.Count) IcpLines[i].Text = file.Icp[i];
            IcpLines[i].Changed += () => { Raise(nameof(IcpHeader)); Touch(); };
        }
        RefreshLayerNames();

        _validateTimer.Tick += (_, _) => { _validateTimer.Stop(); Validate(); };
        _captureTimer.Tick += (_, _) => CaptureTick();
        _simTimer.Tick += (_, _) => SimTick();
        _sim.InputEventsReady += events =>
        {
            _inputEvents = [.. events.Keys.Order(StringComparer.OrdinalIgnoreCase)];
            RefreshSuggestions();
        };
        _simTimer.Start();
        Validate();
        Dirty = false;
    }

    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);

    public ObservableCollection<BindingEditViewModel> Bindings { get; } = [];
    public ObservableCollection<ProblemItem> Problems { get; } = [];
    public ObservableCollection<string> Suggestions { get; } = [];
    public ObservableCollection<LayerEditViewModel> Layers { get; } = [];
    public ObservableCollection<LightEditViewModel> Lights { get; } = [];

    /// <summary>Nombres de las capas, para los desplegables de las condiciones.</summary>
    public ObservableCollection<string> LayerNames { get; } = [];

    public string LayersHeader => $"Capas ({Layers.Count})";
    public string LightsHeader => $"Luces ({Lights.Count})";
    public string IcpHeader => $"Pantalla ICP ({IcpLines.Count(l => l.Text.Trim().Length > 0)})";

    /// <summary>Las 5 líneas de la pantalla del ICP ("icp" del perfil).</summary>
    public IcpLineViewModel[] IcpLines { get; } = [.. Enumerable.Range(1, IcpTemplate.MaxLines).Select(n => new IcpLineViewModel(n))];

    /// <summary>Valores que se pueden insertar en una línea del ICP: los fijos y los del avión (DCS-BIOS) o de MSFS.</summary>
    public IcpFieldOption[] IcpFields { get; private set; } = IcpFieldOption.Fixed;
    public string BindingsHeader => $"Bindings ({Bindings.Count})";

    public static ModeOption[] BindingModes => ConditionEditViewModel.BindingModes;
    public static ModeOption[] LayerModes => ConditionEditViewModel.LayerModes;
    public static TermKindOption[] TermKinds => ConditionEditViewModel.Kinds;
    public static string[] LedNames => LightEditViewModel.LedNames;
    public static CompareOption[] Compares => LightTermViewModel.Compares;
    public static string[] CommonVars => LightTermViewModel.CommonVars;

    /// <summary>
    /// Variables sugeridas para las luces: con controles de DCS-BIOS (perfil de DCS o DCS en marcha),
    /// los que tienen valor numérico como "D:CONTROL"; si no, las A: frecuentes de MSFS.
    /// </summary>
    public string[] LightVars { get; private set; } = LightTermViewModel.CommonVars;

    // ------------------------------------------------------------ capas

    public void NewLayer()
    {
        var name = "capa";
        for (var n = 2; Layers.Any(l => l.Name == name); n++) name = $"capa{n}";
        AddLayer(new LayerEditViewModel(name, ConditionEditViewModel.FromFile(null, isLayer: true)));
        RefreshLayerNames();
        Touch();
    }

    public void DeleteLayer(LayerEditViewModel layer)
    {
        Layers.Remove(layer);
        RefreshLayerNames();
        Touch();
    }

    /// <summary>¿Alguna condición (de bindings o de otras capas) usa la capa? Para avisar antes de borrarla.</summary>
    public bool LayerInUse(LayerEditViewModel layer) =>
        Bindings.Any(b => b.Condition.UsesLayer(layer.Name)) || Layers.Any(l => l != layer && l.Condition.UsesLayer(layer.Name));

    private void AddLayer(LayerEditViewModel layer)
    {
        layer.Changed += Touch;
        layer.Renamed += (from, to) =>
        {
            foreach (var b in Bindings) b.Condition.RenameLayer(from, to);
            foreach (var l in Layers) l.Condition.RenameLayer(from, to);
            RefreshLayerNames();
        };
        Layers.Add(layer);
        Raise(nameof(LayersHeader));
    }

    private void RefreshLayerNames()
    {
        // Sin Clear: los desplegables abiertos pierden la selección si la colección se vacía.
        var names = Layers.Select(l => l.Name).ToList();
        for (var i = LayerNames.Count - 1; i >= 0; i--) if (!names.Contains(LayerNames[i])) LayerNames.RemoveAt(i);
        foreach (var n in names) if (!LayerNames.Contains(n)) LayerNames.Add(n);
        Raise(nameof(LayersHeader));
    }

    // ------------------------------------------------------------ luces

    public void NewLight()
    {
        var led = LedNames.FirstOrDefault(n => Lights.All(l => l.Led != n)) ?? LedNames[0];
        var light = new LightEditViewModel(led);
        light.AddTerm();
        AddLight(light);
        Touch();
    }

    public void DeleteLight(LightEditViewModel light)
    {
        Lights.Remove(light);
        Raise(nameof(LightsHeader));
        Touch();
    }

    private void AddLight(LightEditViewModel light)
    {
        light.Changed += Touch;
        Lights.Add(light);
        Raise(nameof(LightsHeader));
    }

    private static LightEditViewModel LightFromJson(string led, System.Text.Json.JsonElement value)
    {
        var light = new LightEditViewModel(led);
        if (value.ValueKind == System.Text.Json.JsonValueKind.String)
            light.Terms.Add(LightTermViewModel.FromText(light, value.GetString()!));
        else if (value.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                light.Terms.Add(LightTermViewModel.FromText(light, item.ValueKind == System.Text.Json.JsonValueKind.String
                    ? item.GetString()! : item.GetRawText()));
        return light;
    }

    private Dictionary<string, System.Text.Json.JsonElement> LightsToJson()
    {
        var result = new Dictionary<string, System.Text.Json.JsonElement>();
        foreach (var l in Lights)
            result[l.Led] = System.Text.Json.JsonSerializer.SerializeToElement(l.ToJsonValue());
        return result;
    }

    public static string[] SimOptions { get; } = ["msfs", "dcs", "general"];
    public static TargetOption[] Targets => BindingEditViewModel.Targets;
    public static GestureOption[] Gestures => BindingEditViewModel.Gestures;

    public string Name { get => _name; set { if (Set(ref _name, value)) Touch(); } }
    public string Sim { get => _sim0; set { if (Set(ref _sim0, value)) Touch(); } }
    public string AircraftText { get => _aircraftText; set { if (Set(ref _aircraftText, value)) Touch(); } }
    public string Notes { get => _notes; set { if (Set(ref _notes, value)) Touch(); } }

    public BindingEditViewModel? Selected
    {
        get => _selected;
        set
        {
            if (_isCapturing) StopCapture("");
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasSelected));
            RefreshSuggestions();
        }
    }

    public bool HasSelected => _selected is not null;

    public bool Dirty { get => _dirty; private set { if (Set(ref _dirty, value)) Raise(nameof(Title)); } }
    public string Title => $"Editando {FileName}{(_dirty ? "  ·  cambios sin guardar" : "")}";

    public bool IsCapturing { get => _isCapturing; private set { if (Set(ref _isCapturing, value)) Raise(nameof(CaptureButtonText)); } }
    public string CaptureButtonText => _isCapturing ? "Cancelar" : "🎯 Capturar";
    public string CaptureStatus { get => _captureStatus; private set => Set(ref _captureStatus, value); }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool Ok { get => _ok; private set => Set(ref _ok, value); }
    public string SimStatus { get => _simStatus; private set => Set(ref _simStatus, value); }

    /// <summary>Se dispara al guardar o descartar: la pantalla Perfiles vuelve a la vista normal.</summary>
    public event Action<bool>? Closed;

    // ------------------------------------------------------------ bindings

    public void AddBinding()
    {
        var b = BindingEditViewModel.FromFile(new BindingFile { Input = "", Gesture = "press", Action = "B:" });
        Add(b);
        Selected = b;
        Touch();
        ToggleCapture(); // lo normal es pulsar el botón que se quiere asignar
    }

    public void DuplicateSelected()
    {
        if (_selected is null) return;
        var copy = _selected.Clone();
        Add(copy, Bindings.IndexOf(_selected) + 1);
        Selected = copy;
        Touch();
    }

    public void DeleteSelected()
    {
        if (_selected is null) return;
        var i = Bindings.IndexOf(_selected);
        _selected.Changed -= Touch;
        Bindings.RemoveAt(i);
        Selected = Bindings.Count == 0 ? null : Bindings[Math.Min(i, Bindings.Count - 1)];
        Touch();
    }

    private void Add(BindingEditViewModel b, int index = -1)
    {
        b.Changed += Touch;
        b.DcsControls = _dcsControls;
        b.PropertyChanged += (_, e) =>
        {
            if (ReferenceEquals(b, _selected) && e.PropertyName is nameof(BindingEditViewModel.Name) or nameof(BindingEditViewModel.Target))
                RefreshSuggestions();
        };
        if (index < 0 || index > Bindings.Count) Bindings.Add(b);
        else Bindings.Insert(index, b);
    }

    /// <summary>Elige una sugerencia: en las de DCS va "NOMBRE · descripción" y se queda solo el nombre.</summary>
    public void PickSuggestion(string text)
    {
        if (_selected is null) return;
        var dot = text.IndexOf(" · ", StringComparison.Ordinal);
        _selected.Name = dot > 0 ? text[..dot] : text;
    }

    // ------------------------------------------------------------ controles de DCS-BIOS

    private IReadOnlyList<DcsControl> _dcsControls = [];
    private string? _dcsControlsFor;

    /// <summary>
    /// Avión de DCS cuyos controles se sugieren: el primero "dcs:..." del perfil o, si no hay, el
    /// cargado ahora en DCS. Se recarga al cambiar.
    /// </summary>
    private void RefreshDcsControls()
    {
        var aircraft = AircraftList().FirstOrDefault(a => a.StartsWith("dcs:", StringComparison.OrdinalIgnoreCase))
                       ?? (_dcs.Aircraft is { } live ? "dcs:" + live : null);
        if (string.Equals(aircraft, _dcsControlsFor, StringComparison.OrdinalIgnoreCase)) return;
        _dcsControlsFor = aircraft;
        _dcsControls = aircraft is null ? [] : DcsBiosCatalog.ForAircraft(aircraft);
        foreach (var b in Bindings) b.DcsControls = _dcsControls;
        var dcsVars = _dcsControls.Where(c => c.Address is not null).Select(c => "D:" + c.Name).Distinct().Order().ToArray();
        LightVars = dcsVars.Length > 0 ? dcsVars : LightTermViewModel.CommonVars;
        Raise(nameof(LightVars));
        IcpFields = IcpFieldOption.For(_dcsControls);
        Raise(nameof(IcpFields));
        RefreshSuggestions();
    }

    /// <summary>Sugerencias para el nombre: B: del avión cargado, K: frecuentes o controles de DCS-BIOS, filtradas por lo escrito.</summary>
    private void RefreshSuggestions()
    {
        Raise(nameof(DcsValueHelp));
        Suggestions.Clear();
        if (_selected is null) return;
        var filter = _selected.Name;
        if (_selected.Target == ActionTarget.Dcs)
        {
            // Busca en el nombre y en la descripción ("anti-col" encuentra PLT_EXTL_ACOL_L_SW).
            foreach (var c in _dcsControls.Where(c => c.Inputs.Length > 0 && (filter.Length == 0
                         || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || c.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))).Take(200))
                if (!c.Name.Equals(filter, StringComparison.OrdinalIgnoreCase)) Suggestions.Add(c.ToString());
            return;
        }
        IEnumerable<string> source = _selected.Target switch
        {
            ActionTarget.Cabin => _inputEvents,
            ActionTarget.Event => ActionDescriber.CommonSimEvents,
            _ => [],
        };
        foreach (var s in source.Where(s => filter.Length == 0 || s.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(200))
            if (!s.Equals(filter, StringComparison.OrdinalIgnoreCase)) Suggestions.Add(s);
    }

    /// <summary>Posiciones del control de DCS elegido ("0 = RED, 1 = OFF, 2 = WHT"), para la ayuda del valor.</summary>
    public string DcsValueHelp
    {
        get
        {
            if (_selected is not { Target: ActionTarget.Dcs }) return "";
            var c = _dcsControls.FirstOrDefault(c => c.Name.Equals(_selected.Name, StringComparison.OrdinalIgnoreCase));
            if (c is null) return _dcsControls.Count == 0 ? "Sin definiciones de DCS-BIOS para este avión (añade \"dcs:avión\" en Aviones o carga el avión en DCS)." : "";
            var positions = c.Positions.Length > 0
                ? "Posiciones: " + string.Join(", ", c.Positions.Select((p, i) => $"{i} = {p}"))
                : c.MaxValue > 0 ? $"Valores de 0 a {c.MaxValue}." : "";
            return $"{c.Description}. {positions} Admite: {string.Join(", ", c.Inputs)}.";
        }
    }

    // ------------------------------------------------------------ captura de entradas

    /// <summary>Captura la entrada del binding seleccionado (o cancela la captura en curso).</summary>
    public void ToggleCapture()
    {
        if (_isCapturing) { StopCapture("Cancelado."); return; }
        if (_selected is null || _selected.IsSequence) return;
        var target = _selected;
        StartCapture(v => target.Input = v);
    }

    /// <summary>Captura la entrada de una pieza de condición (un modificador, p. ej.).</summary>
    public void CaptureInto(ConditionTermViewModel term)
    {
        if (_isCapturing) { StopCapture("Cancelado."); return; }
        StartCapture(v => term.Input = v);
    }

    private Action<string>? _captureApply;

    private void StartCapture(Action<string> apply)
    {
        _captureApply = apply;
        try
        {
            _di ??= DInput.DirectInput8Create();
            _monitor ??= DeviceMonitor.Open(_di, _window);
        }
        catch (Exception ex)
        {
            CaptureStatus = $"No se pueden abrir los mandos: {ex.Message}";
            return;
        }
        _capture = new InputCapture(_monitor);
        _captureStart = _clock.ElapsedMilliseconds;
        IsCapturing = true;
        CaptureStatus = "Preparando los mandos…";
        _captureTimer.Start();
    }

    private void CaptureTick()
    {
        var now = _clock.ElapsedMilliseconds;
        var got = _capture!.Poll(now);
        if (_capture.Ready && CaptureStatus.StartsWith("Preparando", StringComparison.Ordinal))
            CaptureStatus = "Pulsa el botón, mueve el hat o lleva un eje a un extremo…";
        if (got is null)
        {
            if (now - _captureStart > CaptureTimeoutMs) StopCapture("No se ha pulsado nada (15 s).");
            return;
        }

        var alias = AliasFor(got);
        _captureApply?.Invoke($"{alias}:{got.Part}");
        StopCapture($"Capturado: {got.DeviceName} · {_catalog.Devices.FirstOrDefault(d => SameKey(d.Key, got.DeviceKey))?.InputLabel(got.Part) ?? got.Part}");
    }

    private void StopCapture(string message)
    {
        _captureTimer.Stop();
        _capture = null;
        _captureApply = null;
        IsCapturing = false;
        CaptureStatus = message;
    }

    /// <summary>Alias del dispositivo en este perfil; si no está, lo añade con un alias corto sacado del catálogo.</summary>
    private string AliasFor(CapturedInput got)
    {
        foreach (var (alias, key) in _devices)
            if (SameKey(key, got.DeviceKey) || SameKey(got.DeviceKey, key)) return alias;

        var baseName = new string(got.DeviceName.TakeWhile(c => c != ' ').Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (baseName.Length == 0) baseName = "mando";
        var name = baseName;
        for (var n = 2; _devices.ContainsKey(name); n++) name = $"{baseName}{n}";
        _devices[name] = got.DeviceKey;
        Touch();
        return name;
    }

    private static bool SameKey(string full, string key) =>
        full.Equals(key, StringComparison.OrdinalIgnoreCase) || full.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------ simulador

    private void SimTick()
    {
        var now = _clock.ElapsedMilliseconds;
        _sim.Tick(now);
        _dcs.Tick(now);
        SimStatus = _dcs.IsActive ? $"DCS: {_dcs.Aircraft ?? "sin avión"}"
            : !_sim.IsConnected ? "MSFS: sin conexión (la lista de controles B: necesita MSFS con un avión cargado)"
            : $"MSFS: {AircraftFolder(_sim.Aircraft) ?? "sin avión"} · {_inputEvents.Count} controles B:";
        if (_dcs.IsActive) SimStatus += $" · {_dcsControls.Count} controles de DCS-BIOS";
        RefreshDcsControls();
        UpdateLiveLights();
        UpdateIcpPreview();
    }

    /// <summary>Añade el avión cargado ahora (MSFS o DCS) a la lista de aviones del perfil.</summary>
    public void UseCurrentAircraft()
    {
        var current = _dcs.IsActive && _dcs.Aircraft is not null ? $"dcs:{_dcs.Aircraft.ToLowerInvariant()}"
            : AircraftFolder(_sim.Aircraft);
        if (current is null)
        {
            Status = "No hay ningún avión cargado en MSFS ni en DCS.";
            return;
        }
        var list = AircraftList();
        if (!list.Contains(current, StringComparer.OrdinalIgnoreCase)) list.Add(current);
        AircraftText = string.Join(", ", list);
        if (current.StartsWith("dcs:", StringComparison.Ordinal)) Sim = "dcs";
    }

    /// <summary>"simobjects\airplanes\asobo_c172sp_g1000\..." -> "asobo_c172sp_g1000".</summary>
    private static string? AircraftFolder(string? path)
    {
        if (path is null) return null;
        var parts = SimConnectOutput.NormalizeAircraft(path).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[2] : parts.LastOrDefault();
    }

    private List<string> AircraftList() =>
        [.. _aircraftText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    // ------------------------------------------------------------ validar y guardar

    private void Touch()
    {
        Dirty = true;
        _validateTimer.Stop();
        _validateTimer.Start();
    }

    public ProfileFile BuildFile() => new()
    {
        Name = _name.Trim(),
        Sim = _sim0,
        Notes = string.IsNullOrWhiteSpace(_notes) ? null : _notes.Trim(),
        Timing = _original.Timing,
        Aircraft = AircraftList(),
        AxisHysteresis = _original.AxisHysteresis,
        Devices = UsedDevices(),
        Layers = Layers.Where(l => l.Name.Length > 0).GroupBy(l => l.Name)
                       .ToDictionary(g => g.Key, g => g.First().Condition.ToFile() ?? new ConditionFile()),
        Bindings = [.. Bindings.Select(b => b.ToFile())],
        Lights = LightsToJson(),
        Icp = IcpToList(),
    };

    /// <summary>Las líneas del ICP hasta la última con texto (las vacías de en medio se quedan: son líneas en blanco).</summary>
    private List<string> IcpToList()
    {
        var last = Array.FindLastIndex(IcpLines, l => l.Text.Trim().Length > 0);
        return [.. IcpLines.Take(last + 1).Select(l => l.Text.TrimEnd())];
    }

    /// <summary>Los dispositivos del perfil, menos los añadidos por captura que ya no usa nadie.</summary>
    private Dictionary<string, string> UsedDevices()
    {
        // Entradas usadas: las de los bindings y las de las condiciones (de bindings y de capas).
        var inputs = Bindings.Select(b => b.Input)
            .Concat(Bindings.SelectMany(b => b.Condition.Terms).Select(t => t.Input))
            .Concat(Layers.SelectMany(l => l.Condition.Terms).Select(t => t.Input)).ToList();
        var used = new Dictionary<string, string>(_devices, StringComparer.OrdinalIgnoreCase);
        foreach (var alias in _devices.Keys.Where(a => !_original.Devices.ContainsKey(a)))
            if (!inputs.Any(i => i.StartsWith(alias + ":", StringComparison.OrdinalIgnoreCase)))
                used.Remove(alias);
        return used;
    }

    // ------------------------------------------------------------ vista en vivo de las luces

    private CompiledProfile? _liveProfile;
    private string _liveKey = "";

    /// <summary>
    /// Con el perfil ya compilado, pide a MSFS las variables de las luces (lo mismo que hará la
    /// ejecución) para enseñar en vivo si cada luz se encendería. Solo si cambian las reglas.
    /// </summary>
    private void PrepareLiveLights(ProfileFile file)
    {
        // Las líneas del ICP con error se dejan en blanco para que las demás se vean igual en la vista previa.
        var icp = file.Icp.Select(l => IcpTemplate.TryParse(l, _ => 0, out _, out _) ? l : "").ToList();
        var key = System.Text.Json.JsonSerializer.Serialize(file.Lights) + System.Text.Json.JsonSerializer.Serialize(icp);
        if (key == _liveKey) return;
        _liveKey = key;
        ProfileFile Live(Dictionary<string, System.Text.Json.JsonElement> lights) => new()
        {
            Name = "en vivo", Lights = lights, Icp = icp, Sim = file.Sim, Aircraft = file.Aircraft, // D: se buscan en el avión del perfil
        };
        var (profile, _) = ProfileCompiler.Compile(Live(file.Lights));
        profile ??= ProfileCompiler.Compile(Live([])).Profile; // luces con errores: al menos la vista previa del ICP
        _liveProfile = profile;
        if (profile is null) return;
        _sim.Prepare(profile);
        _dcs.Prepare(profile);
        _liveValues = new double[profile.WatchedVars.Length];
    }

    private double[] _liveValues = [];

    private void UpdateLiveLights()
    {
        // A:/L: de MSFS y D: de DCS-BIOS, por el índice de WatchedVars (como ActionDispatcher.Values).
        var values = _liveValues;
        var fromSim = _sim.Values;
        for (var i = 0; i < values.Length; i++) values[i] = i < fromSim.Length ? fromSim[i] : double.NaN;
        _dcs.Fill(values);
        var connected = _sim.IsConnected || _dcs.HasState;
        foreach (var light in Lights)
        {
            var rule = _liveProfile?.Lights.FirstOrDefault(r => r.Name == light.Led);
            if (rule is null || !connected || values.Length == 0)
            {
                light.LiveOn = null;
                light.LiveState = connected ? "" : "sin simulador";
                foreach (var t in light.Terms) t.Live = "";
                continue;
            }
            var on = rule.Evaluate(values);
            light.LiveOn = on;
            light.LiveState = on ? "● encendida ahora" : "○ apagada ahora";
            for (var i = 0; i < light.Terms.Count && i < rule.Conditions.Length; i++)
            {
                var c = rule.Conditions[i];
                light.Terms[i].Live = $"ahora: {LightEditViewModel.Format(values[c.Var])} {(c.Evaluate(values) ? "✓" : "✗")}";
            }
        }
    }

    private readonly char[] _icpCells = new char[IcpFont.Columns];
    private readonly bool[] _icpInverted = new bool[IcpFont.Columns];

    /// <summary>Vista previa del ICP con los valores de ahora (sin simulador: el texto fijo, la hora y el perfil).</summary>
    private void UpdateIcpPreview()
    {
        var lines = _liveProfile?.Icp ?? [];
        var aircraft = _dcs.HasState && _dcs.Aircraft is { } d ? d : AircraftFolder(_sim.Aircraft) ?? "";
        var ctx = new IcpContext(_liveValues, _dcs.ReadForDisplay, _name.Trim(), aircraft, DateTime.Now);
        for (var i = 0; i < IcpLines.Length; i++)
        {
            if (i < lines.Length) IcpTemplate.Render(lines[i], ctx, _icpCells, _icpInverted);
            else { Array.Fill(_icpCells, ' '); Array.Clear(_icpInverted); }
            IcpLines[i].SetPreview(_icpCells, _icpInverted);
        }
    }

    /// <summary>Compila el perfil en memoria y reparte el resultado: textos de cada fila y lista de problemas.</summary>
    public void Validate()
    {
        var file = BuildFile();
        var report = ProfileInspector.Inspect(file, Path, _catalog);
        Problems.Clear();
        // Lo que el JSON no puede ni representar (claves repetidas): se detecta aquí.
        foreach (var dup in Layers.GroupBy(l => l.Name).Where(g => g.Count() > 1 || g.Key.Length == 0))
            report.Errors.Add(dup.Key.Length == 0 ? "hay una capa sin nombre." : $"capa \"{dup.Key}\" repetida.");
        foreach (var dup in Lights.GroupBy(l => l.Led).Where(g => g.Count() > 1))
            report.Errors.Add($"lights.{dup.Key}: la luz está dos veces; junta sus condiciones en una.");
        foreach (var e in report.Errors) Problems.Add(new ProblemItem(Severity.Error, e));
        foreach (var f in report.Findings) Problems.Add(new ProblemItem(f.Severity, f.Message));

        for (var i = 0; i < Bindings.Count; i++)
        {
            var b = Bindings[i];
            var row = report.Bindings.Count == Bindings.Count ? report.Bindings[i] : null;
            b.InputText = row?.InputText ?? (b.IsSequence ? b.SequenceText : b.Input.Length == 0 ? "(sin entrada)" : b.Input);
            b.KindText = Targets.First(t => t.Value == b.Target).Label;
            b.ConditionText = row?.Condition ?? "";
            var prefix = $"binding #{i + 1}:";
            b.Error = string.Join("\n", report.Errors.Where(e => e.StartsWith(prefix, StringComparison.Ordinal))
                                                      .Select(e => e[prefix.Length..].Trim()));
        }

        // Errores de cada luz, junto a ella.
        foreach (var light in Lights)
            light.Error = string.Join("\n", report.Errors.Where(e => e.StartsWith($"lights.{light.Led}", StringComparison.OrdinalIgnoreCase))
                                                          .Select(e => e[(e.IndexOf(':') + 1)..].Trim()));
        // Errores de cada línea del ICP, junto a ella.
        foreach (var line in IcpLines)
            line.Error = string.Join("\n", report.Errors.Where(e => e.StartsWith($"icp, línea {line.Number}:", StringComparison.Ordinal))
                                                           .Select(e => e[(e.IndexOf(':') + 1)..].Trim()));
        PrepareLiveLights(file);
        Raise(nameof(BindingsHeader));

        Ok = report.Ok;
        var errors = report.Errors.Count + report.Findings.Count(f => f.Severity == Severity.Error);
        Status = errors == 0 ? $"Sin errores · {Bindings.Count} bindings"
            : $"{errors} error(es): se puede guardar, pero la ejecución no usará el perfil hasta corregirlos";
    }

    public void Save()
    {
        Validate();
        BuildFile().Save(Path);
        Dirty = false;
        Closed?.Invoke(true);
    }

    public void Discard() => Closed?.Invoke(false);

    private static string SimFromFile(ProfileFile f) => ProfileCompiler.ResolveSim(f, out _) switch
    {
        SimKind.Msfs => "msfs",
        SimKind.Dcs => "dcs",
        _ => "general",
    };

    public void Dispose()
    {
        _captureTimer.Stop();
        _simTimer.Stop();
        _validateTimer.Stop();
        _monitor?.Dispose();
        _di?.Dispose();
        _sim.Dispose();
        _dcs.Dispose();
    }
}
