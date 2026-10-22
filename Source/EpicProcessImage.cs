using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VpnZapretSwitch;

// Query the image path directly. Enumerating MainModule can return null while
// Epic's bootstrap process is still loading; that is not a failed installation.
internal static class EpicProcessImage
{
    internal static string? Read(Process process) => Resolve(() => process.HasExited, () =>
    {
        using var handle = OpenProcess(0x1000, false, process.Id); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new StringBuilder(32768); uint length = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(handle, 0, buffer, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return buffer.ToString();
    });

    internal static string? Resolve(Func<bool> exited, Func<string> query)
    {
        if (exited()) return null;
        try
        {
            string path = query();
            if (exited()) return null;
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Windows не вернула путь процесса Epic.");
            return Path.GetFullPath(path);
        }
        catch (Win32Exception error)
        {
            if (exited()) return null;
            throw new InvalidOperationException($"Не удалось прочитать путь работающего процесса Epic (Windows {error.NativeErrorCode}).", error);
        }
        catch (InvalidOperationException) when (exited()) { return null; }
    }
    [DllImport("kernel32.dll", SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", EntryPoint="QueryFullProcessImageNameW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
}
