using System.Security.Cryptography;
using System.Text;

// Todo RigControl es solo para Windows (DirectInput, SimConnect): DPAPI siempre está.
#pragma warning disable CA1416

namespace RigControl.Core.Ai;

/// <summary>
/// Clave de la API de Anthropic, guardada cifrada con DPAPI (solo la puede descifrar este usuario de
/// Windows en este equipo) en %APPDATA%\RigControl\anthropic.key. Si no hay archivo, se usa la
/// variable ANTHROPIC_API_KEY si existe.
/// </summary>
public static class ApiKeyStore
{
    public static string KeyPath { get; } =
        Path.Combine(Path.GetDirectoryName(DeviceCatalog.DefaultPath)!, "anthropic.key");

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RigControl.Anthropic");

    /// <summary>La clave guardada, o la de ANTHROPIC_API_KEY, o null.</summary>
    public static string? Load()
    {
        try
        {
            if (File.Exists(KeyPath))
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), Entropy, DataProtectionScope.CurrentUser);
                var key = Encoding.UTF8.GetString(plain).Trim();
                if (key.Length > 0) return key;
            }
        }
        catch (CryptographicException ex)
        {
            Log.Warning($"No se pudo descifrar la clave de la API ({ex.Message}); vuelve a introducirla.");
        }
        var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    public static bool HasStoredKey => File.Exists(KeyPath);

    public static void Save(string key)
    {
        key = key.Trim();
        if (key.Length == 0) throw new ArgumentException("La clave está vacía.", nameof(key));
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyPath, data);
    }

    public static void Delete()
    {
        if (File.Exists(KeyPath)) File.Delete(KeyPath);
    }

    /// <summary>"sk-ant-…XXXX" para mostrar sin enseñar la clave.</summary>
    public static string Mask(string key) => key.Length <= 12 ? "…" : $"{key[..7]}…{key[^4..]}";
}
