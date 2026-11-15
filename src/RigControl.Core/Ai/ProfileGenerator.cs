using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using RigControl.Core.Bindings;
using RigControl.Core.WinWing;

namespace RigControl.Core.Ai;

/// <summary>Lo que se le pide al generador.</summary>
/// <param name="Sim">"dcs" o "msfs".</param>
/// <param name="Aircraft">Avión como en "aircraft" del perfil ("dcs:ah-64d_blk_ii", "c172").</param>
/// <param name="Preferences">Lo que escribe el usuario: qué quiere asignar y cómo.</param>
/// <param name="MsfsInputEvents">Controles B: del avión cargado en MSFS (null = no hay MSFS).</param>
/// <param name="BaseProfileJson">Perfil del que partir (para pedir cambios a uno existente); null = nuevo.</param>
/// <param name="ConnectedKeys">Claves de los mandos conectados ahora (null = no se sabe).</param>
public sealed record GenerationRequest(string Sim, string Aircraft, string Preferences,
                                       IReadOnlyCollection<string>? MsfsInputEvents = null,
                                       string? BaseProfileJson = null,
                                       ISet<string>? ConnectedKeys = null);

/// <summary>Resultado: el último borrador que validó el modelo (aunque tenga errores) y su resumen.</summary>
public sealed record GenerationResult(ProfileFile? Profile, string? Json, ValidationResult? Validation,
                                      string Summary, long InputTokens, long OutputTokens, long CacheReadTokens,
                                      long CacheWriteTokens = 0)
{
    /// <summary>Coste aproximado en dólares con los precios de Opus 5.5 (entrada 4, escritura en caché 5, lectura 0,20, salida 20 por millón).</summary>
    public double CostUsd => (InputTokens * 4.0 + CacheWriteTokens * 5.0 + CacheReadTokens * 0.20 + OutputTokens * 20.0) / 1_000_000;

    public string UsageText =>
        $"Tokens: {InputTokens + CacheWriteTokens + CacheReadTokens:N0} de entrada ({CacheReadTokens:N0} leídos de la caché), " +
        $"{OutputTokens:N0} de salida · unos {CostUsd:0.00} $";
}

/// <summary>Lo que pide la IA con pedir_simulador: arrancar <paramref name="Sim"/> ("dcs"/"msfs") con ese avión cargado.</summary>
public sealed record SimRequest(string Sim, string Aircraft, string Reason)
{
    public string SimName => Sim == "dcs" ? "DCS World" : "MSFS 2024";
}

/// <summary>Lo que devuelve la herramienta validar_perfil (y lo que ve el usuario).</summary>
public sealed record ValidationResult(ProfileFile? File, ProfileReport? Report, bool Ok, string Text);

/// <summary>
/// Genera un perfil con Claude (API de Anthropic). Manda los mandos del usuario (catálogo y notas),
/// sus perfiles actuales como ejemplo, los controles del avión (DCS-BIOS o B: de MSFS) y sus
/// preferencias; el modelo escribe el perfil y lo valida con la herramienta validar_perfil
/// (ProfileCompiler + ConflictGuard + nombres de controles) hasta que no tiene errores.
/// El resultado es un BORRADOR: nunca se guarda en la carpeta de perfiles desde aquí.
/// </summary>
public sealed partial class ProfileGenerator
{
    public const string Model = "claude-opus-5-5";
    private const int MaxTurns = 12;

