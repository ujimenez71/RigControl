namespace RigControl.Core.Bindings;

/// <summary>Carga un perfil, lo compila, lo cruza con el catálogo y pasa el Conflict Guard (lo que hace "check").</summary>
public static class ProfileLoader
{
    /// <summary>
    /// Devuelve null si el perfil no existe, no es JSON válido, tiene errores o bindings duplicados.
    /// Con verbose = false solo se imprimen los errores (para "run auto", que carga muchos a la vez).
    /// Lanza IOException si el archivo está bloqueado (p. ej. el editor aún lo está guardando).
    /// </summary>
    public static CompiledProfile? LoadAndCheck(string path, bool verbose = true)
    {
        var name = Path.GetFileName(path);
        void Info(string text) { if (verbose) Log.Info(text); }
        void Error(string text) => Log.Error(verbose ? text : $"  {name}: {text.Trim()}");

        Info($"Perfil: {path}");
        if (!File.Exists(path))
        {
            Error("No existe.");
            return null;
        }

        ProfileFile file;
        try
        {
            file = ProfileFile.Load(path);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            Error($"JSON no válido: {ex.Message}");
            return null;
        }

        var (profile, errors) = ProfileCompiler.Compile(file);
        foreach (var e in errors) Error($"ERROR  {e}");
        if (profile is null)
        {
            Error($"{errors.Count} error(es). Corrige el perfil.");
            return null;
        }
        profile.SourcePath = Path.GetFullPath(path);

        if (verbose)
        {
            var catalog = DeviceCatalog.Load();
            for (var d = 0; d < profile.DeviceKeys.Length; d++)
            {
                var entry = catalog.Find(profile.DeviceKeys[d]);
                Log.Info(entry is null
                    ? $"AVISO  \"{profile.DeviceAliases[d]}\" ({profile.DeviceKeys[d]}) no está en el catálogo; comprueba la clave con \"catalog\"."
                    : $"  {profile.DeviceAliases[d],-10} -> {entry.Alias}");
            }
            if (profile.Aircraft.Length > 0)
                Log.Info($"  aviones   -> {string.Join(", ", profile.Aircraft)}");
            foreach (var r in profile.Lights)
                Log.Info($"  luz {r.Name,-15} <- {r.Text}");
            for (var i = 0; i < profile.Icp.Length; i++)
                Log.Info($"  icp {i + 1}         <- {profile.Icp[i].Source}");
        }

        var findings = ConflictGuard.Check(profile);
        if (verbose)
        {
            Log.Info($"\nConflict Guard: {(findings.Count == 0 ? "sin incidencias" : $"{findings.Count} incidencia(s)")}");
            foreach (var f in findings) Log.Info($"  {f}");
        }

        if (findings.Any(f => f.Severity == Severity.Error))
        {
            if (!verbose)
                foreach (var f in findings.Where(f => f.Severity == Severity.Error)) Error(f.ToString());
            Error("Hay bindings duplicados. Corrige el perfil.");
            return null;
        }

        Info($"\nOK: {profile.Bindings.Length} binding(s), {profile.Inputs.Length} entrada(s) vigilada(s).");
        return profile;
    }
}
