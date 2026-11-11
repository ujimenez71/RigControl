using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using RigControl.Core.Hangar;
using RigControl.Core.Mods;

namespace RigControl.ViewModels;

/// <summary>Una opción del desplegable de prioridad; null = no tocarla.</summary>
public sealed record PriorityOption(HangarPriority? Value, string Text)
{
    public static IReadOnlyList<PriorityOption> All { get; } =
    [
        new(null, "Sin cambiar"),
        .. Enum.GetValues<HangarPriority>().Select(p => new PriorityOption(p, HangarRunner.PriorityText(p))),
    ];

    public static PriorityOption Of(HangarPriority? value) => All.First(o => o.Value == value);

    public override string ToString() => Text;
}

/// <summary>Una opción del desplegable de estado de la ventana; null = no tocarlo.</summary>
public sealed record WindowStateOption(HangarWindowState? Value, string Text)
{
    public static IReadOnlyList<WindowStateOption> All { get; } =
    [
        new(null, "Sin cambiar"),
        new(HangarWindowState.Normal, "Normal"),
        new(HangarWindowState.Minimized, "Minimizada"),
        new(HangarWindowState.Maximized, "Maximizada"),
    ];

    public static WindowStateOption Of(HangarWindowState? value) => All.First(o => o.Value == value);

    public override string ToString() => Text;
}

/// <summary>Una opción del desplegable "Siempre encima"; null = no tocarlo.</summary>
public sealed record TopmostOption(bool? Value, string Text)
{
    public static IReadOnlyList<TopmostOption> All { get; } = [new(null, "Sin cambiar"), new(true, "Sí"), new(false, "No (quitarlo)")];

    public static TopmostOption Of(bool? value) => All.First(o => o.Value == value);

    public override string ToString() => Text;
}

/// <summary>Una opción del desplegable de conjunto de mods del hangar; null = no tocar los mods.</summary>
public sealed record ModSetOption(string? Value, bool Missing = false)
{
    public string Text => Value is null ? "Sin cambiar" : Missing ? $"{Value} (no existe en mods.json)" : Value;

    /// <summary>Iguales por el nombre del conjunto (así el desplegable reconoce la opción elegida).</summary>
    public bool Equals(ModSetOption? other) => other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => Value is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public override string ToString() => Text;
}

/// <summary>Un programa de la secuencia, editable y con su estado.</summary>
public sealed class HangarStepItem(HangarStep step, Action changed) : ObservableObject
{
    private string _name = step.Name;
    private string _target = step.Target;
    private string _args = step.Args ?? "";
    private string _process = step.Process ?? "";
    private double _waitSeconds = step.WaitSeconds;
    private double _timeoutSeconds = step.TimeoutSeconds;
    private bool _enabled = step.Enabled;
    private PriorityOption _priority = PriorityOption.Of(step.Priority);
    private WindowStateOption _windowState = WindowStateOption.Of(step.Window?.State);
    private TopmostOption _topmost = TopmostOption.Of(step.Window?.Topmost);
    private bool _placePosition = step.Window?.HasPosition == true;
    private int _x = step.Window?.X ?? 0;
    private int _y = step.Window?.Y ?? 0;
    private bool _placeSize = step.Window?.HasSize == true;
    private int _width = step.Window?.Width ?? 1280;
    private int _height = step.Window?.Height ?? 720;
    private bool _focus = step.Focus;
    private HangarPresence _presence = HangarPresence.Unknown;
    private string _runText = "";
    private bool _runProblem;

    public string Name { get => _name; set => Edit(ref _name, value); }
    public string Target { get => _target; set => Edit(ref _target, value); }
    public string Args { get => _args; set => Edit(ref _args, value); }
    public string Process { get => _process; set => Edit(ref _process, value); }
    public double WaitSeconds { get => _waitSeconds; set => Edit(ref _waitSeconds, Math.Max(0, value)); }
    public double TimeoutSeconds { get => _timeoutSeconds; set => Edit(ref _timeoutSeconds, Math.Max(0, value)); }
    public bool Enabled { get => _enabled; set => Edit(ref _enabled, value); }
    public PriorityOption Priority { get => _priority; set => Edit(ref _priority, value ?? PriorityOption.All[0]); }
    public WindowStateOption WindowState { get => _windowState; set => Edit(ref _windowState, value ?? WindowStateOption.All[0]); }
    public TopmostOption Topmost { get => _topmost; set => Edit(ref _topmost, value ?? TopmostOption.All[0]); }
    public bool PlacePosition { get => _placePosition; set => Edit(ref _placePosition, value); }
    public int X { get => _x; set => Edit(ref _x, value); }
    public int Y { get => _y; set => Edit(ref _y, value); }
    public bool PlaceSize { get => _placeSize; set => Edit(ref _placeSize, value); }
    public int Width { get => _width; set => Edit(ref _width, Math.Max(1, value)); }
    public int Height { get => _height; set => Edit(ref _height, Math.Max(1, value)); }

