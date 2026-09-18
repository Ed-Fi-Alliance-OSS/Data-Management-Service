// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    public bool InjectedFailure { get; init; }
    public IReadOnlyList<string> StatusCodes { get; init; } = [];
    public IReadOnlyList<string> Parameters { get; init; } = [];
    public IReadOnlyList<string> StackFrames { get; init; } = [];
    public string MessageKind { get; init; } = "NotObserved";
    public string MessageSha256 { get; init; } = string.Empty;
    public string BuildStamp { get; init; } = string.Empty;

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
            InjectedFailure = text.Split('\n', StringSplitOptions.TrimEntries)
                .Contains("CDC_SQL_STARTUP_INJECTED_FAILURE", StringComparer.Ordinal),
            StatusCodes = Statuses()
                .Matches(text)
                .Select(m => m.Groups[1].Value.ToUpperInvariant())
                .Distinct()
                .Take(8)
                .ToArray(),
            Parameters = ParameterValues()
                .Matches(text)
                .Select(m => m.Groups[1].Value.ToUpperInvariant())
                .Take(8)
                .ToArray(),
            StackFrames = Frames()
                .Matches(text)
                .Select(m =>
                    m.Groups[1].Value.ToLowerInvariant() + "+" + m.Groups[2].Value.ToUpperInvariant()
                )
                .Take(16)
                .ToArray(),
            MessageKind = ClassifyMessage(text),
            MessageSha256 = HashMessage(text),
            BuildStamp = BuildStamps().Match(text).Groups[1].Value.ToLowerInvariant(),
        };
    }

    private static string ClassifyMessage(string text)
    {
        string message = Messages().Match(text).Groups[1].Value;
        if (message.Length == 0)
        {
            return "NotObserved";
        }
        if (message.Contains("ASSERT:", StringComparison.OrdinalIgnoreCase))
        {
            return "Assertion";
        }
        if (message.StartsWith("Termination of ", StringComparison.OrdinalIgnoreCase))
        {
            return "Termination";
        }
        if (message.Contains("Kernel bug check", StringComparison.OrdinalIgnoreCase))
        {
            return "KernelBugCheck";
        }
        if (message.Contains("Resource temporarily unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return "ResourceTemporarilyUnavailable";
        }
        return "Other";
    }

    // The digest correlates unknown messages without publishing arbitrary log prose.
    private static string HashMessage(string text)
    {
        string message = Messages().Match(text).Groups[1].Value;
        return message.Length == 0
            ? string.Empty
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(message)));
    }

    [GeneratedRegex(
        @"^\s*Message:[ \t]*([^\r\n]+)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100
    )]
    private static partial Regex Messages();

    [GeneratedRegex(
        @"^\s*Status:[ \t]*(0x[0-9a-f]{8})\b",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100
    )]
    private static partial Regex Statuses();

    [GeneratedRegex(
        @"^\s*\[[0-7]\][ \t]+(0x[0-9a-f]{1,16})\b",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100
    )]
    private static partial Regex ParameterValues();

    [GeneratedRegex(
        @"^\s*file:///?(?:package[0-9]+/)?(?:windows/)?(?:system32/)?(?:binn/)?(sqlpal\.dll|sqlservr|sqllang\.dll|sqldk\.dll|ntdll\.dll|kernel32\.dll|lsasrv\.dll|samsrv\.dll|lsass\.exe|apploader\.exe)\+(0x[0-9a-f]{1,16})\b",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100
    )]
    private static partial Regex Frames();

    [GeneratedRegex(
        @"^\s*Build stamp:[ \t]*([0-9a-f]{64})\b",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100
    )]
    private static partial Regex BuildStamps();

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
