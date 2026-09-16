// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.RegularExpressions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

// These fields describe markers in a bounded log sample, not an inferred cause of the exit.
internal sealed record CdcSqlServerStartupLogEvidence(
    string State,
    bool Truncated,
    bool FatalMessage,
    bool MemoryMessage,
    bool MappingMessage,
    IReadOnlyList<int> SqlErrorNumbers,
    IReadOnlyList<uint> FatalReasonCodes,
    IReadOnlyList<int> LastErrnos,
    IReadOnlyList<string> Signals
)
{
    public bool LsaInitializationTimeout { get; init; }

    internal static CdcSqlServerStartupLogEvidence Empty(string state) =>
        new(state, false, false, false, false, [], [], [], []);
}

internal static partial class CdcSqlServerStartupLogClassifier
{
    private const int MaximumStreamCharacters = 32767;

    internal static CdcSqlServerStartupLogEvidence Parse(DockerCommandResult result)
    {
        if (result.ExitCode != 0)
        {
            return CdcSqlServerStartupLogEvidence.Empty("Unavailable");
        }
        // Docker bounds the requested tail. Independently bound parsing of each stream, including
        // a single unusually long line. No raw text or unknown marker is retained in the result.
        string text = string.Concat(
            result.StandardOutput.AsSpan(0, Math.Min(result.StandardOutput.Length, MaximumStreamCharacters)),
            "\n",
            result.StandardError.AsSpan(0, Math.Min(result.StandardError.Length, MaximumStreamCharacters))
        );
        return new CdcSqlServerStartupLogEvidence(
            "Observed",
            result.StandardOutput.Length > MaximumStreamCharacters
                || result.StandardError.Length > MaximumStreamCharacters
                // A saturated Docker tail may have omitted earlier contradictory evidence.
                || text.Count(character => character == '\n') >= 400,
            text.Contains("This program has encountered a fatal error", StringComparison.OrdinalIgnoreCase),
            text.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
                || text.Contains("unable to allocate", StringComparison.OrdinalIgnoreCase)
                || text.Contains("insufficient memory", StringComparison.OrdinalIgnoreCase)
                || text.Contains("requires a minimum of 2000 megabytes", StringComparison.OrdinalIgnoreCase),
            text.Contains("invalid mapping of address", StringComparison.OrdinalIgnoreCase)
                || text.Contains("failed to reserve", StringComparison.OrdinalIgnoreCase)
                || text.Contains("mmap_rnd_bits", StringComparison.OrdinalIgnoreCase)
                || text.Contains("legacy_va_layout", StringComparison.OrdinalIgnoreCase),
            SqlErrors()
                .Matches(text)
                .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .Distinct()
                .Take(8)
                .ToArray(),
            Reasons()
                .Matches(text)
                .Select(m =>
                    uint.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                )
                .Distinct()
                .Take(8)
                .ToArray(),
            Errnos()
                .Matches(text)
                .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .Distinct()
                .Take(8)
                .ToArray(),
            Signals()
                .Matches(text)
                .Select(m => m.Groups[1].Value.ToUpperInvariant())
                .Distinct()
                .Take(8)
                .ToArray()
        )
        {
            LsaInitializationTimeout = HasExactLsaInitializationTimeout(text),
        };
    }

    private static bool HasExactLsaInitializationTimeout(string text)
    {
        string[] lines = text.Split('\n', StringSplitOptions.TrimEntries);
        string[] expected =
        [
            "** ERROR: [AppLoader] Failed to load LSA: 0xc0070102",
            "AppLoader: Exiting with status=0xc0070102",
            @"Message: Termination of \SystemRoot\system32\AppLoader.exe was due to fatal error 0xC0000001",
        ];
        string[] prefixes =
        [
            "** ERROR: [AppLoader] Failed to load LSA:",
            "AppLoader: Exiting with status=",
            "Message: Termination of ",
        ];
        return Enumerable
            .Range(0, expected.Length)
            .All(index =>
                lines
                    .Where(line => line.StartsWith(prefixes[index], StringComparison.OrdinalIgnoreCase))
                    .SequenceEqual([expected[index]], StringComparer.OrdinalIgnoreCase)
            );
    }

    [GeneratedRegex(
        @"\bError:\s*([0-9]{1,6}),\s*Severity:\s*[0-9]{1,2}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100
    )]
    private static partial Regex SqlErrors();

    [GeneratedRegex(
        @"\bReason:\s*0x([0-9a-f]{8})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100
    )]
    private static partial Regex Reasons();

    [GeneratedRegex(
        @"\bLast errno:\s*([0-9]{1,4})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100
    )]
    private static partial Regex Errnos();

    [GeneratedRegex(
        @"\bSignal:\s*(SIGABRT|SIGSEGV|SIGBUS|SIGILL|SIGFPE|SIGKILL)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100
    )]
    private static partial Regex Signals();
}