    /// <summary>Traer al frente al terminar. Solo uno por hangar: lo garantiza HangarItem.</summary>
    public bool Focus { get => _focus; set => Edit(ref _focus, value); }

    public HangarPresence Presence
    {
        get => _presence;
        set { if (Set(ref _presence, value)) Raise(nameof(PresenceText)); }
    }

    public string PresenceText => _presence switch
    {
        HangarPresence.Open => "Abierto",
        HangarPresence.Closed => "Cerrado",
        HangarPresence.Missing => "No se encuentra el programa",
        _ => "Sin proceso indicado: no se sabe si está abierto",
    };

    /// <summary>Resultado de la última secuencia.</summary>
    public string RunText { get => _runText; private set => Set(ref _runText, value); }
    public bool RunProblem { get => _runProblem; private set => Set(ref _runProblem, value); }

    public void SetRun(HangarStepState state, string detail)
    {
        RunText = detail;
        RunProblem = state is HangarStepState.Failed or HangarStepState.TimedOut or HangarStepState.Warning;
    }

    public void ClearRun() { RunText = ""; RunProblem = false; }

    public HangarStep ToModel() => new()
    {
        Name = Name,
        Target = Target,
        Args = string.IsNullOrWhiteSpace(Args) ? null : Args,
        Process = string.IsNullOrWhiteSpace(Process) ? null : Process,
        WaitSeconds = WaitSeconds,
        TimeoutSeconds = TimeoutSeconds,
        Enabled = Enabled,
        Priority = Priority.Value,
        Window = ToWindow(),
        Focus = Focus,
    };

    private HangarWindow? ToWindow()
    {
        var window = new HangarWindow
        {
            State = WindowState.Value,
            Topmost = Topmost.Value,
            X = PlacePosition ? X : null,
            Y = PlacePosition ? Y : null,
            Width = PlaceSize ? Width : null,
            Height = PlaceSize ? Height : null,
        };
        return window.IsEmpty ? null : window;
    }

    /// <summary>Copia la posición y el tamaño de una ventana abierta (y "maximizada" si lo está).</summary>
    public void UseWindow(WindowInfo info)
    {
        X = info.X;
        Y = info.Y;
        Width = info.Width;
        Height = info.Height;
        PlacePosition = true;
        PlaceSize = true;
        if (info.State == HangarWindowState.Maximized) WindowState = WindowStateOption.Of(HangarWindowState.Maximized);
    }

    private void Edit<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name)) changed();
    }
}

/// <summary>Un hangar: nombre, opción de iniciar la ejecución y su lista de programas.</summary>
public sealed class HangarItem : ObservableObject
{
    private readonly Action _changed;
    private string _name;
    private bool _startRun;
    private string? _modSet;

    public HangarItem(HangarProfile model, Action changed)
    {
        _changed = changed;
        _name = model.Name;
        _startRun = model.StartRun;
        _modSet = string.IsNullOrWhiteSpace(model.ModSet) ? null : model.ModSet;
        Steps.CollectionChanged += (_, e) =>
        {
            foreach (HangarStepItem item in e.NewItems ?? Array.Empty<HangarStepItem>()) item.PropertyChanged += OnStepChanged;
            foreach (HangarStepItem item in e.OldItems ?? Array.Empty<HangarStepItem>()) item.PropertyChanged -= OnStepChanged;
        };
        foreach (var s in model.Steps) Steps.Add(new HangarStepItem(s, changed));
    }

