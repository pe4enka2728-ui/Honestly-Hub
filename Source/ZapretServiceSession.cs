using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace VpnZapretSwitch;

internal static class ZapretServiceSession
{
    private const string Menu = "Select option (0-11):";
    private const string Pick = "Input file index (number):";
    private static readonly Regex Pause = new(@"Press any key to continue\s*\.\s*\.\s*\.", RegexOptions.IgnoreCase);

    public static async Task InstallAndUpdateAsync(Settings settings, Action<string>? report)
    {
        // service.bat hardcodes this name. Refuse to install/manage a different service.
        if (settings.ServiceName != "zapret") throw new InvalidOperationException("Для service.bat имя службы должно быть zapret.");
        using var console = new PseudoConsoleSession(settings.ServiceScript, settings.ZapretFolder);
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(45, settings.TimeoutSeconds));
        await console.ExpectAsync(s => s.Contains(Menu, StringComparison.Ordinal), timeout, "Главное меню service.bat не появилось.");
        report?.Invoke("service.bat: 1 — Install Service…");
        await console.SendAsync("1\r");
        string choices = await console.ExpectAsync(s => s.Contains(Pick, StringComparison.Ordinal), timeout,
            "service.bat не предложил список стратегий.");
        int index = ZapretConsoleProtocol.FindStrategyIndex(choices, settings.StrategyFile);
        report?.Invoke($"service.bat: выбрана {settings.StrategyFile} (пункт {index})…");
        await console.SendAsync(index + "\r");
        await console.ExpectAsync(s => Pause.IsMatch(s), timeout, "Установка службы не завершилась.");
        ValidateInstalledStrategy(settings);
        var service = new NativeService(settings.ServiceName);
        var wait = Stopwatch.StartNew();
        while (service.GetState() != NativeService.Running)
        {
            if (wait.Elapsed > timeout) throw new TimeoutException("service.bat не запустил службу Zapret.");
            await Task.Delay(250);
        }
        // pause consumes one key, not a line of pre-buffered menu commands.
        await console.SendAsync("\r");
        await console.ExpectAsync(s => s.Contains(Menu, StringComparison.Ordinal), timeout, "service.bat не вернулся в главное меню после установки.");
        report?.Invoke("service.bat: 7 — Update IPSet List…");
        string list = Path.Combine(settings.ZapretFolder, "lists", "ipset-all.txt");
        byte[]? backup = File.Exists(list) ? await File.ReadAllBytesAsync(list) : null;
        DateTime updateStarted = DateTime.UtcNow;
        try
        {
            await console.SendAsync("7\r");
            string completed = await console.ExpectAsync(s => Pause.IsMatch(s), TimeSpan.FromSeconds(180),
                "Обновление IPSet не завершилось за 180 секунд.");
            // The script echoes Finished even after curl errors. Verify the actual list.
            if (!completed.Contains("Finished", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(completed, @"curl:\s*\(\d+\)", RegexOptions.IgnoreCase)
                || !File.Exists(list) || File.GetLastWriteTimeUtc(list) < updateStarted.AddSeconds(-2)
                || !ZapretConsoleProtocol.IsValidIpSet(await File.ReadAllLinesAsync(list)))
                throw new IOException("service.bat завершил пункт 7, но корректный IPSet не получен. Проверьте интернет и повторите.");
        }
        catch
        {
            // Stop the owned downloader before restoring its output file.
            console.Stop();
            if (backup is not null) await File.WriteAllBytesAsync(list, backup);
            throw;
        }
        // Do not press another key here: the requested final state is this last pause.
        // Dispose closes only our pseudoconsole, leaving the SCM-managed service running.
        report?.Invoke("Служба установлена, IPSet обновлён. Закрываем service.bat.");
    }