    /// <summary>Notas libres del usuario para la IA (mapas de botones, costumbres...).</summary>
    public static string NotesPath { get; } =
        Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "ia-notas.md");

    /// <summary>Borradores generados desde RigProbe (fuera de profiles\: "run auto" no los carga).</summary>
    public static string DraftsDir { get; } =
        Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "borradores");

    private readonly AnthropicClient _client;

    public ProfileGenerator(string apiKey)
    {
        _client = new AnthropicClient { ApiKey = apiKey };
    }

    /// <summary>
    /// Si no es null, la IA puede pedir (herramienta pedir_simulador) que se arranque el simulador con el
    /// avión, para leer sus controles. Devuelve true cuando el usuario dice que ya está; false = seguir sin él.
    /// </summary>
    public Func<SimRequest, CancellationToken, Task<bool>>? AskForSim { get; init; }

    public async Task<GenerationResult> GenerateAsync(GenerationRequest request, IProgress<string>? progress = null,
                                                      CancellationToken cancel = default)
    {
        var catalog = DeviceCatalog.Load();
        IReadOnlyList<DcsControl>? dcs = request.Sim == "dcs" ? DcsBiosCatalog.ForAircraft(request.Aircraft) : null;
        var bNames = request.MsfsInputEvents is { Count: > 0 } ev
            ? new HashSet<string>(ev, StringComparer.OrdinalIgnoreCase) : null;

        List<BetaToolUnion> tools = AskForSim is null ? [ValidateTool] : [ValidateTool, SimTool];

        var context = BuildContext(request, catalog, dcs, AskForSim is not null);
        List<BetaMessageParam> messages = [new BetaUserMessageParam { Content = context }];

        ValidationResult? last = null;
        string? lastJson = null;
        long inTokens = 0, outTokens = 0, cacheRead = 0, cacheWrite = 0;
        var summary = "";

        for (var turn = 1; turn <= MaxTurns; turn++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report(turn == 1 ? "Claude está escribiendo el perfil…" : $"Claude está corrigiendo el borrador ({turn - 1})…");

            BetaMessage response;
            try
            {
                response = await _client.Beta.Messages.Create(new MessageCreateParams
                {
                    Model = Model,
                    MaxTokens = 32000,
                    Betas = ["server-side-fallback-2026-07-01"],
                    Fallbacks = new Default(),
                    OutputConfig = new BetaOutputConfig { Effort = Effort.High },
                    CacheControl = new BetaCacheControlEphemeral(),
                    System = ProfileGuide.System,
                    Tools = tools,
                    Messages = messages,
                }, cancel);
            }
            catch (AnthropicApiException ex)
            {
                throw new InvalidOperationException(ApiErrorText(ex), ex);
            }
            catch (AnthropicIOException ex)
            {
                throw new InvalidOperationException($"No se pudo conectar con la API de Anthropic ({ex.Message}).", ex);
            }

            inTokens += response.Usage.InputTokens;
            outTokens += response.Usage.OutputTokens;
            cacheRead += response.Usage.CacheReadInputTokens ?? 0;
            cacheWrite += response.Usage.CacheCreationInputTokens ?? 0;

            if (response.StopReason == BetaStopReason.Refusal)
            {
                summary = "Claude ha rechazado la petición" + (response.StopDetails is { } d ? $" ({d.Category}: {d.Explanation})." : ".");
                break;
            }

            // Eco del turno del asistente. Tras un "fallback" (otro modelo continuó), lo anterior al último
            // bloque fallback que no sea texto se omite, y el bloque se conserva en su sitio.
            var content = response.Content;
            var lastFallback = -1;
            for (var i = 0; i < content.Count; i++)
                if (content[i].TryPickFallback(out _)) lastFallback = i;

            List<BetaContentBlockParam> echo = [];
            List<BetaContentBlockParam> results = [];
            var text = new StringBuilder();
            for (var i = 0; i < content.Count; i++)
            {
                var block = content[i];
                var beforeFallback = i < lastFallback;
                if (block.TryPickText(out var t))
                {
                    echo.Add(new BetaTextBlockParam { Text = t.Text });
                    text.Append(t.Text);
                }
                else if (block.TryPickFallback(out var fb))
                    echo.Add(new BetaFallbackBlockParam { From = new BetaFallbackInfoParam { Model = fb.From.Model }, To = new BetaFallbackInfoParam { Model = fb.To.Model } });
                else if (beforeFallback)
                    continue;
                else if (block.TryPickThinking(out var th))
                    echo.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                else if (block.TryPickRedactedThinking(out var rt))
                    echo.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
                else if (block.TryPickToolUse(out var tu))
                {
                    echo.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                    string resultText;
                    var isError = false;
                    if (tu.Name == SimToolName && AskForSim is not null)
                    {
                        var reason = tu.Input.TryGetValue("motivo", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "";
                        var (simText, newDcs, newEvents) = await AskSimAsync(request, reason, progress, cancel);
                        if (newDcs is not null) dcs = newDcs;
                        if (newEvents is not null) bNames = newEvents;
                        resultText = simText;
                    }
                    else if (tu.Name != ValidateToolName || !tu.Input.TryGetValue("perfil_json", out var pj)
                        || pj.ValueKind != JsonValueKind.String)
                    {
                        resultText = "Herramienta o parámetros no válidos: llama a validar_perfil con perfil_json (texto).";
                        isError = true;
                    }
                    else
                    {
                        lastJson = pj.GetString()!;
                        last = Validate(lastJson, catalog, dcs, bNames);
                        resultText = last.Text;
                        progress?.Report(last.Ok ? "Borrador válido." : "Borrador con problemas: " + FirstLine(last.Text));
                    }
                    results.Add(new BetaToolResultBlockParam { ToolUseID = tu.ID, Content = resultText, IsError = isError });
                }
            }

            if (response.StopReason == BetaStopReason.ToolUse && results.Count > 0)
            {
                messages.Add(new BetaAssistantMessageParam { Content = echo });
                messages.Add(new BetaUserMessageParam { Content = results });
                continue;
            }

            summary = text.ToString().Trim();
            if (response.StopReason == BetaStopReason.MaxTokens)
                summary += "\n(La respuesta se cortó por longitud.)";
            break;
        }

        if (lastJson is null && summary.Length == 0)
            summary = "Claude no llegó a entregar ningún borrador.";
        return new GenerationResult(last?.File, lastJson is null ? null : Reformat(last?.File, lastJson), last,
                                    summary, inTokens, outTokens, cacheRead, cacheWrite);
    }

    /// <summary>pedir_simulador: espera al usuario, lee el simulador y devuelve el texto para la IA y los controles nuevos.</summary>
    private async Task<(string Text, IReadOnlyList<DcsControl>? Dcs, HashSet<string>? Events)> AskSimAsync(
        GenerationRequest request, string reason, IProgress<string>? progress, CancellationToken cancel)
    {
        var ask = new SimRequest(request.Sim, request.Aircraft, reason);
        progress?.Report($"Claude pide abrir {ask.SimName} con {request.Aircraft}.");
        if (!await AskForSim!(ask, cancel))
        {
            progress?.Report("Sin simulador: Claude sigue sin esa información.");
            return ("El usuario no puede abrir el simulador ahora. Sigue sin la lista de controles y dilo en el resumen.", null, null);
        }
        progress?.Report($"Leyendo {ask.SimName}…");
        var snap = await Task.Run(() => SimSnapshot.Read(15000, cancel), cancel);
        cancel.ThrowIfCancellationRequested();
        var wanted = request.Aircraft.StartsWith("dcs:", StringComparison.OrdinalIgnoreCase) ? request.Aircraft[4..] : request.Aircraft;

        if (snap.Sim != request.Sim || snap.Aircraft is null)
        {
            progress?.Report($"{ask.SimName} no responde o no tiene un avión cargado.");
            var other = snap.Sim is not null && snap.Sim != request.Sim
                ? $" (está abierto {(snap.Sim == "dcs" ? "DCS World" : "MSFS 2024")} con {snap.Aircraft})" : "";
            return ($"{ask.SimName} no responde o no tiene ningún avión cargado{other}. Puedes pedirlo una vez más o seguir sin la lista.", null, null);
        }
        if (!snap.Aircraft.Contains(wanted, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report($"Hay otro avión cargado: {snap.Aircraft}.");
            return ($"En {ask.SimName} está cargado \"{snap.Aircraft}\", no \"{request.Aircraft}\". Puedes pedirlo una vez más " +
                    "(dilo en el motivo) o seguir sin la lista.", null, null);
        }

        if (request.Sim == "dcs")
        {
            var controls = DcsBiosCatalog.ForAircraft(snap.Aircraft);
            if (controls.Count == 0)
                return ($"DCS tiene cargado {snap.Aircraft}, pero DCS-BIOS no tiene definiciones de controles para él. Sigue sin lista.", null, null);
            progress?.Report($"{controls.Count} controles de DCS-BIOS.");
            var sb = new StringBuilder($"DCS tiene cargado {snap.Aircraft}.\n");
            AppendDcsControls(sb, controls);
            return (sb.ToString(), controls, null);
        }
        if (snap.InputEvents is not { Count: > 0 } ev)
        {
            progress?.Report("MSFS no da la lista de controles B: (¿aún en el menú?).");
            return ($"MSFS tiene cargado {snap.Aircraft} pero no ha dado la lista de controles B: (suele llegar al estar ya en la cabina). " +
                    "Puedes pedirlo una vez más o seguir sin la lista.", null, null);
        }
        progress?.Report($"{ev.Count} controles B:.");
        return ($"MSFS tiene cargado {snap.Aircraft}. Controles B: ({ev.Count}):\n" + string.Join(", ", ev.Order(StringComparer.OrdinalIgnoreCase)),
                null, new HashSet<string>(ev, StringComparer.OrdinalIgnoreCase));
    }

    private const string SimToolName = "pedir_simulador";

    private static readonly BetaTool SimTool = new()
    {
        Name = SimToolName,
        Description = "Pide al usuario que arranque el simulador del perfil con el avión cargado (en la cabina) y " +
                      "devuelve la lista de controles del avión (B: de MSFS o D: de DCS-BIOS). Úsala solo si falta esa " +
                      "lista y la necesitas; el usuario puede tardar minutos o decir que no.",
        InputSchema = new()
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["motivo"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string",
                    description = "Para qué lo necesitas, en una frase en español (se le enseña al usuario).",
                }),
            },
            Required = ["motivo"],
        },
    };

    private const string ValidateToolName = "validar_perfil";

    private static readonly BetaTool ValidateTool = new()
    {
        Name = ValidateToolName,
        Description = "Compila y revisa un perfil de RigControl completo. Devuelve \"OK\" o la lista de errores " +
                      "(formato, entradas mal escritas, bindings duplicados, controles que no existen en el avión, " +
                      "dispositivos que no están en el catálogo). El último perfil que pases es el que recibe el usuario.",
        InputSchema = new()
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["perfil_json"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string",
                    description = "El perfil completo en JSON (sin comentarios).",
                }),
            },
            Required = ["perfil_json"],
        },
    };

    // ------------------------------------------------------------------ validación

    [GeneratedRegex(@"(?<![A-Za-z])D:([A-Za-z0-9_]+)")]
    private static partial Regex DcsNameRegex();

    /// <summary>
    /// Lo que hace validar_perfil: lo mismo que "check" (compilar + Conflict Guard), más los nombres de
    /// controles D: y B: contra los del avión y las claves de dispositivo contra el catálogo.
    /// Público para las pruebas.
    /// </summary>
    public static ValidationResult Validate(string json, DeviceCatalog catalog, IReadOnlyList<DcsControl>? dcs,
                                            IReadOnlySet<string>? msfsInputEvents)
    {
        ProfileFile file;
        try
        {
            file = ProfileFile.Parse(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return new ValidationResult(null, null, false, $"JSON no válido: {ex.Message}");
        }

        var report = ProfileInspector.Inspect(file, "borrador.json", catalog);
        List<string> problems = [.. report.Errors.Select(e => "ERROR " + e)];
        problems.AddRange(report.Findings.Where(f => f.Severity != Severity.Info).Select(f => f.ToString()));

        foreach (var (alias, key) in file.Devices)
        {
            var k0 = key.Trim();
            if (!catalog.Devices.Any(d => d.Key.Equals(k0, StringComparison.OrdinalIgnoreCase)
                                          || d.Key.StartsWith(k0 + ":", StringComparison.OrdinalIgnoreCase)))
                problems.Add($"ERROR devices: \"{alias}\" = \"{key}\" no está en el catálogo de mandos (usa la clave exacta).");
        }

        // Nombres de controles: en acciones, condiciones, luces e icp (basta con buscarlos en el texto).
        var bindingsText = string.Join("\n", file.Bindings.Select(b => b.Action)
            .Concat(file.Lights.Values.Select(v => v.ToString()))
            .Concat(file.Icp)
            .Concat(file.Bindings.Select(b => b.When is null ? "" : JsonSerializer.Serialize(b.When)))
            .Concat(file.Layers.Values.Select(l => JsonSerializer.Serialize(l))));
        if (dcs is { Count: > 0 })
        {
            var names = new HashSet<string>(dcs.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var n in DcsNameRegex().Matches(bindingsText).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!names.Contains(n)) problems.Add($"ERROR D:{n} no existe en los controles de DCS-BIOS de este avión.");
        }
        if (msfsInputEvents is { Count: > 0 })
            foreach (var b in file.Bindings.Select(b => b.Action).Where(a => a.StartsWith("B:", StringComparison.OrdinalIgnoreCase)))
            {
                var name = b[2..].Split('=')[0].Trim();
                if (!msfsInputEvents.Contains(name)) problems.Add($"ERROR B:{name} no existe en el avión cargado en MSFS.");
            }

        var ok = problems.Count == 0 || problems.All(p => !p.StartsWith("ERROR", StringComparison.Ordinal));
        var text = problems.Count == 0
            ? $"OK: {file.Bindings.Count} bindings, {file.Lights.Count} luces, {file.Icp.Count} líneas de ICP, sin errores ni duplicados."
            : (ok ? "OK con avisos:\n" : $"{problems.Count(p => p.StartsWith("ERROR"))} errores:\n") + string.Join("\n", problems.Distinct());
        return new ValidationResult(file, report, ok, text);
    }

    private static string FirstLine(string s) => s.Split('\n')[0];

    /// <summary>El JSON con el formato de los perfiles (un binding por línea), o el original si no se puede.</summary>
    private static string Reformat(ProfileFile? file, string json)
    {
        if (file is null) return json;
        try
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"rigcontrol-ia-{Guid.NewGuid():N}.json");
            file.Save(tmp);
            var text = File.ReadAllText(tmp);
            File.Delete(tmp);
            return text;
        }
        catch (IOException)
        {
            return json;
        }
    }

    private static string ApiErrorText(AnthropicApiException ex) => ex switch
    {
        AnthropicUnauthorizedException => "La clave de la API no es válida (401). Revísala.",
        AnthropicRateLimitException => "Límite de peticiones de la API (429). Espera un poco y vuelve a intentarlo.",
        Anthropic5xxException => "La API de Anthropic ha fallado o está saturada. Vuelve a intentarlo en un rato.",
        _ => $"Error de la API de Anthropic: {ex.Message}",
    };

    // ------------------------------------------------------------------ contexto

    /// <summary>Todo lo que el modelo necesita saber, en un solo mensaje (público para RigProbe "ia contexto").</summary>
    public static string BuildContext(GenerationRequest request, DeviceCatalog catalog, IReadOnlyList<DcsControl>? dcs,
                                      bool canAskForSim = false)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Mandos del usuario (catálogo de RigControl)");
        foreach (var d in catalog.Devices)
        {
            var connected = request.ConnectedKeys is null ? "" : request.ConnectedKeys.Contains(d.Key) ? " · conectado" : " · NO conectado";
            sb.AppendLine($"- {d.Alias} · clave \"{d.Key}\" · {d.ProductName} · uso: {string.Join(", ", d.Tags)}{connected}");
            if (d.Inputs is { Count: > 0 })
                sb.AppendLine("  Entradas con nombre: " + string.Join("; ", d.Inputs.Select(i => $"{i.Key} = {i.Value}")));
        }
        sb.AppendLine();

        sb.AppendLine("# Notas del usuario sobre sus mandos (ia-notas.md)");
        sb.AppendLine(File.Exists(NotesPath) ? File.ReadAllText(NotesPath).Trim() : "(no hay notas)");
        sb.AppendLine();

        sb.AppendLine("# Perfiles que ya tiene (ejemplos de estilo y fuente de lo que hace cada botón)");
        if (Directory.Exists(ProfileFile.ProfilesDir))
            foreach (var path in Directory.EnumerateFiles(ProfileFile.ProfilesDir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"## {Path.GetFileName(path)}");
                sb.AppendLine("```json");
                sb.AppendLine(File.ReadAllText(path).Trim());
                sb.AppendLine("```");
            }
        sb.AppendLine();

        sb.AppendLine("# Luces disponibles");
        sb.AppendLine("Bravo: " + string.Join(", ", Enum.GetNames<BravoLed>().Skip(1)));
        sb.AppendLine("WinWing: " + string.Join(", ", WinWingPanel.LightNames));
        sb.AppendLine();

        sb.AppendLine($"# Avión: {request.Aircraft} (simulador: {(request.Sim == "dcs" ? "DCS World" : "MSFS 2024")})");
        if (request.Sim == "dcs")
        {
            if (dcs is null or { Count: 0 })
                sb.AppendLine("No se han encontrado los controles de DCS-BIOS de este avión (¿nombre mal escrito?): no uses nombres D: que no conozcas" +
                              (canAskForSim ? "; con pedir_simulador se lee el avión cargado en DCS." : "."));
            else
                AppendDcsControls(sb, dcs);
        }
        else
        {
            if (request.MsfsInputEvents is { Count: > 0 } ev)
            {
                sb.AppendLine($"Controles B: del avión cargado en MSFS ({ev.Count}):");
                sb.AppendLine(string.Join(", ", ev.Order(StringComparer.OrdinalIgnoreCase)));
            }
            else
                sb.AppendLine("No hay lista de controles B: (MSFS no está abierto con este avión). " +
                              (canAskForSim ? "Si la necesitas, pídela con pedir_simulador; si no, usa " : "Usa ") +
                              "eventos K: estándar y, si hace falta B:, solo los que aparezcan en los perfiles existentes; dilo en el resumen.");
            sb.AppendLine("Eventos K: frecuentes: " + string.Join(", ", ActionDescriber.CommonSimEvents));
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(request.BaseProfileJson))
        {
            sb.AppendLine("# Perfil de partida (modifícalo según las preferencias; conserva lo demás)");
            sb.AppendLine("```json");
            sb.AppendLine(request.BaseProfileJson.Trim());
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("# Lo que pide el usuario");
        sb.AppendLine(string.IsNullOrWhiteSpace(request.Preferences) ? "(nada concreto: un perfil razonable con lo que se sepa de los mandos)" : request.Preferences.Trim());
        return sb.ToString();
    }

    private static void AppendDcsControls(StringBuilder sb, IReadOnlyList<DcsControl> dcs)
    {
        sb.AppendLine($"Controles de DCS-BIOS ({dcs.Count}). Formato: NOMBRE | categoría | descripción | entradas | posiciones (0, 1, ...) o valor máximo | texto");
        foreach (var c in dcs)
        {
            sb.Append(c.Name).Append(" | ").Append(c.Category).Append(" | ").Append(c.Description);
            sb.Append(" | ").Append(c.Inputs.Length == 0 ? "solo salida" : string.Join(",", c.Inputs.Distinct()));
            if (c.Positions.Length > 0) sb.Append(" | ").Append(string.Join(", ", c.Positions.Select((p, i) => $"{i}={p}")));
            else if (c.MaxValue > 1) sb.Append(" | máx ").Append(c.MaxValue);
            if (c.TextLength > 0) sb.Append(" | texto de ").Append(c.TextLength);
            sb.AppendLine();
        }
    }
}
