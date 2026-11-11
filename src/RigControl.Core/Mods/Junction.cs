using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RigControl.Core.Mods;

/// <summary>
/// Uniones de directorio (junctions, como "mklink /J"): no necesitan administrador ni el modo
/// desarrollador, a diferencia de los enlaces simbólicos. Solo valen para carpetas locales.
/// Quitar una unión borra el enlace, nunca lo que hay dentro de la carpeta a la que apunta.
/// </summary>
public static class Junction
{
    /// <summary>Crea la unión "link" apuntando a la carpeta "target" (que debe existir). "link" no debe existir.</summary>
    public static void Create(string link, string target)
    {
        link = Path.GetFullPath(link);
        target = Path.GetFullPath(target).TrimEnd('\\');
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException($"no existe {target}");
        if (Path.Exists(link)) throw new IOException($"ya existe {link}");

        Directory.CreateDirectory(link);
        try
        {
            using var handle = CreateFile(link, GenericWrite, 0, 0, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

            var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
            var print = Encoding.Unicode.GetBytes(target);
            // REPARSE_DATA_BUFFER de un punto de montaje: cabecera de 8 bytes + 4 ushort + rutas terminadas en \0.
            var dataLength = 8 + substitute.Length + 2 + print.Length + 2;
            var buffer = new byte[8 + dataLength];
            BitConverter.TryWriteBytes(buffer.AsSpan(0), IoReparseTagMountPoint);
            BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)dataLength);
            BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)0);                              // SubstituteNameOffset
            BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)substitute.Length);             // SubstituteNameLength
            BitConverter.TryWriteBytes(buffer.AsSpan(12), (ushort)(substitute.Length + 2));       // PrintNameOffset
            BitConverter.TryWriteBytes(buffer.AsSpan(14), (ushort)print.Length);                  // PrintNameLength
            substitute.CopyTo(buffer, 16);
            print.CopyTo(buffer, 16 + substitute.Length + 2);

            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, 0, 0, out _, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            try { Directory.Delete(link); } catch { /* lo dejamos: está vacía */ }
            throw;
        }
    }

    /// <summary>¿Es una unión o enlace simbólico de carpeta (y no una carpeta de verdad)?</summary>
    public static bool IsLink(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null;
    }

    /// <summary>Carpeta a la que apunta, completa; null si no es un enlace.</summary>
    public static string? TargetOf(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || !info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not { } target) return null;
        if (target.StartsWith(@"\??\", StringComparison.Ordinal)) target = target[4..];
        return Path.GetFullPath(target, Path.GetDirectoryName(info.FullName)!).TrimEnd('\\');
    }

    /// <summary>Quita el enlace. Se niega con una carpeta de verdad (para eso no hay método: nunca se borran mods).</summary>
    public static void Remove(string path)
    {
        if (!IsLink(path)) throw new InvalidOperationException($"{path} no es un enlace: no se borra");
        Directory.Delete(path, recursive: false); // con un punto de reanálisis borra solo el enlace
    }

    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000, FileFlagOpenReparsePoint = 0x00200000;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint FsctlSetReparsePoint = 0x000900A4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize, nint outBuffer, int outSize,
                                               out int returned, nint overlapped);
}
