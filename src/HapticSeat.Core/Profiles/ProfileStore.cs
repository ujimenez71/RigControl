using System.Text.Json;

namespace HapticSeat.Profiles;

/// <summary>
/// Lee y guarda perfiles y ajustes como JSON legible (se pueden editar a mano; se admiten comentarios).
/// Un archivo por perfil, con el nombre del perfil saneado como nombre de archivo.
/// </summary>
public sealed class ProfileStore(string? rootDir = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string RootDir { get; } = rootDir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HapticSeat");

    public string ProfilesDir => Path.Combine(RootDir, "profiles");

    private string SettingsPath => Path.Combine(RootDir, "settings.json");

    /// <summary>Todos los perfiles, con "General" el primero (se crea si no existe). Los JSON rotos se saltan.</summary>
    public List<Profile> LoadAll(Action<string>? warn = null)
    {
        Directory.CreateDirectory(ProfilesDir);
        var profiles = new List<Profile>();
        foreach (var file in Directory.EnumerateFiles(ProfilesDir, "*.json"))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(file), Json);
                if (profile is not null && !string.IsNullOrWhiteSpace(profile.Name)) profiles.Add(profile);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                warn?.Invoke($"Perfil no válido, se ignora: {Path.GetFileName(file)} ({ex.Message})");
            }
        }

        if (!profiles.Any(p => p.IsDefault))
        {
            var general = new Profile();
            Save(general);
            profiles.Add(general);
        }

        return [.. profiles.OrderBy(p => !p.IsDefault).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public void Save(Profile profile)
    {
        Directory.CreateDirectory(ProfilesDir);
        WriteAtomic(PathFor(profile.Name), JsonSerializer.Serialize(profile, Json));
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Ajustes ilegibles: se arranca con los de fábrica (el archivo se reescribe al guardar)
        }
        return new AppSettings();
    }

    public void SaveSettings(AppSettings settings)
    {
        Directory.CreateDirectory(RootDir);
        WriteAtomic(SettingsPath, JsonSerializer.Serialize(settings, Json));
    }

    /// <summary>El perfil para un avión: el primero (por orden) cuyos patrones encajen; si no, "General".</summary>
    public static Profile ForAircraft(IReadOnlyList<Profile> profiles, string aircraft) =>
        profiles.FirstOrDefault(p => !p.IsDefault && p.Matches(aircraft))
        ?? profiles.First(p => p.IsDefault);

    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 60 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private string PathFor(string name) => Path.Combine(ProfilesDir, name.Trim() + ".json");

    /// <summary>Escribe a un temporal y lo renombra: un corte a mitad no deja el JSON a medias.</summary>
    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
