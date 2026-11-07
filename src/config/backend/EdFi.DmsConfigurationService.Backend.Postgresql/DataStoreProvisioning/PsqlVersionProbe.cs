// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>Why the version probe produced no version.</summary>
internal enum PsqlProbeFailure
{
    /// <summary>The executable could not be started; the type chain names the runtime's exception.</summary>
    StartFailed,

    /// <summary>The executable did not exit within the probe's bound and was killed.</summary>
    TimedOut,

    /// <summary>The executable exited with a non-zero code.</summary>
    ExitedWithError,

    /// <summary>The executable, or something it started, could not be confirmed gone within the termination bound.</summary>
    TerminationUnconfirmed,
}

/// <summary>The result of <see cref="PsqlVersionProbe.ProbeAsync"/>; the floor comparison (D-9) belongs to readiness.</summary>
internal abstract record PsqlVersionProbeResult
{
    public sealed record Parsed(int Major, int Minor) : PsqlVersionProbeResult;

    /// <summary>The executable ran and exited cleanly but did not announce itself as <c>psql (PostgreSQL) &lt;version&gt;</c>.</summary>
    public sealed record Unparsable() : PsqlVersionProbeResult;

    public sealed record Failed(PsqlProbeFailure Reason, string ExceptionTypeChain) : PsqlVersionProbeResult;
}

/// <summary>
/// The startup probe of the configured executable (7.3 item 5): runs <c>psql --version</c> under the
/// environment allowlist with a short bound and parses the announced version, so readiness can prove that
/// <c>PsqlPath</c> is a <c>psql</c> whose major version meets the floor before any operation depends on it.
/// </summary>
internal static partial class PsqlVersionProbe
{
    /// <summary>The <c>Diagnostic.Operation</c> of a probe that failed.</summary>
    public const string ProbeStep = "ProbePsqlVersion";

    /// <summary>The bound of 7.3: a version print that takes longer than this is not a usable executable.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private const string VersionArgument = "--version";
    private const int OutputLimit = 4096;

    /// <summary>
    /// Runs the probe. A cancelled caller token propagates; the probe's own bound is reported as
    /// <see cref="PsqlProbeFailure.TimedOut"/>. Standard error is consumed and discarded.
    /// </summary>
    public static Task<PsqlVersionProbeResult> ProbeAsync(
        string psqlPath,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    ) =>
        ProbeAsync(
            psqlPath,
            timeout,
            PsqlProcess.DefaultTerminationBound,
            timeProvider,
            kill: null,
            cancellationToken
        );

    /// <summary>The probe with the termination bound and the kill exposed, so a test can prove the unconfirmed paths quickly.</summary>
    internal static async Task<PsqlVersionProbeResult> ProbeAsync(
        string psqlPath,
        TimeSpan timeout,
        TimeSpan terminationBound,
        TimeProvider timeProvider,
        PsqlProcess.KillProcess? kill,
        CancellationToken cancellationToken
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);

        ProcessStartInfo startInfo = PsqlProcess.CreateStartInfo(psqlPath, Path.GetTempPath());
        startInfo.ArgumentList.Add(VersionArgument);
        LibpqEnvironment.Apply(startInfo, connection: null, passwordFilePath: null, applicationName: null);

        StringBuilder output = new();
        using CancellationTokenSource deadline = new(timeout, timeProvider);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token
        );

        PsqlProcess.Completion completion;
        try
        {
            completion = await PsqlProcess.RunAsync(
                startInfo,
                text => AppendBounded(output, text),
                consumeStandardError: null,
                terminationBound,
                timeProvider,
                kill,
                stop.Token
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new PsqlVersionProbeResult.Failed(
                PsqlProbeFailure.StartFailed,
                DmsProvisioningDiagnostic.TypeChain(exception)
            );
        }

        if (completion.Termination != PsqlProcess.Termination.Exited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new PsqlVersionProbeResult.Failed(
                completion.Termination == PsqlProcess.Termination.Killed
                    ? PsqlProbeFailure.TimedOut
                    : PsqlProbeFailure.TerminationUnconfirmed,
                string.Empty
            );
        }

        if (completion.ExitCode != 0)
        {
            return new PsqlVersionProbeResult.Failed(PsqlProbeFailure.ExitedWithError, string.Empty);
        }

        return TryParse(output.ToString(), out int major, out int minor)
            ? new PsqlVersionProbeResult.Parsed(major, minor)
            : new PsqlVersionProbeResult.Unparsable();
    }

    /// <summary>
    /// Parses the first line of a <c>--version</c> print: <c>psql (PostgreSQL) 18.1</c> gives 18 and 1, a
    /// distribution suffix after the version is ignored, a prerelease tag (<c>18beta1</c>) gives minor 0,
    /// and anything not announcing <c>psql (PostgreSQL)</c> is rejected.
    /// </summary>
    public static bool TryParse(string? output, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (output is null)
        {
            return false;
        }

        ReadOnlySpan<char> firstLine = output.AsSpan();
        int lineEnd = firstLine.IndexOfAny('\r', '\n');
        if (lineEnd >= 0)
        {
            firstLine = firstLine[..lineEnd];
        }

        Match match = VersionLine().Match(firstLine.Trim().ToString());
        if (
            !match.Success
            || !int.TryParse(
                match.Groups[1].ValueSpan,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out major
            )
        )
        {
            return false;
        }

        if (
            match.Groups[2].Success
            && !int.TryParse(
                match.Groups[2].ValueSpan,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out minor
            )
        )
        {
            major = 0;
            return false;
        }

        return true;
    }

    private static void AppendBounded(StringBuilder output, ReadOnlySpan<char> text)
    {
        int room = OutputLimit - output.Length;
        if (room > 0)
        {
            output.Append(text[..Math.Min(room, text.Length)]);
        }
    }

    // The digit runs are unbounded on purpose: a run too long for an int fails the overflow-checked parse
    // instead of being silently truncated by a bounded quantifier.
    [GeneratedRegex(@"^psql \(PostgreSQL\) (\d+)(?:\.(\d+))?")]
    private static partial Regex VersionLine();
}
