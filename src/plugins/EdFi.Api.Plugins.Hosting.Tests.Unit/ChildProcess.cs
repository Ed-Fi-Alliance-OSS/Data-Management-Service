// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// Runs a child process to completion under a deadline, capturing everything it wrote.
/// </summary>
/// <remarks>
/// Some claims about the loader can only be made in a process of its own, and this is what runs those
/// processes. It exists once rather than per caller because each way of getting it wrong is invisible
/// in a passing run and costs a hung suite when it is not: a child that never exits, a child that fills
/// one pipe while the runner reads the other, and a child that exits leaving its streams held open by
/// something else.
/// </remarks>
internal static class ChildProcess
{
    /// <summary>How long the runner waits for a process it has killed to be reaped.</summary>
    private static readonly TimeSpan _reapDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The least time the streams are given to finish after the process itself has exited.
    /// </summary>
    /// <remarks>
    /// What is left of the deadline is usually the allowance, but a process that exits at the very
    /// edge of it would otherwise be given nothing at all and lose its last line to a race.
    /// </remarks>
    private static readonly TimeSpan _minimumDrain = TimeSpan.FromSeconds(5);

    internal sealed record Result(int ExitCode, string Output);

    /// <summary>Starts the process, drains both its streams, and waits for it under the deadline.</summary>
    /// <param name="start">The process to run, with both streams redirected.</param>
    /// <param name="deadline">How long the process may run before it is killed and the call fails.</param>
    /// <param name="what">How to name the process in the failure message.</param>
    internal static Result Run(ProcessStartInfo start, TimeSpan deadline, string what)
    {
        using Process process =
            Process.Start(start) ?? throw new AssertionException($"{what} did not start.");

        // Both streams are drained as the child writes them, rather than one to its end and then the
        // other. A child that fills one pipe while the runner waits on the other blocks on its write,
        // never closes the stream being waited on, and both sides wait forever; a deadline cannot help,
        // because a blocking read only reaches one after it has returned. Draining concurrently is what
        // makes the deadlines below the things that actually bound this.
        Lock outputLock = new();
        StringBuilder output = new();
        TaskCompletionSource standardOutputClosed = new();
        TaskCompletionSource standardErrorClosed = new();

        DataReceivedEventHandler Capture(TaskCompletionSource closed) =>
            (_, received) =>
            {
                // A null line is how the reader says the stream reached its end.
                if (received.Data is not { } line)
                {
                    closed.TrySetResult();
                    return;
                }

                lock (outputLock)
                {
                    output.AppendLine(line);
                }
            };

        string CapturedOutput()
        {
            lock (outputLock)
            {
                return output.ToString();
            }
        }

        process.OutputDataReceived += Capture(standardOutputClosed);
        process.ErrorDataReceived += Capture(standardErrorClosed);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Stopwatch elapsed = Stopwatch.StartNew();

        if (!process.WaitForExit(deadline))
        {
            KillQuietly(process);
            process.WaitForExit(_reapDeadline);

            throw new AssertionException(
                $"{what} did not finish within its deadline. Output so far: {CapturedOutput()}"
            );
        }

        // The overload above returns the moment the process exits and does not wait for the readers to
        // deliver what it wrote last. The parameterless overload does wait for them, but waits without
        // a bound: a grandchild that inherited the pipe holds the streams open after the child itself
        // is gone, and the wait then never returns. So the wait is on the readers, for what is left of
        // the deadline, and what was captured is reported either way.
        TimeSpan drainAllowance = deadline - elapsed.Elapsed;

        if (drainAllowance < _minimumDrain)
        {
            drainAllowance = _minimumDrain;
        }

        if (!Task.WhenAll(standardOutputClosed.Task, standardErrorClosed.Task).Wait(drainAllowance))
        {
            KillQuietly(process);

            throw new AssertionException(
                $"{what} exited, but something held its streams open past the deadline. "
                    + $"Output so far: {CapturedOutput()}"
            );
        }

        return new Result(process.ExitCode, CapturedOutput());
    }

    /// <summary>
    /// Ends the process and anything it started, without turning the reason for killing it into a
    /// different failure.
    /// </summary>
    private static void KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // The process, or a descendant of it, is already gone. The caller is about to report the
            // real problem, and it is not this.
        }
    }
}
