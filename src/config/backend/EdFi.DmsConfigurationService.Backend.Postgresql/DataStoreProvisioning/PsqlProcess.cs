// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The process plumbing every <c>psql</c> invocation shares (D-10): no shell, no window, standard input
/// closed at start, standard output and error pumped to the caller's consumers, and a stop token that
/// kills the whole process tree. Every wait after the stop, and every wait for the output streams to
/// reach end of stream, is bounded, because a parent's exit proves nothing about descendants that may
/// have inherited its pipes: the completion says whether termination was confirmed (the process exited
/// and both streams ended) or is unconfirmed (something still holds a pipe, or the process did not exit
/// within the bound), and a caller treats the latter as an outcome in doubt. Start failures (a missing or
/// non-executable path) propagate as the runtime raises them; their messages name the path, never an
/// argument or a value.
/// </summary>
internal static class PsqlProcess
{
    /// <summary>The default bound for a stopped process to exit, and for the output streams to end after an exit.</summary>
    public static readonly TimeSpan DefaultTerminationBound = TimeSpan.FromSeconds(5);

    private const int PumpBufferLength = 4096;

    /// <summary>Receives a chunk of decoded output as it arrives.</summary>
    public delegate void ConsumeText(ReadOnlySpan<char> text);

    /// <summary>What the run proved about the process when it returned.</summary>
    public enum Termination
    {
        /// <summary>The process exited on its own and both output streams ended: nothing of it is left running.</summary>
        Exited,

        /// <summary>The stop token fired, the tree was killed, the exit was observed, and both streams ended.</summary>
        Killed,

        /// <summary>
        /// The process did not exit within the bound after the stop, or it exited but a stream did not end
        /// within the bound (a descendant inherited a pipe). The process or a descendant may still be running.
        /// </summary>
        Unconfirmed,
    }

    /// <summary>The process id, what was proved about its termination, and the exit code when the exit was observed.</summary>
    public sealed record Completion(int ProcessId, Termination Termination, int? ExitCode);

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
    /// Starts the process and waits for it to exit and for both output streams to end. When
    /// <paramref name="stop"/> fires first, the process tree is killed; when it fires while the streams of
    /// an already exited process are still open, the tree is killed again on a best-effort basis so a
    /// descendant holding a pipe is reached where the platform still lets it be found. The exit after a
    /// stop and the end of the streams after an exit are each awaited at most
    /// <paramref name="terminationBound"/>, measured on <paramref name="timeProvider"/>; past the bound the
    /// run returns <see cref="Termination.Unconfirmed"/> and the still-open readers are closed with the process.
    /// </summary>
    public static async Task<Completion> RunAsync(
        ProcessStartInfo startInfo,
        ConsumeText? consumeStandardOutput,
        ConsumeText? consumeStandardError,
        TimeSpan terminationBound,
        TimeProvider timeProvider,
        CancellationToken stop
    )
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(terminationBound, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);

        using Process process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("The psql process did not start.");
        int processId = process.Id;
        process.StandardInput.Close();
        Task streams = Task.WhenAll(
            PumpAsync(process.StandardOutput, consumeStandardOutput),
            PumpAsync(process.StandardError, consumeStandardError)
        );

        bool stopped = false;
        try
        {
            await process.WaitForExitAsync(stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            stopped = true;
            KillTree(process);
        }

        using CancellationTokenSource bound = new(terminationBound, timeProvider);
        bool exited =
            !stopped
            || await CompletesWithinAsync(process.WaitForExitAsync(CancellationToken.None), bound.Token);
        bool ended = exited && await StreamsEndWithinAsync(streams, process, stopped, stop, bound.Token);
        if (!ended)
        {
            ObserveLater(streams);
            return new Completion(processId, Termination.Unconfirmed, exited ? process.ExitCode : null);
        }

        return new Completion(processId, stopped ? Termination.Killed : Termination.Exited, process.ExitCode);
    }

    /// <summary>
    /// Waits for both streams to end within the bound. A stop that arrives during this wait, for a process
    /// that exited on its own, is still acted on: the tree is killed once and the wait continues within the
    /// same bound.
    /// </summary>
    private static async Task<bool> StreamsEndWithinAsync(
        Task streams,
        Process process,
        bool alreadyStopped,
        CancellationToken stop,
        CancellationToken bound
    )
    {
        if (!alreadyStopped)
        {
            using CancellationTokenSource stopOrBound = CancellationTokenSource.CreateLinkedTokenSource(
                stop,
                bound
            );
            if (await CompletesWithinAsync(streams, stopOrBound.Token))
            {
                return true;
            }

            if (bound.IsCancellationRequested)
            {
                return false;
            }

            KillTree(process);
        }

        return await CompletesWithinAsync(streams, bound);
    }

    private static async Task<bool> CompletesWithinAsync(Task task, CancellationToken until)
    {
        try
        {
            await task.WaitAsync(until);
            return true;
        }
        catch (OperationCanceledException) when (until.IsCancellationRequested)
        {
            return false;
        }
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

    /// <summary>Kills the tree below a process that may already have exited; the kill is best effort on every path.</summary>
    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the stop and the kill; descendants reachable through it were handled.
        }
        catch (AggregateException)
        {
            // Part of the tree could not be killed; the bounded waits decide what the run can confirm.
        }
    }

    /// <summary>A pump abandoned past the bound ends when its reader is disposed with the process; its fault is observed, never raised.</summary>
    private static void ObserveLater(Task streams) =>
        _ = streams.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
}
