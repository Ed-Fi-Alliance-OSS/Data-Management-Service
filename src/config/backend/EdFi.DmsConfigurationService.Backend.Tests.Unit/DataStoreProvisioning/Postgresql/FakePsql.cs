// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// A stand-in for the <c>psql</c> executable: a batch file on Windows (run by <c>cmd.exe</c>) or a
/// <c>/bin/sh</c> script elsewhere, generated into its own temporary directory with every path baked in,
/// because the runner passes the fake nothing but the D-10 allowlist. A blocking fake dumps the environment
/// it received to a signal file (written under a temporary name and renamed, so a watcher that sees the
/// name sees complete content) and then waits, polling once a second for at most ten minutes, until the
/// fake's stop file exists; a fake that exits while a child keeps its pipes starts that child on the same
/// wait (the child also writes a tick to standard error and to a tick file on every poll) before it signals
/// and exits. Every wait loop records its process id on Unix. Disposal writes the stop file, waits for
/// evidence that every recorded process is gone (killing a survivor itself, so the cleanup does not depend
/// on the code under test), and only then removes the directory: on Unix a running script does not keep
/// its directory from being deleted, and deleting the stop file early would leave the loop running. On
/// Windows a running batch file keeps its directory undeletable, so the deletion retries are the evidence.
/// The output fakes copy a data file to a stream and exit with a chosen code, which keeps every line
/// verbatim without shell quoting.
/// </summary>
internal sealed class FakePsql : IDisposable, IAsyncDisposable
{
    /// <summary>Variables the platform shell adds to its own environment whatever it was started with.</summary>
    public static readonly string[] ShellInjectedVariables = OperatingSystem.IsWindows()
        ? ["COMSPEC", "PATHEXT", "PROMPT"]
        : ["PWD", "OLDPWD", "SHLVL", "_"];

    private const string StopFileName = "stop";
    private const string PidsFileName = "pids";
    private const string ClosedMarkerName = "closed";
    private const string ChildScriptName = "child";
    private const string StandardErrorDataFile = "stderr.txt";
    private const string StandardOutputDataFile = "stdout.txt";
    private static readonly TimeSpan _exitEvidenceBound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _deleteBound = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan _signalReadRetryInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>The <see cref="Exception.HResult"/> of a Windows sharing violation (<c>ERROR_SHARING_VIOLATION</c>).</summary>
    private const int SharingViolationHResult = unchecked((int)0x80070020);

    private FakePsql(string directory, string executablePath)
    {
        Directory = directory;
        ExecutablePath = executablePath;
    }

    public string Directory { get; }

    public string ExecutablePath { get; }

    /// <summary>The file whose appearance ends every wait loop a fake runs.</summary>
    public string StopFilePath => Path.Combine(Directory, StopFileName);

    /// <summary>The file a pipe-holding child writes when a write to its inherited standard error fails because the reading end closed.</summary>
    public string ClosedMarkerPath => Path.Combine(Directory, ClosedMarkerName);

