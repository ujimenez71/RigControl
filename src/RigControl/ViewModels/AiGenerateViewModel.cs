using System.Collections.ObjectModel;
using System.IO;
using RigControl.Core;
using RigControl.Core.Ai;
using RigControl.Core.Bindings;

namespace RigControl.ViewModels;

/// <summary>Un perfil del que partir en "Generar con IA" (null = perfil nuevo).</summary>
public sealed record BaseProfileOption(string Label, string? Path)
{
    public override string ToString() => Label;
}

/// <summary>
/// Ventana "Generar con IA": clave de la API, avión, perfil de partida y lo que quiere el usuario;
/// llama a ProfileGenerator y deja el borrador listo para abrirlo en el editor (nunca lo guarda).
/// </summary>
public sealed class AiGenerateViewModel : ObservableObject
{
    private string? _apiKey = ApiKeyStore.Load();
    private string _aircraft = "";
    private string _preferences = "";
    private BaseProfileOption _baseProfile;
    private IReadOnlyCollection<string>? _inputEvents;
    private string? _eventsAircraft;
    private bool _busy;
    private string _status = "";
    private string _summary = "";
    private string _validation = "";
    private CancellationTokenSource? _cts;

    public AiGenerateViewModel()
    {
        BaseProfiles.Add(new BaseProfileOption("(perfil nuevo)", null));
        if (Directory.Exists(ProfileFile.ProfilesDir))
            foreach (var p in Directory.EnumerateFiles(ProfileFile.ProfilesDir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
                BaseProfiles.Add(new BaseProfileOption($"Modificar {System.IO.Path.GetFileName(p)}", p));
        _baseProfile = BaseProfiles[0];
    }

    public ObservableCollection<BaseProfileOption> BaseProfiles { get; } = [];
    public ObservableCollection<string> Progress { get; } = [];

    // ------------------------------------------------------------ clave

    public bool HasKey => _apiKey is not null;
    public bool NeedsKey => _apiKey is null;
    public string KeyText => _apiKey is null
        ? "Falta la clave de la API de Anthropic (console.anthropic.com → API keys). Se guarda cifrada para tu usuario de Windows."
        : $"Clave de la API: {ApiKeyStore.Mask(_apiKey)} ({(ApiKeyStore.HasStoredKey ? "guardada y cifrada" : "de ANTHROPIC_API_KEY")})";

    public void SaveKey(string key)
    {
        ApiKeyStore.Save(key);
        _apiKey = ApiKeyStore.Load();
        RaiseKey();
    }

    public void ForgetKey()
    {
        ApiKeyStore.Delete();
        _apiKey = ApiKeyStore.Load();
        RaiseKey();
    }

    private void RaiseKey()
    {
        Raise(nameof(HasKey));
        Raise(nameof(NeedsKey));
        Raise(nameof(KeyText));
        Raise(nameof(CanGenerate));
    }

    // ------------------------------------------------------------ petición

    public string Aircraft
    {
        get => _aircraft;
        set { if (Set(ref _aircraft, value.Trim())) Raise(nameof(CanGenerate)); }
    }

    public string Preferences { get => _preferences; set => Set(ref _preferences, value); }

    public BaseProfileOption BaseProfile
    {
        get => _baseProfile;
        set
        {
            if (!Set(ref _baseProfile, value) || value.Path is null) return;
            // Al modificar un perfil, el avión es el suyo.
            try
            {
                var file = ProfileFile.Load(value.Path);
                if (file.Aircraft.FirstOrDefault(a => a != "*") is { } a) Aircraft = a;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException) { }
        }
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            Raise(nameof(CanGenerate));
            Raise(nameof(NotBusy));
        }
    }

    public bool NotBusy => !_busy;
    public bool CanGenerate => !_busy && _apiKey is not null && _aircraft.Length > 0;

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Summary { get => _summary; private set { if (Set(ref _summary, value)) Raise(nameof(HasResult)); } }
    public string Validation { get => _validation; private set => Set(ref _validation, value); }

    public GenerationResult? Result { get; private set; }
    public bool HasResult => Result?.Profile is not null;

    /// <summary>Dónde se guardará al pulsar Guardar en el editor.</summary>
    public string? TargetPath { get; private set; }

    /// <summary>Busca el avión cargado en DCS o MSFS (y en MSFS su lista de controles B:).</summary>
    public async Task DetectAircraftAsync()
    {
        Busy = true;
        Status = "Buscando el avión cargado en DCS o MSFS…";
        try
        {
            var snap = await Task.Run(() => SimSnapshot.Read(8000));
            if (snap.Aircraft is null)
            {
                Status = "No hay ningún avión cargado en DCS ni en MSFS (escríbelo a mano: \"dcs:ah-64d_blk_ii\" o la carpeta del avión de MSFS).";
                return;
            }
            Aircraft = snap.Aircraft;
            _inputEvents = snap.InputEvents;
            _eventsAircraft = snap.Aircraft;
            Status = snap.Sim == "dcs" ? $"DCS: {snap.Aircraft}"
                : $"MSFS: {snap.Aircraft}" + (snap.InputEvents is null ? " (sin lista de controles B: ¿estás ya en vuelo?)" : $" · {snap.InputEvents.Count} controles B:");
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task GenerateAsync()
    {
        if (!CanGenerate || _apiKey is null) return;
        Busy = true;
        Progress.Clear();
        Summary = "";
        Validation = "";
        Result = null;
        _cts = new CancellationTokenSource();
        try
        {
            var sim = _aircraft.StartsWith("dcs:", StringComparison.OrdinalIgnoreCase) ? "dcs" : "msfs";
            // Los B: solo valen si son del mismo avión; si no, se piden ahora a MSFS (si está abierto).
            if (sim == "msfs" && !string.Equals(_eventsAircraft, _aircraft, StringComparison.OrdinalIgnoreCase))
            {
                AddProgress("Pidiendo a MSFS los controles del avión…");
                var snap = await Task.Run(() => SimSnapshot.Read(8000, _cts.Token));
                _inputEvents = snap.Sim == "msfs" && string.Equals(snap.Aircraft, _aircraft, StringComparison.OrdinalIgnoreCase) ? snap.InputEvents : null;
                _eventsAircraft = _aircraft;
                AddProgress(_inputEvents is null ? "Sin lista de controles B: (MSFS cerrado o con otro avión)." : $"{_inputEvents.Count} controles B:.");
            }
            var baseJson = _baseProfile.Path is { } bp ? await File.ReadAllTextAsync(bp) : null;
            var connected = await Task.Run(ProfilesViewModel.ConnectedKeys);
            var request = new GenerationRequest(sim, _aircraft, _preferences, sim == "msfs" ? _inputEvents : null, baseJson, connected);

            var progress = new Progress<string>(AddProgress);
            var generator = new ProfileGenerator(_apiKey);
            var result = await Task.Run(() => generator.GenerateAsync(request, progress, _cts.Token));
            Result = result;
            Summary = result.Summary;
            Validation = result.Validation is null ? "" : result.Validation.Ok ? "✔ El borrador es válido." : "Problemas que quedan:\n" + result.Validation.Text;
            Status = result.UsageText;
            if (result.Profile is not null)
            {
                TargetPath = _baseProfile.Path;
                Raise(nameof(HasResult));
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelado.";
        }
        catch (Exception ex) // red, API o disco: se muestra y se puede reintentar
        {
            Status = ex.Message;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Busy = false;
        }
    }

    public void Cancel() => _cts?.Cancel();

    private void AddProgress(string line) => Progress.Add($"{DateTime.Now:HH:mm:ss}  {line}");
}
