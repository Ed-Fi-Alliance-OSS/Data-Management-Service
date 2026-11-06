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
/// name sees complete content) and then sleeps until killed; the output fakes copy a data file to a stream
/// and exit with a chosen code, which keeps every line verbatim without shell quoting.
/// </summary>
internal sealed class FakePsql : IDisposable
{
    /// <summary>Variables the platform shell adds to its own environment whatever it was started with.</summary>
    public static readonly string[] ShellInjectedVariables = OperatingSystem.IsWindows()
        ? ["COMSPEC", "PATHEXT", "PROMPT"]
        : ["PWD", "OLDPWD", "SHLVL", "_"];

    private const string StandardErrorDataFile = "stderr.txt";
    private const string StandardOutputDataFile = "stdout.txt";

    private FakePsql(string directory, string executablePath)
    {
        Directory = directory;
        ExecutablePath = executablePath;
    }

    public string Directory { get; }

    public string ExecutablePath { get; }

    /// <summary>A fake that writes its environment to <paramref name="signalPath"/> and then blocks until killed.</summary>
    public static FakePsql Blocking(string signalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalPath);
        string temporary = signalPath + ".tmp";
        return Create(_ =>
            OperatingSystem.IsWindows()
                ?
                [
                    "@echo off",
                    $"set > \"{temporary}\"",
                    $"move /y \"{temporary}\" \"{signalPath}\" >nul",
                    "ping -n 601 127.0.0.1 >nul",
                ]
                : ["#!/bin/sh", $"env > '{temporary}'", $"mv '{temporary}' '{signalPath}'", "sleep 600"]
        );
    }

    /// <summary>A fake that writes <paramref name="lines"/> to standard error and exits with <paramref name="exitCode"/>.</summary>
    public static FakePsql WritingStandardError(IReadOnlyList<string> lines, int exitCode) =>
        Create(directory =>
        {
            string data = WriteData(directory, StandardErrorDataFile, lines);
            return OperatingSystem.IsWindows()
                ? ["@echo off", $"type \"{data}\" 1>&2", $"exit /b {exitCode}"]
                : ["#!/bin/sh", $"cat '{data}' >&2", $"exit {exitCode}"];
        });

    /// <summary>A fake that writes <paramref name="lines"/> to standard output and exits with <paramref name="exitCode"/>.</summary>
    public static FakePsql WritingStandardOutput(IReadOnlyList<string> lines, int exitCode) =>
        Create(directory =>
        {
            string data = WriteData(directory, StandardOutputDataFile, lines);
            return OperatingSystem.IsWindows()
                ? ["@echo off", $"type \"{data}\"", $"exit /b {exitCode}"]
                : ["#!/bin/sh", $"cat '{data}'", $"exit {exitCode}"];
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

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A fake killed a moment ago may still hold its directory on Windows; the temp path is reclaimed later.
        }
    }

    private static FakePsql Create(Func<string, string[]> scriptLinesFor)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dms-1438-fake-psql-" + Guid.NewGuid().ToString("N")
        );
        System.IO.Directory.CreateDirectory(directory);
        string executable = Path.Combine(
            directory,
            OperatingSystem.IsWindows() ? "fake-psql.cmd" : "fake-psql"
        );
        string newline = OperatingSystem.IsWindows() ? "\r\n" : "\n";
        File.WriteAllText(
            executable,
            string.Join(newline, scriptLinesFor(directory)) + newline,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }

        return new FakePsql(directory, executable);
    }

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
