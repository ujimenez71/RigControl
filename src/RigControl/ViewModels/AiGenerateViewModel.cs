using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
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
/// Ventana "Generar con IA": clave de la API, simulador, avión, perfil de partida y lo que quiere el usuario;
/// llama a ProfileGenerator y deja el borrador listo para abrirlo en el editor (nunca lo guarda).
/// Si Claude pide el simulador (pedir_simulador), la ventana enseña el aviso y espera a "Ya está" o "Seguir sin él".
/// </summary>
public sealed class AiGenerateViewModel : ObservableObject
{
    public const string DcsName = "DCS World";
    public const string MsfsName = "MSFS 2024";

    private string? _apiKey = ApiKeyStore.Load();
    private string? _simName;
    private string _aircraft = "";
    private string _preferences = "";
    private BaseProfileOption _baseProfile;
    private IReadOnlyCollection<string>? _inputEvents;
    private string? _eventsAircraft;
    private IReadOnlyList<string>? _dcsAircraft;
    private bool _busy;
    private string _status = "";
    private string _summary = "";
    private string _validation = "";
    private string? _simAsk;
    private TaskCompletionSource<bool>? _simAnswer;
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
    public IReadOnlyList<string> SimNames { get; } = [DcsName, MsfsName];

    /// <summary>Sugerencias para el avión: los de DCS-BIOS en DCS, los de los perfiles de MSFS en MSFS.</summary>
    public ObservableCollection<string> AircraftSuggestions { get; } = [];

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

    /// <summary>"DCS World" o "MSFS 2024"; null = sin elegir (no se puede generar).</summary>
    public string? SimName
    {
        get => _simName;
        set
        {
            if (!Set(ref _simName, value)) return;
            FillSuggestions();
            Raise(nameof(Sim));
            Raise(nameof(AircraftHint));
            Raise(nameof(AircraftInfo));
            Raise(nameof(CanGenerate));
        }
    }

    /// <summary>"dcs", "msfs" o null.</summary>
    public string? Sim => _simName switch { DcsName => "dcs", MsfsName => "msfs", _ => null };

    /// <summary>Avión tal como se escribe (en DCS sin "dcs:"; escribir "dcs:..." elige DCS).</summary>
    public string Aircraft
    {
        get => _aircraft;
        set
        {
            var v = (value ?? "").Trim();
            if (v.StartsWith("dcs:", StringComparison.OrdinalIgnoreCase))
            {
                v = v[4..];
                SimName = DcsName;
            }
            if (!Set(ref _aircraft, v)) return;
            Raise(nameof(AircraftInfo));
            Raise(nameof(CanGenerate));
        }
    }

    /// <summary>El avión como va en "aircraft" del perfil ("dcs:AH-64D_BLK_II" o la carpeta de MSFS).</summary>
    public string ProfileAircraft => Sim == "dcs" ? "dcs:" + _aircraft : _aircraft;

    public string AircraftHint => Sim switch
    {
        "dcs" => "Nombre del avión en DCS-BIOS (p. ej. AH-64D_BLK_II, UH-1H). No hace falta tener DCS abierto.",
        "msfs" => "Carpeta del avión de MSFS (p. ej. asobo_c172sp_g1000). Los controles B: solo se leen con MSFS abierto y el avión cargado: si hacen falta, Claude te pedirá que lo abras.",
        _ => "Elige primero el simulador.",
    };

    public string AircraftInfo
    {
        get
        {
            if (_aircraft.Length == 0 || Sim is null) return "";
            if (Sim == "dcs")
                return (_dcsAircraft ??= DcsBiosCatalog.AircraftNames()).Count == 0
                    ? "No se encuentra DCS-BIOS: Claude no tendrá la lista de controles."
                    : _dcsAircraft.Any(a => a.Contains(_aircraft, StringComparison.OrdinalIgnoreCase))
                        ? "" : "DCS-BIOS no conoce ese avión: revisa el nombre (o Claude te pedirá abrir DCS con él).";
            return string.Equals(_eventsAircraft, _aircraft, StringComparison.OrdinalIgnoreCase) && _inputEvents is { Count: > 0 } ev
                ? $"{ev.Count} controles B: leídos de MSFS." : "";
        }
    }

