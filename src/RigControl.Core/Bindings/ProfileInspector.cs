namespace RigControl.Core.Bindings;

/// <summary>Un dispositivo del perfil, cruzado con el catálogo y con lo que hay conectado.</summary>
public sealed record DeviceReport(string Alias, string Key, string? CatalogName, bool? Connected);

/// <summary>Una fila de la tabla de bindings, ya en texto.</summary>
public sealed record BindingReport(int Number, string Trigger, string Gesture, string Condition, string Action,
                                   string Kind, int RepeatMs);

/// <summary>Una regla de luz del Bravo.</summary>
public sealed record LightReport(string Light, string Rule);

/// <summary>
/// Resultado de revisar un perfil (lo mismo que "check", pero devuelto en vez de escrito en el log):
/// para la pantalla Perfiles. Nunca lanza: los errores van en <see cref="Errors"/>.
/// </summary>
public sealed class ProfileReport
{
    public required string Path { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Name { get; init; } = "";
    public string[] Aircraft { get; init; } = [];
    public List<string> Errors { get; } = [];
    public List<Finding> Findings { get; } = [];
    public List<DeviceReport> Devices { get; } = [];
    public List<BindingReport> Bindings { get; } = [];
    public List<LightReport> Lights { get; } = [];

    /// <summary>Se puede usar: compila y no tiene duplicados (lo mismo que acepta "run").</summary>
    public bool Ok => Errors.Count == 0 && !Findings.Any(f => f.Severity == Severity.Error);
}

public static class ProfileInspector
{
    /// <param name="connectedKeys">Claves de los dispositivos conectados ahora (null = no se sabe).</param>
    public static ProfileReport Inspect(string path, DeviceCatalog? catalog = null, ISet<string>? connectedKeys = null)
    {
        ProfileFile file;
        try
        {
            file = ProfileFile.Load(path);
        }
        catch (Exception ex)
        {
            var bad = new ProfileReport { Path = path, Name = System.IO.Path.GetFileNameWithoutExtension(path) };
            bad.Errors.Add(ex is System.Text.Json.JsonException ? $"JSON no válido: {ex.Message}" : ex.Message);
            return bad;
        }

        var (profile, errors) = ProfileCompiler.Compile(file);
        var report = new ProfileReport
        {
            Path = path,
            Name = string.IsNullOrWhiteSpace(file.Name) ? System.IO.Path.GetFileNameWithoutExtension(path) : file.Name,
            Aircraft = [.. file.Aircraft],
        };
        report.Errors.AddRange(errors);

        // Los dispositivos se muestran aunque el perfil tenga errores (vienen del JSON tal cual).
        foreach (var (alias, key) in file.Devices)
        {
            // "VID:PID" en el perfil encaja con "VID:PID:serial" del catálogo o de lo conectado.
            var k0 = key.Trim();
            bool Same(string k) => k.Equals(k0, StringComparison.OrdinalIgnoreCase)
                                   || k.StartsWith(k0 + ":", StringComparison.OrdinalIgnoreCase);
            var entry = catalog?.Devices.FirstOrDefault(d => Same(d.Key));
            bool? connected = connectedKeys is null ? null : connectedKeys.Any(Same);
            report.Devices.Add(new DeviceReport(alias, key, entry is null ? null : $"{entry.Alias} ({entry.ProductName})", connected));
        }

        if (profile is null) return report;

        report.Findings.AddRange(ConflictGuard.Check(profile));
        foreach (var b in profile.Bindings)
            report.Bindings.Add(new BindingReport(b.Number, b.Trigger, b.Gesture.ToString().ToLowerInvariant(),
                b.When?.Describe() ?? "", b.Action, KindName(b.Output.Kind), b.RepeatMs));
        foreach (var r in profile.Lights)
            report.Lights.Add(new LightReport(r.Name, r.Text));
        return report;
    }

    public static string KindName(ActionKind kind) => kind switch
    {
        ActionKind.SimEvent => "MSFS K:",
        ActionKind.InputEvent => "MSFS B:",
        ActionKind.LVar => "MSFS L:",
        ActionKind.Key => "Teclado",
        ActionKind.DcsBios => "DCS-BIOS",
        _ => "Solo texto",
    };
}
