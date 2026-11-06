// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The process plumbing every <c>psql</c> invocation shares (D-10): no shell, no window, standard input
/// closed at start, standard output and error pumped to the caller's consumers until end of stream, and a
/// stop token that kills the whole process tree and then waits for the exit, so a caller that stops a run
/// always gets back a process that is gone. Start failures (a missing or non-executable path) propagate as
/// the runtime raises them; their messages name the path, never an argument or a value.
/// </summary>
internal static class PsqlProcess
{
    private const int PumpBufferLength = 4096;

    /// <summary>Receives a chunk of decoded output as it arrives.</summary>
    public delegate void ConsumeText(ReadOnlySpan<char> text);

    /// <summary>What a run proves: the process id, whether the stop token killed it, and the exit code it reported.</summary>
    public sealed record Completion(int ProcessId, bool Killed, int ExitCode);

    /// <summary>The start info shared by every invocation; the caller adds arguments and the environment.</summary>
    public static ProcessStartInfo CreateStartInfo(string executablePath, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("The psql path must be fully qualified.", nameof(executablePath));
        }

        return new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
        };
    }

    /// <summary>
    /// Starts the process and waits for it to exit. When <paramref name="stop"/> fires first, the process
    /// tree is killed and the exit is still awaited; the completion then says so. Both output streams are
    /// drained to end of stream before returning, which the exit of the whole tree guarantees.
    /// </summary>
    public static async Task<Completion> RunAsync(
        ProcessStartInfo startInfo,
        ConsumeText? consumeStandardOutput,
        ConsumeText? consumeStandardError,
        CancellationToken stop
    )
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        using Process process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("The psql process did not start.");
        int processId = process.Id;
        process.StandardInput.Close();
        Task standardOutput = PumpAsync(process.StandardOutput, consumeStandardOutput);
        Task standardError = PumpAsync(process.StandardError, consumeStandardError);

        bool killed = false;
        try
        {
            await process.WaitForExitAsync(stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            killed = true;
            KillTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        await Task.WhenAll(standardOutput, standardError);
        return new Completion(processId, killed, process.ExitCode);
    }

    private static async Task PumpAsync(StreamReader reader, ConsumeText? consume)
    {
        char[] buffer = new char[PumpBufferLength];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            consume?.Invoke(buffer.AsSpan(0, read));
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited on its own between the stop and the kill; the exit is awaited either way.
        }
    }
}