    /// <summary>Solo un paso puede traerse al frente: marcar uno desmarca los demás.</summary>
    private void OnStepChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HangarStepItem.Focus) || sender is not HangarStepItem { Focus: true } chosen) return;
        foreach (var other in Steps)
            if (other != chosen && other.Focus) other.Focus = false;
    }

    public ObservableCollection<HangarStepItem> Steps { get; } = [];

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) _changed(); }
    }

    public bool StartRun
    {
        get => _startRun;
        set { if (Set(ref _startRun, value)) _changed(); }
    }

    /// <summary>Conjunto de mods que se aplica antes de abrir nada; null = no tocarlos.</summary>
    public string? ModSet
    {
        get => _modSet;
        set
        {
            if (!Set(ref _modSet, value)) return;
            Raise(nameof(ModSetChoice));
            _changed();
        }
    }

    /// <summary>Para el desplegable: el valor envuelto (null = "Sin cambiar").</summary>
    public ModSetOption ModSetChoice
    {
        get => new(_modSet);
        set { if (value is not null) ModSet = value.Value; } // null: el desplegable se está vaciando
    }

    /// <summary>Tras rehacer la lista de opciones, para que el desplegable vuelva a marcar la elegida.</summary>
    public void RefreshChoice() => Raise(nameof(ModSetChoice));

    public HangarProfile ToModel() => new()
    {
        Name = Name,
        StartRun = StartRun,
        ModSet = ModSet,
        Steps = [.. Steps.Select(s => s.ToModel())],
    };
}