    private static void ValidateInstalledStrategy(Settings settings)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
        string actual = key?.GetValue("zapret-discord-youtube") as string ?? "";
        string image = key?.GetValue("ImagePath") as string ?? "";
        string exe = image.StartsWith('"') ? image[1..].Split('"')[0] : image.Split(' ', 2)[0];
        if (!string.Equals(actual, Path.GetFileNameWithoutExtension(settings.StrategyFile), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFullPath(exe), Path.GetFullPath(settings.WinwsExe), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("service.bat не подтвердил установку выбранной стратегии из настроенной папки.");
    }
}

// Windows 10 1809+ ConPTY supplies a real console to set /p, pause and chcp.
// No SendKeys, shared desktop console or fixed timing is used for menu input.
internal sealed class PseudoConsoleSession : IDisposable
{
    private IntPtr pseudoConsole;
    private readonly FileStream input;
    private readonly FileStream output;
    private readonly Process process;
    private readonly Task reader;
    private readonly StringBuilder transcript = new();
    private readonly object sync = new();
    private long consumed;
    private bool stopped;
    private Exception? readError;
    private OwnedProcessJob? job;
    public int ProcessId => process.Id;
    public bool HasExited { get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } } }

    public PseudoConsoleSession(string script, string directory)
        : this(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            new StringBuilder($"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /d /v:off /s /c \"\"{script}\" admin\""), directory) { }

    public PseudoConsoleSession(string executable, string[] arguments, string directory)
        : this(executable, new StringBuilder(Quote(executable) + " " + string.Join(" ", arguments.Select(Quote))), directory) { }

    private PseudoConsoleSession(string executable, StringBuilder command, string directory)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)) throw Error();
        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        { inputRead.Dispose(); inputWrite.Dispose(); throw Error(); }
        input = new FileStream(inputWrite, FileAccess.Write, 4096, false);
        output = new FileStream(outputRead, FileAccess.Read, 4096, false);
        IntPtr attributes = IntPtr.Zero;
        bool attributesInitialized = false;
        try
        {
            int hresult = CreatePseudoConsole(new(160, 80), inputRead, outputWrite, 0, out pseudoConsole);
            Marshal.ThrowExceptionForHR(hresult);
            IntPtr bytes = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            attributes = Marshal.AllocHGlobal(bytes);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw Error();
            attributesInitialized = true;
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20016, pseudoConsole,
                (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw Error();
            var start = new StartupInfoEx { Info = new() { Size = Marshal.SizeOf<StartupInfoEx>() }, Attributes = attributes };
            // Handles are associated with ConPTY, never inherited by unrelated children.
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x80004,
                IntPtr.Zero, directory, ref start, out var created)) throw Error();
            try
            {
                job = new OwnedProcessJob(created.Process);
                process = Process.GetProcessById((int)created.Id);
                if (ResumeThread(created.Thread) == uint.MaxValue) throw Error();
            }
            catch { TerminateProcess(created.Process, 1); throw; }
            finally { CloseHandle(created.Process); CloseHandle(created.Thread); }
            reader = Task.Run(ReadOutput);
        }
        catch
        {
            input.Dispose(); output.Dispose();
            job?.Dispose();
            if (pseudoConsole != IntPtr.Zero) ClosePseudoConsole(pseudoConsole);
            pseudoConsole = IntPtr.Zero;
            throw;
        }
        finally
        {
            inputRead.Dispose(); outputWrite.Dispose();
            if (attributes != IntPtr.Zero)
            {
                if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
        }
    }

    private void ReadOutput()
    {
        // ConPTY's transport is UTF-8 even when the hosted batch switches code pages.
        using var text = new StreamReader(output, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        char[] buffer = new char[4096];
        try
        {
            int count;
            while ((count = text.Read(buffer, 0, buffer.Length)) > 0)
                lock (sync)
                {
                    transcript.Append(buffer, 0, count);
                    if (transcript.Length > 4_000_000) throw new IOException("Слишком большой вывод service.bat.");
                }
        }
        catch (Exception ex) { lock (sync) readError = ex; }
    }

    public async Task SendAsync(string keys)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(keys);
        await input.WriteAsync(bytes);
        await input.FlushAsync();
    }

    public async Task<string> ExpectAsync(Func<string, bool> match, TimeSpan timeout, string error)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            lock (sync)
            {
                string value = ZapretConsoleProtocol.StripTerminal(transcript.ToString((int)consumed, transcript.Length - (int)consumed));
                if (match(value)) { consumed = transcript.Length; return value; }
                if (readError is not null) throw new IOException(error + " Не удалось прочитать консоль.", readError);
            }
            if (process.HasExited) throw new IOException(error + $" service.bat завершился с кодом {process.ExitCode}.");
            await Task.Delay(100);
        }
        throw new TimeoutException(error + " Ввод прекращён; автоматическая консоль будет закрыта.");
    }
    public void Stop()
    {
        if (stopped) return;
        stopped = true;
        // Only the process tree created by this session; no global cmd.exe termination.
        if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); }
    }
    public void Dispose()
    {
        try { Stop(); }
        finally
        {
            input.Dispose();
            // Drain output concurrently while closing ConPTY to avoid ClosePseudoConsole deadlock.
            if (pseudoConsole != IntPtr.Zero) { ClosePseudoConsole(pseudoConsole); pseudoConsole = IntPtr.Zero; }
            output.Dispose(); process.Dispose(); job?.Dispose();
        }
    }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static Win32Exception Error() => new(Marshal.GetLastWin32Error(), "Не удалось создать консоль service.bat.");
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Coord(short X, short Y);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint Id, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr security, int size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
}
