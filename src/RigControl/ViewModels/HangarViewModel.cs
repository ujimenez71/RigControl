using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using RigControl.Core.Hangar;

namespace RigControl.ViewModels;

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
        RunProblem = state is HangarStepState.Failed or HangarStepState.TimedOut;
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
    };

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

    public HangarItem(HangarProfile model, Action changed)
    {
        _changed = changed;
        _name = model.Name;
        _startRun = model.StartRun;
        foreach (var s in model.Steps) Steps.Add(new HangarStepItem(s, changed));
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

    public HangarProfile ToModel() => new()
    {
        Name = Name,
        StartRun = StartRun,
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
        _presenceTimer.Tick += async (_, _) => await RefreshPresenceAsync();
        Load();
    }

    /// <summary>La secuencia terminó (sin cancelar) y el hangar pide iniciar la ejecución.</summary>
    public event Action? StartRunRequested;

    public ObservableCollection<HangarItem> Hangars { get; } = [];

    public HangarItem? SelectedHangar
    {
        get => _selectedHangar;
        set
        {
            if (!Set(ref _selectedHangar, value)) return;
            SelectedStep = value?.Steps.FirstOrDefault();
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

    public async void LaunchOrCancel()
    {
        if (IsBusy)
        {
            _cts?.Cancel();
            return;
        }
        if (SelectedHangar is not { } hangar) return;

        Save(); // lo que se lanza es lo que se ve
        foreach (var s in hangar.Steps) s.ClearRun();
        var items = hangar.Steps.ToArray();
        var model = hangar.ToModel();
        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        Message = $"Abriendo {hangar.Name}…";

        var runner = new HangarRunner();
        var dispatcher = Dispatcher.CurrentDispatcher;
        runner.StepChanged += (i, state, detail) => dispatcher.BeginInvoke(() => items[i].SetRun(state, detail));
        try
        {
            var outcome = await Task.Run(() => runner.RunAsync(model, cts.Token));
            Message = outcome switch
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

    public void Dispose()
    {
        _cts?.Cancel();
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