    /// <summary>A fake that writes its environment to <paramref name="signalPath"/> and then waits for the stop file.</summary>
    public static FakePsql Blocking(string signalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalPath);
        return Create(directory =>
            [.. EnvironmentDumpLines(signalPath), .. WaitForStopLines(directory, tick: false)]
        );
    }

    /// <summary>
    /// A fake that starts a child inheriting its output pipes (the child waits for the stop file, ticking
    /// on every poll), writes <paramref name="signalPath"/>, and exits 0 while the child lives on.
    /// </summary>
    public static FakePsql HoldingPipeAfterExit(string signalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalPath);
        return Create(directory =>
        {
            string child = WriteScript(
                Path.Combine(directory, ScriptFileName(ChildScriptName)),
                WaitForStopLines(directory, tick: true)
            );
            string[] start = OperatingSystem.IsWindows()
                ? [$"start \"\" /b cmd /c \"{child}\""]
                : [$"sh '{child}' &"];
            return [.. start, .. SignalLines(signalPath, "started"), ExitLine(0)];
        });
    }

    /// <summary>A fake that never connects, writes <paramref name="signalPath"/> as its last act, and exits 0, as a committed replay ends.</summary>
    public static FakePsql ExitingSuccessfully(string signalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalPath);
        return Create(_ => [.. SignalLines(signalPath, "exited"), ExitLine(0)]);
    }

    /// <summary>A fake that writes <paramref name="lines"/> to standard error and exits with <paramref name="exitCode"/>.</summary>
    public static FakePsql WritingStandardError(IReadOnlyList<string> lines, int exitCode) =>
        Create(directory =>
        {
            string data = WriteData(directory, StandardErrorDataFile, lines);
            return
            [
                OperatingSystem.IsWindows() ? $"type \"{data}\" 1>&2" : $"cat '{data}' >&2",
                ExitLine(exitCode),
            ];
        });

    /// <summary>A fake that writes <paramref name="lines"/> to standard output and exits with <paramref name="exitCode"/>.</summary>
    public static FakePsql WritingStandardOutput(IReadOnlyList<string> lines, int exitCode) =>
        Create(directory =>
        {
            string data = WriteData(directory, StandardOutputDataFile, lines);
            return [OperatingSystem.IsWindows() ? $"type \"{data}\"" : $"cat '{data}'", ExitLine(exitCode)];
        });

    /// <summary>
    /// Completes when <paramref name="path"/> appears (created or renamed into place), watching with a
    /// <see cref="FileSystemWatcher"/> and checking once after the watch starts so an early arrival is not
    /// missed. The bound only guards against a hang when the fake never starts.
    /// </summary>
    public static async Task WaitForFileAsync(string path, TimeSpan bound)
    {
        string directory =
            Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The signal path has no directory.", nameof(path));
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using FileSystemWatcher watcher = new(directory, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.FileName,
        };
        watcher.Created += (_, _) => arrived.TrySetResult();
        watcher.Renamed += (_, _) => arrived.TrySetResult();
        watcher.EnableRaisingEvents = true;
        if (File.Exists(path))
        {
            arrived.TrySetResult();
        }

        Task first = await Task.WhenAny(arrived.Task, Task.Delay(bound));
        if (first != arrived.Task)
        {
            throw new TimeoutException("The fake psql did not signal its start within the bound.");
        }
    }

    /// <summary>
    /// Reads a signal file once <see cref="WaitForFileAsync"/> has seen it. On Windows the watcher reports
    /// the rename while <c>move</c> can still hold the renamed file open, so the read shares everything and
    /// retries a sharing violation until that handle is gone; any other failure is raised at once, and the
    /// bound only guards against a hang.
    /// </summary>
    public static async Task<string> ReadSignalAsync(string path, TimeSpan bound)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                await using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );
                using StreamReader reader = new(stream);
                return await reader.ReadToEndAsync();
            }
            catch (IOException exception)
                when (exception.HResult == SharingViolationHResult
                    && Stopwatch.GetElapsedTime(started) < bound
                )
            {
                await Task.Delay(_signalReadRetryInterval);
            }
        }
    }

    /// <summary>Parses a <c>set</c> / <c>env</c> dump into name-value pairs; names compare ignoring case.</summary>
    public static IReadOnlyDictionary<string, string> ParseEnvironmentDump(string dump)
    {
        Dictionary<string, string> variables = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in dump.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            int separator = trimmed.IndexOf('=');
            if (separator > 0)
            {
                variables[trimmed[..separator]] = trimmed[(separator + 1)..];
            }
        }

        return variables;
    }

    /// <summary>Stops every process the fake started and makes one attempt to remove its directory; the async form waits for the evidence.</summary>
    public void Dispose()
    {
        WriteStopFile();
        TryDeleteDirectory();
    }

    /// <summary>Stops every process the fake started, waits for evidence they are gone, then removes the directory.</summary>
    public async ValueTask DisposeAsync()
    {
        WriteStopFile();
        if (!OperatingSystem.IsWindows())
        {
            await WaitForRecordedProcessesToExitAsync();
        }

        long deadline = Stopwatch.GetTimestamp();
        while (!TryDeleteDirectory() && Stopwatch.GetElapsedTime(deadline) < _deleteBound)
        {
            await Task.Delay(_pollInterval);
        }
    }

    private void WriteStopFile()
    {
        try
        {
            File.WriteAllText(StopFilePath, string.Empty);
        }
        catch (IOException)
        {
            // The directory is already gone; nothing is waiting on the stop file.
        }
    }

    /// <summary>
    /// Waits for every process id a wait loop recorded to disappear; a process still alive past the bound
    /// is killed here, so the cleanup never depends on the kill the code under test performed.
    /// </summary>
    private async Task WaitForRecordedProcessesToExitAsync()
    {
        string pidsPath = Path.Combine(Directory, PidsFileName);
        if (!File.Exists(pidsPath))
        {
            return;
        }

        List<int> pids = [];
        foreach (string line in await File.ReadAllLinesAsync(pidsPath))
        {
            if (int.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                pids.Add(pid);
            }
        }

        long started = Stopwatch.GetTimestamp();
        while (pids.Exists(IsRunning) && Stopwatch.GetElapsedTime(started) < _exitEvidenceBound)
        {
            await Task.Delay(_pollInterval);
        }

        foreach (int survivor in pids.Where(IsRunning))
        {
            TryKill(survivor);
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TryKill(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Gone in the meantime.
        }
        catch (InvalidOperationException)
        {
            // Exited in the meantime.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Not ours to kill; the loop ends on its own within ten minutes.
        }
    }

    private bool TryDeleteDirectory()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static FakePsql Create(Func<string, string[]> bodyLinesFor)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dms-1438-fake-psql-" + Guid.NewGuid().ToString("N")
        );
        System.IO.Directory.CreateDirectory(directory);
        string executable = WriteScript(
            Path.Combine(directory, ScriptFileName("fake-psql")),
            bodyLinesFor(directory)
        );
        return new FakePsql(directory, executable);
    }

    private static string ScriptFileName(string stem) => OperatingSystem.IsWindows() ? stem + ".cmd" : stem;

    private static string WriteScript(string path, string[] bodyLines)
    {
        string[] lines = [OperatingSystem.IsWindows() ? "@echo off" : "#!/bin/sh", .. bodyLines];
        string newline = OperatingSystem.IsWindows() ? "\r\n" : "\n";
        File.WriteAllText(
            path,
            string.Join(newline, lines) + newline,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }

        return path;
    }

    private static string[] EnvironmentDumpLines(string signalPath)
    {
        string temporary = signalPath + ".tmp";
        return OperatingSystem.IsWindows()
            ? [$"set > \"{temporary}\"", $"move /y \"{temporary}\" \"{signalPath}\" >nul"]
            : [$"env > '{temporary}'", $"mv '{temporary}' '{signalPath}'"];
    }

    private static string[] SignalLines(string signalPath, string content)
    {
        string temporary = signalPath + ".tmp";
        return OperatingSystem.IsWindows()
            ? [$"echo {content} > \"{temporary}\"", $"move /y \"{temporary}\" \"{signalPath}\" >nul"]
            : [$"echo '{content}' > '{temporary}'", $"mv '{temporary}' '{signalPath}'"];
    }

    /// <summary>
    /// Records the process id (Unix), then polls for the stop file once a second, at most six hundred
    /// times, then exits 0. When asked to tick, every poll first writes a line to standard error through a
    /// child process (so the failure of that write, once the reading end is closed, is observed instead of
    /// ending the loop) and records the closed marker and exits when it fails. On Unix <c>SIGPIPE</c> is
    /// ignored for the same reason.
    /// </summary>
    private static string[] WaitForStopLines(string directory, bool tick)
    {
        string stop = Path.Combine(directory, StopFileName);
        string closed = Path.Combine(directory, ClosedMarkerName);
        if (OperatingSystem.IsWindows())
        {
            string[] tickLines = tick
                ? [$"cmd /c \"echo tick 1>&2\" || (echo closed>> \"{closed}\" & exit /b 0)"]
                : [];
            return
            [
                "set /a waited=0",
                ":wait",
                $"if exist \"{stop}\" exit /b 0",
                "set /a waited+=1",
                "if %waited% geq 600 exit /b 0",
                .. tickLines,
                "ping -n 2 127.0.0.1 >nul",
                "goto wait",
            ];
        }

        string[] unixTickLines = tick
            ? [$"  if ! /bin/echo tick >&2 2>/dev/null; then echo closed >> '{closed}'; exit 0; fi"]
            : [];
        return
        [
            "trap '' PIPE",
            $"echo $$ >> '{Path.Combine(directory, PidsFileName)}'",
            "waited=0",
            $"while [ ! -e '{stop}' ] && [ \"$waited\" -lt 600 ]; do",
            .. unixTickLines,
            "  sleep 1",
            "  waited=$((waited+1))",
            "done",
            ExitLine(0),
        ];
    }

    private static string ExitLine(int exitCode) =>
        OperatingSystem.IsWindows() ? $"exit /b {exitCode}" : $"exit {exitCode}";

    private static string WriteData(string directory, string fileName, IReadOnlyList<string> lines)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(
            path,
            string.Join('\n', lines) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
        return path;
    }
}
