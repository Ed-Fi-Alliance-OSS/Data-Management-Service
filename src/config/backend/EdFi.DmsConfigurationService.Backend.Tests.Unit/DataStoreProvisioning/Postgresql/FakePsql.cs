// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// A stand-in for the <c>psql</c> executable: a batch file on Windows (run by <c>cmd.exe</c>) or a
/// <c>/bin/sh</c> script elsewhere, generated into its own temporary directory with every path baked in,
/// because the runner passes the fake nothing but the D-10 allowlist. A blocking fake dumps the environment
/// it received to a signal file (written under a temporary name and renamed, so a watcher that sees the
/// name sees complete content) and then waits, polling once a second for at most ten minutes, until the
/// fake's stop file exists; a fake that exits while a child keeps its pipes starts that child on the same
/// wait before it signals and exits. Disposal writes the stop file first, so every process a fake started
/// ends on its own within about a second, whatever the code under test did with it. The output fakes copy
/// a data file to a stream and exit with a chosen code, which keeps every line verbatim without shell quoting.
/// </summary>
internal sealed class FakePsql : IDisposable, IAsyncDisposable
{
    /// <summary>Variables the platform shell adds to its own environment whatever it was started with.</summary>
    public static readonly string[] ShellInjectedVariables = OperatingSystem.IsWindows()
        ? ["COMSPEC", "PATHEXT", "PROMPT"]
        : ["PWD", "OLDPWD", "SHLVL", "_"];

    private const string StopFileName = "stop";
    private const string ChildScriptName = "child";
    private const string StandardErrorDataFile = "stderr.txt";
    private const string StandardOutputDataFile = "stdout.txt";
    private const int DeleteAttempts = 10;

    private FakePsql(string directory, string executablePath)
    {
        Directory = directory;
        ExecutablePath = executablePath;
    }

    public string Directory { get; }

    public string ExecutablePath { get; }

    /// <summary>The file whose appearance ends every wait loop a fake runs.</summary>
    public string StopFilePath => Path.Combine(Directory, StopFileName);

    /// <summary>A fake that writes its environment to <paramref name="signalPath"/> and then waits for the stop file.</summary>
    public static FakePsql Blocking(string signalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalPath);
        return Create(directory =>
            [.. EnvironmentDumpLines(signalPath), .. WaitForStopLines(directory, exitCode: 0)]
        );
    }

    /// <summary>
    /// A fake that starts a child inheriting its output pipes (the child waits for the stop file), writes
    /// <paramref name="signalPath"/>, and exits 0 while the child lives on.
    /// </summary>
    public static FakePsql HoldingPipeAfterExit(string signalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalPath);
        return Create(directory =>
        {
            string child = WriteScript(
                Path.Combine(directory, ScriptFileName(ChildScriptName)),
                WaitForStopLines(directory, exitCode: 0)
            );
            string[] start = OperatingSystem.IsWindows()
                ? [$"start \"\" /b cmd /c \"{child}\""]
                : [$"sh '{child}' &"];
            return [.. start, .. SignalLines(signalPath, "started"), ExitLine(0)];
        });
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

    /// <summary>Stops every process the fake started and makes one attempt to remove its directory.</summary>
    public void Dispose()
    {
        WriteStopFile();
        TryDeleteDirectory();
    }

    /// <summary>Stops every process the fake started and removes its directory once they have let go of it.</summary>
    public async ValueTask DisposeAsync()
    {
        WriteStopFile();
        for (int attempt = 0; attempt < DeleteAttempts && !TryDeleteDirectory(); attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
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

    /// <summary>Polls for the stop file once a second, at most six hundred times, then exits.</summary>
    private static string[] WaitForStopLines(string directory, int exitCode)
    {
        string stop = Path.Combine(directory, StopFileName);
        return OperatingSystem.IsWindows()
            ?
            [
                "set /a waited=0",
                ":wait",
                $"if exist \"{stop}\" exit /b {exitCode}",
                "set /a waited+=1",
                $"if %waited% geq 600 exit /b {exitCode}",
                "ping -n 2 127.0.0.1 >nul",
                "goto wait",
            ]
            :
            [
                "waited=0",
                $"while [ ! -e '{stop}' ] && [ \"$waited\" -lt 600 ]; do",
                "  sleep 1",
                "  waited=$((waited+1))",
                "done",
                ExitLine(exitCode),
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