/// <summary>
/// Pantalla Hangar: secuencias de programas de hangar.json. Se guarda sola al editar. Si el
/// archivo está roto se muestra el error y no se toca hasta que se corrija a mano.
/// </summary>
public sealed class HangarViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _presenceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HangarRunner _inspector = new();
    private CancellationTokenSource? _cts;
    private HangarRunner? _runner; // el último: puede seguir vigilando prioridades tras la secuencia
    private DateTime _fileTime;
    private bool _loading;
    private bool _inspecting;

    private HangarItem? _selectedHangar;
    private HangarStepItem? _selectedStep;
    private bool _isBusy;
    private bool _fileOk = true;
    private string _message = "";

    public HangarViewModel()
    {
        _saveTimer.Tick += (_, _) => Save();
        _presenceTimer.Tick += async (_, _) =>
        {
            ReloadIfChangedOnDisk();
            await RefreshPresenceAsync();
        };
        Load();
    }

    /// <summary>La secuencia terminó (sin cancelar) y el hangar pide iniciar la ejecución.</summary>
    public event Action? StartRunRequested;

    public ObservableCollection<HangarItem> Hangars { get; } = [];

    /// <summary>"Sin cambiar" + los conjuntos de mods.json (+ el del hangar elegido si ya no existe).</summary>
    public ObservableCollection<ModSetOption> ModSetOptions { get; } = [];

    public HangarItem? SelectedHangar
    {
        get => _selectedHangar;
        set
        {
            if (!Set(ref _selectedHangar, value)) return;
            SelectedStep = value?.Steps.FirstOrDefault();
            RefreshModSets();
            Raise(nameof(HasHangar));
            Raise(nameof(CanLaunch));
            _ = RefreshPresenceAsync();
        }
    }

    public HangarStepItem? SelectedStep
    {
        get => _selectedStep;
        set { if (Set(ref _selectedStep, value)) Raise(nameof(HasStep)); }
    }

    public bool HasHangar => _selectedHangar is not null;
    public bool HasStep => _selectedStep is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            Raise(nameof(CanEdit));
            Raise(nameof(CanLaunch));
            Raise(nameof(LaunchText));
        }
    }

    /// <summary>No se edita durante una secuencia (los índices de los pasos deben seguir valiendo) ni con el archivo roto.</summary>
    public bool CanEdit => _fileOk && !_isBusy;
    public bool CanLaunch => _isBusy || _fileOk && _selectedHangar is not null;
    public string LaunchText => _isBusy ? "■  Cancelar" : "▶  Abrir hangar";
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string FilePath => HangarFile.DefaultPath;

    public void SetVisible(bool visible)
    {
        if (visible)
        {
            ReloadIfChangedOnDisk();
            RefreshModSets();
            _presenceTimer.Start();
            _ = RefreshPresenceAsync();
        }
        else _presenceTimer.Stop();
    }

    public void AddHangar()
    {
        var item = new HangarItem(new HangarProfile { Name = "Nuevo hangar" }, MarkDirty);
        Hangars.Add(item);
        SelectedHangar = item;
        MarkDirty();
    }

    public void DuplicateHangar()
    {
        if (SelectedHangar is null) return;
        var model = SelectedHangar.ToModel();
        model.Name += " (copia)";
        var item = new HangarItem(model, MarkDirty);
        Hangars.Add(item);
        SelectedHangar = item;
        MarkDirty();
    }

    public void RemoveHangar()
    {
        if (SelectedHangar is null) return;
        var index = Hangars.IndexOf(SelectedHangar);
        Hangars.RemoveAt(index);
        SelectedHangar = Hangars.Count == 0 ? null : Hangars[Math.Min(index, Hangars.Count - 1)];
        MarkDirty();
    }

    public void AddStep()
    {
        if (SelectedHangar is null) return;
        var step = new HangarStepItem(new HangarStep { Name = "Nuevo programa" }, MarkDirty);
        SelectedHangar.Steps.Add(step);
        SelectedStep = step;
        MarkDirty();
    }

    public void RemoveStep()
    {
        if (SelectedHangar is null || SelectedStep is null) return;
        var steps = SelectedHangar.Steps;
        var index = steps.IndexOf(SelectedStep);
        steps.RemoveAt(index);
        SelectedStep = steps.Count == 0 ? null : steps[Math.Min(index, steps.Count - 1)];
        MarkDirty();
    }

    public void MoveStep(int delta)
    {
        if (SelectedHangar is null || SelectedStep is null) return;
        var steps = SelectedHangar.Steps;
        var from = steps.IndexOf(SelectedStep);
        var to = from + delta;
        if (to < 0 || to >= steps.Count) return;
        steps.Move(from, to);
        MarkDirty();
    }

    /// <summary>Tras elegir un programa con el diálogo: pone la ruta y, si no tenía nombre, lo deduce.</summary>
    public void SetSelectedProgram(string path)
    {
        if (SelectedStep is not { } step) return;
        step.Target = path;
        step.Process = "";
        if (step.Name is "" or "Nuevo programa") step.Name = Path.GetFileNameWithoutExtension(path);
        _ = RefreshPresenceAsync();
    }

    /// <summary>"Usar la posición actual": la de la ventana principal abierta del programa elegido.</summary>
    public async void CaptureWindow()
    {
        if (SelectedStep is not { } step) return;
        var model = step.ToModel();
        var process = HangarRunner.ProcessNameOf(model);
        if (process is null)
        {
            Message = "Para copiar la posición hace falta el proceso (se deduce del .exe; en las URI hay que escribirlo).";
            return;
        }
        var info = await Task.Run(() =>
        {
            var host = new ProcessHost();
            var windows = new WindowHost();
            foreach (var id in host.FindProcesses(process))
                foreach (var hwnd in windows.MainWindows(id))
                    if (windows.Describe(hwnd) is { } found) return found;
            return null;
        });
        if (info is null)
        {
            Message = $"No hay ninguna ventana visible de {process}: ábrelo, ponlo donde lo quieras y vuelve a pulsar.";
            return;
        }
        step.UseWindow(info);
        Message = $"Copiada la ventana de {step.Name}: en {info.X},{info.Y} de {info.Width}×{info.Height}" +
                  (info.State == HangarWindowState.Maximized ? ", maximizada." : ".");
    }

    public async void LaunchOrCancel()
    {
        if (IsBusy)
        {
            _cts?.Cancel();
            return;
        }
        ReloadIfChangedOnDisk(); // no pisar con la copia en memoria lo editado a mano
        if (SelectedHangar is not { } hangar) return;

        Save(); // lo que se lanza es lo que se ve
        foreach (var s in hangar.Steps) s.ClearRun();
        var items = hangar.Steps.ToArray();
        var model = hangar.ToModel();
        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        var modsText = "";
        if (model.ModSet is { } setName)
        {
            Message = $"Aplicando los mods «{setName}»…";
            modsText = await Task.Run(() => ApplyMods(setName));
        }
        Message = $"{modsText}Abriendo {hangar.Name}…";

        _runner?.StopWatching();
        var runner = _runner = new HangarRunner();
        var dispatcher = Dispatcher.CurrentDispatcher;
        runner.StepChanged += (i, state, detail) => dispatcher.BeginInvoke(() => items[i].SetRun(state, detail));
        try
        {
            var outcome = await Task.Run(() => runner.RunAsync(model, cts.Token));
            Message = modsText + outcome switch
            {
                HangarOutcome.Ok => "Secuencia completa.",
                HangarOutcome.Cancelled => "Secuencia cancelada. Lo que ya se había abierto sigue abierto.",
                _ => "Secuencia terminada con problemas: mira los pasos en rojo.",
            };
            if (outcome != HangarOutcome.Cancelled && model.StartRun)
            {
                StartRunRequested?.Invoke();
                Message += " Se ha pedido la ejecución automática de perfiles: el resultado está en la pantalla Ejecución.";
            }
        }
        catch (Exception ex)
        {
            Message = $"Error en la secuencia: {ex.Message}";
        }
        finally
        {
            _cts = null;
            IsBusy = false;
        }
        await RefreshPresenceAsync();
    }

    /// <summary>Aplica el conjunto de mods del hangar; devuelve el resumen para el mensaje. Un fallo no detiene la secuencia.</summary>
    private static string ApplyMods(string setName)
    {
        try
        {
            var file = ModsFile.Load();
            if (file.FindSet(setName) is not { } set) return $"Mods: no existe el conjunto «{setName}», no se tocan. ";
            var changes = new ModManager().ApplySet(file, set);
            var errors = changes.Where(c => c.Error is not null).ToList();
            if (changes.Count == 0) return $"Mods «{set.Name}»: ya estaban así. ";
            return errors.Count == 0
                ? $"Mods «{set.Name}»: {changes.Count} cambio(s). "
                : $"Mods «{set.Name}» con problemas: {string.Join("; ", errors)}. ";
        }
        catch (Exception ex)
        {
            return $"Mods: {ex.Message}. ";
        }
    }

    private void RefreshModSets()
    {
        var names = new List<string>();
        try { names.AddRange(ModsFile.Load().Sets.Select(s => s.Name)); }
        catch (Exception) { /* mods.json roto: lo dice la pantalla Mods */ }
        var options = new List<ModSetOption> { new(null) };
        options.AddRange(names.Select(n => new ModSetOption(n)));
        if (SelectedHangar?.ModSet is { } current && !names.Contains(current, StringComparer.OrdinalIgnoreCase))
            options.Add(new ModSetOption(current, Missing: true));
        if (options.SequenceEqual(ModSetOptions) && options.Select(o => o.Text).SequenceEqual(ModSetOptions.Select(o => o.Text))) return;
        var keep = SelectedHangar;
        ModSetOptions.Clear();
        foreach (var o in options) ModSetOptions.Add(o);
        keep?.RefreshChoice();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _runner?.StopWatching();
        _presenceTimer.Stop();
        if (_saveTimer.IsEnabled) Save();
        _saveTimer.Stop();
    }

    private void MarkDirty()
    {
        if (_loading || !_fileOk) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        _saveTimer.Stop();
        if (!_fileOk) return;
        try
        {
            new HangarFile { Hangars = [.. Hangars.Select(h => h.ToModel())] }.Save();
            _fileTime = File.GetLastWriteTimeUtc(HangarFile.DefaultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"No se pudo guardar: {ex.Message}";
        }
    }

    private void Load()
    {
        var selectedName = SelectedHangar?.Name;
        _loading = true;
        try
        {
            var file = HangarFile.Load();
            _fileTime = File.Exists(HangarFile.DefaultPath) ? File.GetLastWriteTimeUtc(HangarFile.DefaultPath) : default;
            SelectedHangar = null;
            Hangars.Clear();
            foreach (var h in file.Hangars) Hangars.Add(new HangarItem(h, MarkDirty));
            SelectedHangar = Hangars.FirstOrDefault(h => h.Name == selectedName) ?? Hangars.FirstOrDefault();
            _fileOk = true;
            Message = "";
        }
        catch (Exception ex)
        {
            _fileOk = false;
            _fileTime = File.GetLastWriteTimeUtc(HangarFile.DefaultPath);
            SelectedHangar = null;
            Hangars.Clear();
            Message = $"hangar.json tiene un error; corrígelo a mano (no se sobrescribe): {ex.Message}";
        }
        finally
        {
            _loading = false;
            Raise(nameof(CanEdit));
            Raise(nameof(CanLaunch));
        }
    }

    /// <summary>Por si se ha editado a mano mientras la app estaba abierta.</summary>
    private void ReloadIfChangedOnDisk()
    {
        if (IsBusy || _saveTimer.IsEnabled) return;
        var time = File.Exists(HangarFile.DefaultPath) ? File.GetLastWriteTimeUtc(HangarFile.DefaultPath) : default;
        if (time != _fileTime) Load();
    }

    /// <summary>Fuera del hilo de la interfaz: enumerar procesos o tocar un disco dormido puede tardar.</summary>
    private async Task RefreshPresenceAsync()
    {
        if (_inspecting || SelectedHangar is not { } hangar) return;
        _inspecting = true;
        try
        {
            var items = hangar.Steps.Select(s => (Item: s, Model: s.ToModel())).ToArray();
            var results = await Task.Run(() => items.Select(x => _inspector.Inspect(x.Model)).ToArray());
            for (var i = 0; i < items.Length; i++) items[i].Item.Presence = results[i];
        }
        finally
        {
            _inspecting = false;
        }
    }
}
