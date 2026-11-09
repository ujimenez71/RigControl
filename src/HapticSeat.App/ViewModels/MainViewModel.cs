using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HapticSeat.Hf8;
using HapticSeat.Profiles;
using HapticSeat.Telemetry;

namespace HapticSeat.App.ViewModels;

/// <summary>Ventanas de diálogo, para que el ViewModel no dependa de la vista.</summary>
public interface IDialogs
{
    string? AskText(string title, string prompt, string initial);
    bool Confirm(string text);
    void Info(string text);
}

/// <summary>
/// Sondea el host ~30 veces por segundo (estado, cojín, datos de vuelo), gestiona los perfiles y
/// guarda los cambios en disco medio segundo después de la última edición.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const string Ok = "#3FB950";
    private const string Warn = "#D29922";
    private const string Error = "#F85149";
    private const string Idle = "#8B949E";

    private readonly HapticHost _host;
    private readonly ProfileStore _store;
    private readonly AppSettings _settings;
    private readonly IDialogs _dialogs;
    private readonly HostSnapshot _snap = new();
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _saveTimer;
    private readonly HashSet<Profile> _dirtyProfiles = [];
    private bool _settingsDirty;
    private bool _initializing;
    private bool _loadingProfile;
    private string _aircraft = "";
    private int _measuringMotor = -1;
    private bool _measureSeenActive;

    public ObservableCollection<Profile> Profiles { get; }
    public ObservableCollection<EffectViewModel> Effects { get; }

    /// <summary>Motores en orden 0..7 (calibración).</summary>
    public ObservableCollection<MotorViewModel> Motors { get; }

    /// <summary>Motores en el orden del dibujo: filas hombros→muslos, columna izquierda y derecha.</summary>
    public IReadOnlyList<MotorViewModel> SeatLayout { get; }

    public MainViewModel(HapticHost host, ProfileStore store, AppSettings settings, List<Profile> profiles, IDialogs dialogs)
    {
        _host = host;
        _store = store;
        _settings = settings;
        _dialogs = dialogs;

        // Antes que nada: los avisos de cambio de las propiedades de abajo usan estos temporizadores
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => SaveNow();
        _poll = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _poll.Tick += (_, _) => Poll();

        Profiles = new ObservableCollection<Profile>(profiles);
        Effects = new ObservableCollection<EffectViewModel>(host.Effects.Select(e => new EffectViewModel(e, OnEffectEdited)));
        var min = settings.MinLevelBytes();
        Motors = new ObservableCollection<MotorViewModel>(
            Enumerable.Range(0, Hf8Motors.Count).Select(m => new MotorViewModel(m, min[m], OnMinLevelEdited)));
        SeatLayout = [.. new[] { 7, 6, 5, 4, 3, 2, 1, 0 }.Select(m => Motors[m])];

        // Valores leídos de disco: no son ediciones, no hay nada que guardar
        _initializing = true;
        AutoProfile = settings.AutoProfile;
        StartMinimized = settings.StartMinimized;
        SelectedProfile = Profiles.FirstOrDefault(p => p.Name.Equals(settings.LastProfile, StringComparison.OrdinalIgnoreCase))
                          ?? Profiles[0];
        _initializing = false;

        if (host.SourceError is { } error) SimText = error;
        _poll.Start();
    }

    // ------------------------------------------------------ estado (solo lectura para la vista)

    [ObservableProperty] public partial string SeatText { get; set; } = "Buscando el HF8…";
    [ObservableProperty] public partial string SeatColor { get; set; } = Idle;
    [ObservableProperty] public partial string SimText { get; set; } = "Esperando telemetría de IL-2…";
    [ObservableProperty] public partial string SimColor { get; set; } = Idle;
    [ObservableProperty] public partial string AircraftText { get; set; } = "—";
    [ObservableProperty] public partial string IasText { get; set; } = "—";
    [ObservableProperty] public partial string GText { get; set; } = "—";
    [ObservableProperty] public partial string AglText { get; set; } = "—";
    [ObservableProperty] public partial string RpmText { get; set; } = "—";
    [ObservableProperty] public partial string BuffetText { get; set; } = "—";
    [ObservableProperty] public partial string EventsText { get; set; } = "—";
    [ObservableProperty] public partial bool Flying { get; set; }

    [ObservableProperty] public partial bool Calibrating { get; set; }
    [ObservableProperty] public partial string CalibrationText { get; set; } = "";

    // ------------------------------------------------------ perfil

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditAircraft))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand), nameof(RenameProfileCommand))]
    public partial Profile? SelectedProfile { get; set; }

    [ObservableProperty] public partial int MasterGain { get; set; } = 100;
    [ObservableProperty] public partial string AircraftPatterns { get; set; } = "";
    [ObservableProperty] public partial bool AutoProfile { get; set; }
    [ObservableProperty] public partial bool StartMinimized { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateAircraftProfileCommand))]
    public partial string CurrentAircraft { get; set; } = "";

    /// <summary>El perfil General es el de reserva: no se le asignan aviones.</summary>
    public bool CanEditAircraft => SelectedProfile is { IsDefault: false };

    partial void OnSelectedProfileChanged(Profile? value)
    {
        if (value is null) return;
        _loadingProfile = true;
        MasterGain = value.MasterGain;
        AircraftPatterns = string.Join(", ", value.Aircraft);
        foreach (var effect in Effects) effect.Load(value.For(effect.Id));
        _loadingProfile = false;
        _host.ApplyProfile(value);
    }

    partial void OnMasterGainChanged(int value)
    {
        if (_loadingProfile || SelectedProfile is null) return;
        SelectedProfile.MasterGain = value;
        ProfileEdited(SelectedProfile);
    }

    partial void OnAircraftPatternsChanged(string value)
    {
        if (_loadingProfile || SelectedProfile is null || SelectedProfile.IsDefault) return;
        SelectedProfile.Aircraft = [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        ProfileEdited(SelectedProfile);
    }

    partial void OnAutoProfileChanged(bool value)
    {
        _settings.AutoProfile = value;
        SettingsEdited();
        if (value && _aircraft.Length > 0) SelectedProfile = ProfileStore.ForAircraft(Profiles, _aircraft);
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        _settings.StartMinimized = value;
        SettingsEdited();
    }

    private void OnEffectEdited(EffectViewModel effect)
    {
        if (_loadingProfile || SelectedProfile is null) return;
        var setting = SelectedProfile.For(effect.Id);
        setting.Enabled = effect.Enabled;
        setting.Gain = effect.Gain;
        ProfileEdited(SelectedProfile);
    }

    private void ProfileEdited(Profile profile)
    {
        _host.ApplyProfile(profile);
        _dirtyProfiles.Add(profile);
        ScheduleSave();
    }

    [RelayCommand]
    private void NewProfile() => AddProfile(new Profile(), "Nuevo perfil");

    [RelayCommand]
    private void DuplicateProfile()
    {
        if (SelectedProfile is not null) AddProfile(SelectedProfile.Clone(), $"{SelectedProfile.Name} (copia)");
    }

    [RelayCommand(CanExecute = nameof(CanEditAircraft))]
    private void RenameProfile()
    {
        if (SelectedProfile is not { } profile) return;
        var name = AskNewName("Renombrar perfil", profile.Name, renaming: profile);
        if (name is null) return;

        SaveNow();
        _store.Delete(profile.Name);
        profile.Name = name;
        _store.Save(profile);
        Resort(profile);
    }

    [RelayCommand(CanExecute = nameof(CanEditAircraft))]
    private void DeleteProfile()
    {
        if (SelectedProfile is not { IsDefault: false } profile) return;
        if (!_dialogs.Confirm($"¿Eliminar el perfil «{profile.Name}»?")) return;

        _dirtyProfiles.Remove(profile);
        _store.Delete(profile.Name);
        Profiles.Remove(profile);
        SelectedProfile = AutoProfile && _aircraft.Length > 0 ? ProfileStore.ForAircraft(Profiles, _aircraft) : Profiles[0];
    }

    /// <summary>Nuevo perfil para el avión en vuelo, partiendo de los ajustes actuales.</summary>
    [RelayCommand(CanExecute = nameof(HasAircraft))]
    private void CreateAircraftProfile()
    {
        if (SelectedProfile is null || _aircraft.Length == 0) return;
        var profile = SelectedProfile.Clone();
        profile.Aircraft = [_aircraft];
        AddProfile(profile, string.Concat(_aircraft.Split(Path.GetInvalidFileNameChars())).Trim());
    }

    private bool HasAircraft() => CurrentAircraft.Length > 0;

    private void AddProfile(Profile profile, string suggestedName)
    {
        var name = AskNewName("Nombre del perfil", suggestedName, renaming: null);
        if (name is null) return;
        profile.Name = name;
        _store.Save(profile);
        Profiles.Add(profile);
        Resort(profile);
    }

    private string? AskNewName(string title, string initial, Profile? renaming)
    {
        while (true)
        {
            var name = _dialogs.AskText(title, "Nombre:", initial)?.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            if (!ProfileStore.IsValidName(name))
                _dialogs.Info("Nombre no válido (no uses \\ / : * ? \" < > | y máximo 60 caracteres).");
            else if (Profiles.Any(p => p != renaming && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                     || name.Equals(Profile.DefaultName, StringComparison.OrdinalIgnoreCase))
                _dialogs.Info($"Ya existe un perfil «{name}».");
            else
                return name;
            initial = name;
        }
    }

    /// <summary>Recoloca la lista (General primero, luego por nombre) y selecciona el perfil.</summary>
    private void Resort(Profile select)
    {
        var sorted = Profiles.OrderBy(p => !p.IsDefault).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Profiles.Clear();
        foreach (var p in sorted) Profiles.Add(p);
        SelectedProfile = select;
        _settings.LastProfile = select.Name;
        SettingsEdited();
    }

    /// <summary>Elección manual en la lista: se recuerda para el próximo arranque.</summary>
    public void ProfileChosenByUser()
    {
        if (SelectedProfile is null) return;
        _settings.LastProfile = SelectedProfile.Name;
        SettingsEdited();
    }

    // ------------------------------------------------------ pruebas y calibración

    [RelayCommand]
    private void TestEffect(EffectViewModel effect)
    {
        if (Flying)
            _dialogs.Info("Las pruebas solo funcionan sin estar volando.");
        else
            _host.TestEffect(effect.Id);
    }

    [RelayCommand]
    private void Measure(MotorViewModel motor)
    {
        if (Flying)
        {
            _dialogs.Info("La calibración solo funciona sin estar volando.");
            return;
        }
        _measuringMotor = motor.Index;
        _measureSeenActive = false;
        Calibrating = true;
        CalibrationText = $"Preparando {motor.Name}…";
        foreach (var m in Motors) m.IsMeasuring = m == motor;
        _host.StartThreshold(motor.Index);
    }

    /// <summary>"Lo noto": fija el mínimo de ese motor y de su pareja del otro lado.</summary>
    [RelayCommand]
    private void Felt()
    {
        var motor = _measuringMotor;
        var level = _host.StopThreshold();
        EndMeasure();
        if (motor < 0) return;
        if (level == 0)
        {
            CalibrationText = "Aún no había empezado la rampa. Vuelve a medir.";
            return;
        }

        var pair = motor ^ 1; // 0↔1, 2↔3…: mismo sitio del otro lado
        Motors[motor].MinLevel = level;
        Motors[pair].MinLevel = level;
        CalibrationText = $"{Motors[motor].Name} y {Motors[pair].Name}: mínimo {level}.";
    }

    [RelayCommand]
    private void CancelMeasure()
    {
        _host.StopThreshold();
        EndMeasure();
        CalibrationText = "Medición cancelada.";
    }

    [RelayCommand]
    private void PulseMinimum(MotorViewModel motor) => _host.PulseMinimum(motor.Index);

    [RelayCommand]
    private void RestoreMinLevels()
    {
        foreach (var m in Motors) m.MinLevel = Hf8Motors.DefaultMinLevels[m.Index];
        CalibrationText = "Restaurados los niveles medidos el 20/10/2026.";
    }

    private void EndMeasure()
    {
        _measuringMotor = -1;
        Calibrating = false;
        foreach (var m in Motors) m.IsMeasuring = false;
    }

    private void OnMinLevelEdited(MotorViewModel motor)
    {
        _settings.MinLevels = [.. Motors.Select(m => m.MinLevel)];
        _host.SetMinLevels(_settings.MinLevelBytes());
        SettingsEdited();
    }

    // ------------------------------------------------------ sondeo

    private void Poll()
    {
        _host.Snapshot(_snap);
        var f = _snap.Flight;

        for (var m = 0; m < Motors.Count; m++) Motors[m].Intensity = _snap.Motors[m];

        (SeatText, SeatColor) = _snap.Seat switch
        {
            SeatState.Connected => ($"Conectado ({_snap.SeatPort})", Ok),
            SeatState.Busy => ("Puerto ocupado: cierra HFS", Warn),
            _ => ("No detectado (¿enchufado y en modo PC?)", Error),
        };

        // Un error de IL-2 (puerto ocupado) no debe tapar que MSFS está funcionando
        (SimText, SimColor) = f.Connected
            ? f.Paused ? ($"{f.Source}: en pausa o menús", Warn) : ($"{f.Source}: recibiendo", Ok)
            : _host.SourceError is { } sourceError ? (sourceError, Warn)
            : ("Esperando a MSFS, DCS o IL-2", Idle);
        Flying = f.Active;

        if (f.Active)
        {
            IasText = $"{f.IasMs * 3.6f:0} km/h";
            GText = $"{f.VerticalG:0.0}";
            AglText = $"{f.AglM:0} m";
            RpmText = f.EngineCount == 0 ? "—" : string.Join(" / ", f.EngineRpm.Take(f.EngineCount).Select(r => r.ToString("0")));
            BuffetText = f.BuffetAmplitude > 0.001f ? $"{f.BuffetAmplitude:0.00} a {f.BuffetFrequency:0} Hz" : "—";
        }
        else
        {
            // Sin vuelo, valores congelados confundirían: se borran (el avión se mantiene)
            IasText = GText = AglText = RpmText = BuffetText = "—";
        }

        var c = _snap.EventCounts;
        EventsText = $"Disparos {c[(int)FlightEventType.GunFired]} · Impactos {c[(int)FlightEventType.Hit]} · " +
                     $"Bombas {c[(int)FlightEventType.BombRelease]} · Cohetes {c[(int)FlightEventType.RocketLaunch]}";

        foreach (var e in Effects) e.IsTesting = e.Id == _snap.TestingEffect;

        UpdateCalibration();
        UpdateAircraft(f.Aircraft);
    }

    private void UpdateCalibration()
    {
        if (_measuringMotor < 0) return;
        if (_snap.ThresholdMotor >= 0)
        {
            _measureSeenActive = true;
            var name = Motors[_snap.ThresholdMotor].Name;
            CalibrationText = _snap.ThresholdLevel > 0
                ? $"Midiendo {name}: nivel {_snap.ThresholdLevel}"
                : $"Midiendo {name}…";
        }
        else if (_measureSeenActive)
        {
            EndMeasure();
            CalibrationText = "La rampa terminó sin pulsar «Lo noto»: el umbral está por encima de 80.";
        }
    }

    private void UpdateAircraft(string aircraft)
    {
        if (aircraft == _aircraft) return;
        _aircraft = aircraft;
        CurrentAircraft = aircraft;
        AircraftText = aircraft.Length > 0 ? aircraft : "—";
        if (AutoProfile && aircraft.Length > 0) SelectedProfile = ProfileStore.ForAircraft(Profiles, aircraft);
    }

    // ------------------------------------------------------ guardado

    private void SettingsEdited()
    {
        if (_initializing) return;
        _settingsDirty = true;
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            foreach (var p in _dirtyProfiles) _store.Save(p);
            _dirtyProfiles.Clear();
            if (_settingsDirty) _store.SaveSettings(_settings);
            _settingsDirty = false;
        }
        catch (IOException ex)
        {
            _dialogs.Info($"No se pudieron guardar los cambios: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _poll.Stop();
        SaveNow();
    }
}