    public string Preferences { get => _preferences; set => Set(ref _preferences, value); }

    public BaseProfileOption BaseProfile
    {
        get => _baseProfile;
        set
        {
            if (!Set(ref _baseProfile, value) || value.Path is null) return;
            // Al modificar un perfil, el simulador y el avión son los suyos.
            try
            {
                var file = ProfileFile.Load(value.Path);
                var sim = ProfileCompiler.ResolveSim(file, out _);
                if (sim == SimKind.Dcs) SimName = DcsName;
                else if (sim == SimKind.Msfs) SimName = MsfsName;
                if (file.Aircraft.FirstOrDefault(a => a != "*") is { } a) Aircraft = a;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException) { }
        }
    }

    private void FillSuggestions()
    {
        AircraftSuggestions.Clear();
        IEnumerable<string> items = Sim switch
        {
            "dcs" => _dcsAircraft ??= DcsBiosCatalog.AircraftNames(),
            "msfs" => MsfsProfileAircraft(),
            _ => [],
        };
        foreach (var a in items) AircraftSuggestions.Add(a);
    }

    /// <summary>Aviones que ya usan los perfiles de MSFS (+ el último leído de MSFS).</summary>
    private IEnumerable<string> MsfsProfileAircraft()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_eventsAircraft is { Length: > 0 } e && !e.StartsWith("dcs:", StringComparison.OrdinalIgnoreCase)) set.Add(e);
        if (Directory.Exists(ProfileFile.ProfilesDir))
            foreach (var p in Directory.EnumerateFiles(ProfileFile.ProfilesDir, "*.json"))
                try
                {
                    var file = ProfileFile.Load(p);
                    if (ProfileCompiler.ResolveSim(file, out _) == SimKind.Msfs)
                        foreach (var a in file.Aircraft.Where(a => a != "*")) set.Add(a);
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException) { }
        return set;
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
    public bool CanGenerate => !_busy && _apiKey is not null && Sim is not null && _aircraft.Length > 0;

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Summary { get => _summary; private set { if (Set(ref _summary, value)) Raise(nameof(HasResult)); } }
    public string Validation { get => _validation; private set => Set(ref _validation, value); }

    public GenerationResult? Result { get; private set; }
    public bool HasResult => Result?.Profile is not null;

    /// <summary>Dónde se guardará al pulsar Guardar en el editor.</summary>
    public string? TargetPath { get; private set; }

    // ------------------------------------------------------------ Claude pide el simulador

    /// <summary>Texto del aviso cuando Claude pide abrir el simulador (null = no hay aviso).</summary>
    public string? SimAsk
    {
        get => _simAsk;
        private set { if (Set(ref _simAsk, value)) Raise(nameof(WaitingForSim)); }
    }

    public bool WaitingForSim => _simAsk is not null;

    public void SimReady() => AnswerSim(true);
    public void SimSkip() => AnswerSim(false);

    private void AnswerSim(bool ready)
    {
        SimAsk = null;
        _simAnswer?.TrySetResult(ready);
    }

    /// <summary>Lo llama el generador desde otro hilo: enseña el aviso y espera la respuesta del usuario.</summary>
    private async Task<bool> AskForSimAsync(SimRequest ask, CancellationToken cancel)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _simAnswer = answer;
            var name = ask.Sim == "dcs" && ask.Aircraft.StartsWith("dcs:", StringComparison.OrdinalIgnoreCase) ? ask.Aircraft[4..] : ask.Aircraft;
            SimAsk = $"Claude necesita {ask.SimName} abierto con el avión {name} cargado (ya en la cabina)." +
                     (ask.Reason.Length > 0 ? $"\nMotivo: {ask.Reason}" : "") +
                     "\nCuando esté listo, pulsa «Ya está». Si no puedes ahora, «Seguir sin él».";
        });
        using var reg = cancel.Register(() => answer.TrySetCanceled(cancel));
        try
        {
            return await answer.Task;
        }
        finally
        {
            await Application.Current.Dispatcher.InvokeAsync(() => SimAsk = null);
        }
    }

    // ------------------------------------------------------------ acciones

    /// <summary>Busca el avión cargado en DCS o MSFS (y en MSFS su lista de controles B:); elige también el simulador.</summary>
    public async Task DetectAircraftAsync()
    {
        Busy = true;
        Status = "Buscando el avión cargado en DCS o MSFS…";
        try
        {
            var snap = await Task.Run(() => SimSnapshot.Read(8000));
            if (snap.Aircraft is null)
            {
                Status = "No hay ningún avión cargado en DCS ni en MSFS (elige el simulador y escribe el avión).";
                return;
            }
            _inputEvents = snap.InputEvents;
            _eventsAircraft = snap.Sim == "dcs" ? null : snap.Aircraft;
            SimName = snap.Sim == "dcs" ? DcsName : MsfsName;
            FillSuggestions();
            Aircraft = snap.Aircraft;
            Raise(nameof(AircraftInfo));
            Status = snap.Sim == "dcs" ? $"DCS: {snap.Aircraft}"
                : $"MSFS: {snap.Aircraft}" + (snap.InputEvents is null ? " (sin lista de controles B: ¿estás ya en la cabina?)" : $" · {snap.InputEvents.Count} controles B:");
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task GenerateAsync()
    {
        if (!CanGenerate || _apiKey is null || Sim is not { } sim) return;
        Busy = true;
        Progress.Clear();
        Summary = "";
        Validation = "";
        Result = null;
        _cts = new CancellationTokenSource();
        try
        {
            var aircraft = ProfileAircraft;
            // Los B: solo valen si son del mismo avión; si no, se intenta leerlos ahora (si MSFS está abierto).
            // Si no hay, Claude puede pedir que se abra MSFS (pedir_simulador).
            if (sim == "msfs" && !string.Equals(_eventsAircraft, aircraft, StringComparison.OrdinalIgnoreCase))
            {
                AddProgress("Mirando si MSFS está abierto con el avión…");
                var snap = await Task.Run(() => SimSnapshot.Read(5000, _cts.Token));
                var same = snap.Sim == "msfs" && snap.Aircraft is not null && snap.Aircraft.Contains(aircraft, StringComparison.OrdinalIgnoreCase);
                _inputEvents = same ? snap.InputEvents : null;
                _eventsAircraft = same ? aircraft : null;
                AddProgress(_inputEvents is null ? "Sin lista de controles B: (MSFS cerrado o con otro avión)." : $"{_inputEvents.Count} controles B:.");
                Raise(nameof(AircraftInfo));
            }
            var baseJson = _baseProfile.Path is { } bp ? await File.ReadAllTextAsync(bp) : null;
            var connected = await Task.Run(ProfilesViewModel.ConnectedKeys);
            var request = new GenerationRequest(sim, aircraft, _preferences, sim == "msfs" ? _inputEvents : null, baseJson, connected);

            var progress = new Progress<string>(AddProgress);
            var generator = new ProfileGenerator(_apiKey) { AskForSim = AskForSimAsync };
            var token = _cts.Token;
            var result = await Task.Run(() => generator.GenerateAsync(request, progress, token));
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
            SimAsk = null;
            _cts?.Dispose();
            _cts = null;
            Busy = false;
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _simAnswer?.TrySetCanceled();
    }

    private void AddProgress(string line) => Progress.Add($"{DateTime.Now:HH:mm:ss}  {line}");
}
