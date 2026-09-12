// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// A source-scanning guard over the two things nothing in the type system can prevent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a source scan.</b> AD-2 decided not to put normalization inside
/// <c>TraceId</c>: it lives in <c>Core.External</c>, a published contract assembly, and
/// normalization needs the <c>CorrelationIdMaxLength</c> value from frontend configuration that
/// assembly must not depend on. The stated consequence was that nothing structurally prevents a
/// future raw <c>new TraceId(...)</c>, to be mitigated by a guard test. This is that test.
/// </para>
/// <para>
/// <b>What it guards.</b> Two distinct regressions, neither of which any other test can catch
/// before it ships:
/// <list type="number">
/// <item>
/// A new endpoint constructing a <c>TraceId</c> straight from <c>context.TraceIdentifier</c> or a
/// request header, bypassing <c>AspNetCoreFrontend.ExtractTraceIdFrom</c> and so skipping
/// normalization entirely. The parity fixtures assert only about endpoints that already exist,
/// so a new one would add no failing test.
/// </item>
/// <item>
/// A correlation ID routed through the strict <c>Method</c>/<c>Path</c> sanitizer. That
/// allowlist strips punctuation an upstream identifier scheme legitimately uses, so the logged
/// value would silently stop matching the <c>correlationId</c> the client read for the same
/// request - the single guarantee FR-LOG-6 makes. Most of the sites that log a correlation ID
/// carry no assertion of their own, so this scan is what covers them.
/// </item>
/// </list>
/// </para>
/// <para>
/// <b>Precedent.</b> <c>CdcConnectorTemplateIntegrationBoundaryTests</c> does the same thing:
/// walk up to the solution file, load repository files, assert on their contents.
/// </para>
/// <para>
/// <b>Known blind spot.</b> The scan is textual. A <c>TraceId</c> produced by a target-typed
/// <c>new(...)</c> in an argument position - <c>Method(new(raw))</c>, where only the parameter
/// type names <c>TraceId</c> - is invisible to it, as is a value reaching a sanitizer through a
/// variable whose name says nothing about correlation, and as is a sanitizer call on a string
/// literal (literals are blanked along with comments, and a literal is not a correlation ID).
/// It catches the shapes a regression realistically takes, not every shape one could take.
/// </para>
/// </remarks>
[TestFixture]
[Parallelizable]
public class CorrelationIdConstructionSiteGuardTests
{
    /// <summary>
    /// The only two production files allowed to construct a <see cref="Core.External.Model.TraceId"/>,
    /// as repository-relative paths.
    /// </summary>
    /// <remarks>
    /// <c>AspNetCoreFrontend.cs</c> is the single ingestion point: it is the one place a
    /// correlation ID is normalized, and every other production path reaches a <c>TraceId</c>
    /// through it. <c>No.cs</c> is the null-object factory - <c>No.CreateFrontendRequest</c>,
    /// whose only caller is <c>No.RequestInfo</c>, which has no production callers at all, so it
    /// is unreachable from an HTTP request and cannot carry client input (declined finding D-3).
    ///
    /// Adding an entry here is a deliberate act: it asserts that the new site either normalizes
    /// its input or cannot receive client input.
    /// </remarks>
    private static readonly string[] PermittedTraceIdConstructionSites =
    [
        "src/dms/core/EdFi.DataManagementService.Core/Model/No.cs",
        "src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs",
    ];

    /// <summary>
    /// <c>new TraceId(...)</c>, optionally namespace-qualified.
    /// </summary>
    private static readonly Regex ExplicitTraceIdConstruction = new(
        @"\bnew\s+(?:[\w.]+\.)?TraceId\s*\(",
        RegexOptions.Compiled
    );

    /// <summary>
    /// <c>TraceId t = new(...)</c> - the target-typed spelling, which the explicit pattern above
    /// cannot see and which a reviewer reading a diff can easily miss. Covers locals, fields and
    /// properties, including nullable and namespace-qualified declarations.
    /// </summary>
    private static readonly Regex TargetTypedTraceIdConstruction = new(
        @"\b(?:[\w.]+\.)?TraceId\??\s+[\w@]+\s*(?:=>|=)\s*new\s*[(\{]",
        RegexOptions.Compiled
    );

    /// <summary>
    /// A call to the strict <c>Method</c>/<c>Path</c> sanitizer under either of its two names.
    /// The argument text is inspected separately, because a regex cannot reliably match a
    /// balanced argument list.
    /// </summary>
    private static readonly Regex StrictSanitizerCall = new(
        @"\bSanitizeFor(?:Log|Logging)\s*\(",
        RegexOptions.Compiled
    );

    [Test]
    public void It_permits_a_trace_id_to_be_constructed_only_where_it_is_normalized_or_inert()
    {
        List<SourceMatch> matches = [];

        foreach (SourceFile file in ProductionSourceFiles())
        {
            matches.AddRange(FindMatches(file, ExplicitTraceIdConstruction));
            matches.AddRange(FindMatches(file, TargetTypedTraceIdConstruction));
        }

        string[] offendingSites =
        [
            .. matches
                .Where(match => !PermittedTraceIdConstructionSites.Contains(match.File.RelativePath))
                .Select(match => match.Describe())
                .Order(StringComparer.Ordinal),
        ];

        offendingSites
            .Should()
            .BeEmpty(
                "a TraceId built outside AspNetCoreFrontend.ExtractTraceIdFrom skips correlation ID "
                    + "normalization, so the value in that request's error response body stops matching "
                    + "the value in its log events. Route the new site through ExtractTraceIdFrom, or - "
                    + "if it cannot receive client input - add it to {0}. Offending sites:{1}{2}",
                nameof(PermittedTraceIdConstructionSites),
                Environment.NewLine,
                string.Join(Environment.NewLine, offendingSites)
            );

        // A permitted-list entry that no longer matches anything is stale and would silently stop
        // guarding anything, so the list is held to being exactly the set of real sites.
        string[] matchedFiles =
        [
            .. matches.Select(match => match.File.RelativePath).Distinct().Order(StringComparer.Ordinal),
        ];

        matchedFiles
            .Should()
            .BeEquivalentTo(
                PermittedTraceIdConstructionSites,
                "every entry in {0} must still name a file that constructs a TraceId, or the entry "
                    + "is stale and is quietly permitting a file that no longer needs permission",
                nameof(PermittedTraceIdConstructionSites)
            );
    }

    [Test]
    public void It_never_routes_a_correlation_id_through_the_strict_method_and_path_sanitizer()
    {
        List<string> offendingSites = [];

        foreach (SourceFile file in ProductionSourceFiles())
        {
            foreach (SourceMatch match in FindMatches(file, StrictSanitizerCall))
            {
                string argument = ArgumentListAt(file.ScannableText, match.Index + match.Length - 1);

                if (LooksLikeACorrelationId(argument))
                {
                    offendingSites.Add($"{match.Describe()}: SanitizeFor...({argument.Trim()})");
                }
            }
        }

        offendingSites
            .Should()
            .BeEmpty(
                "SanitizeForLog/SanitizeForLogging apply the strict Method/Path allowlist, which "
                    + "strips punctuation an upstream correlation ID legitimately uses. The logged "
                    + "value would then differ from the correlationId in the response body for the "
                    + "same request. Use LoggingSanitizer.SanitizeCorrelationId, or pass the already-"
                    + "normalized value raw. Offending sites:{0}{1}",
                Environment.NewLine,
                string.Join(Environment.NewLine, offendingSites.Order(StringComparer.Ordinal))
            );
    }

    private static bool LooksLikeACorrelationId(string argument) =>
        argument.Contains("trace", StringComparison.OrdinalIgnoreCase)
        || argument.Contains("correlation", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The text of a balanced argument list whose opening parenthesis is at
    /// <paramref name="openParenIndex"/>. Returns the empty string if the source is unbalanced,
    /// which cannot happen in code that compiles.
    /// </summary>
    private static string ArgumentListAt(string text, int openParenIndex)
    {
        int depth = 0;

        for (int index = openParenIndex; index < text.Length; index++)
        {
            if (text[index] == '(')
            {
                depth++;
            }
            else if (text[index] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return text[(openParenIndex + 1)..index];
                }
            }
        }

        return string.Empty;
    }

    private static IEnumerable<SourceMatch> FindMatches(SourceFile file, Regex pattern) =>
        pattern.Matches(file.ScannableText).Select(match => new SourceMatch(file, match.Index, match.Length));

    private static IEnumerable<SourceFile> ProductionSourceFiles()
    {
        string repositoryRoot = FindRepositoryRoot();
        string dmsRoot = Path.Combine(repositoryRoot, "src", "dms");

        foreach (
            string path in Directory
                .EnumerateFiles(dmsRoot, "*.cs", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
        )
        {
            string relativePath = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');

            if (IsExcluded(relativePath))
            {
                continue;
            }

            yield return new SourceFile(relativePath, BlankCommentsAndLiterals(File.ReadAllText(path)));
        }
    }

    /// <summary>
    /// Build output and every test project. A test is allowed to construct a raw
    /// <c>TraceId</c> - most of them must, to exercise the code under test - and is allowed to
    /// pass a trace-shaped value to the strict sanitizer to prove what that sanitizer does to it.
    /// </summary>
    private static bool IsExcluded(string relativePath) =>
        Array.Exists(
            relativePath.Split('/'),
            segment =>
                segment is "bin" or "obj" || segment.Contains("test", StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>
    /// Replaces every comment and literal with spaces, leaving all other characters and every
    /// newline exactly where they were so indexes and line numbers still refer to the real file.
    /// Without this a sentence in an XML doc comment, or a message string, would be read as code.
    /// </summary>
    private static string BlankCommentsAndLiterals(string source)
    {
        StringBuilder result = new(source);
        int index = 0;

        while (index < source.Length)
        {
            char current = source[index];

            if (current == '/' && Peek(source, index + 1) == '/')
            {
                index = BlankUntil(result, source, index, position => source[position] == '\n');
                continue;
            }

            if (current == '/' && Peek(source, index + 1) == '*')
            {
                index = BlankUntil(
                    result,
                    source,
                    index + 2,
                    position => source[position] == '*' && Peek(source, position + 1) == '/'
                );
                index = BlankCount(result, source, index, 2);
                continue;
            }

            if (current == '\'')
            {
                index = BlankQuoted(result, source, index, '\'');
                continue;
            }

            int quoteStart = index;
            bool verbatim = false;
            while (quoteStart < source.Length && source[quoteStart] is '@' or '$')
            {
                verbatim |= source[quoteStart] == '@';
                quoteStart++;
            }

            if (quoteStart < source.Length && source[quoteStart] == '"')
            {
                int quoteRun = 0;
                while (Peek(source, quoteStart + quoteRun) == '"')
                {
                    quoteRun++;
                }

                if (quoteRun >= 3)
                {
                    index = BlankRawString(result, source, index, quoteStart, quoteRun);
                }
                else if (verbatim)
                {
                    index = BlankVerbatimString(result, source, index, quoteStart);
                }
                else
                {
                    index = BlankQuoted(result, source, quoteStart, '"');
                }

                continue;
            }

            index++;
        }

        return result.ToString();
    }

    private static char Peek(string source, int index) => index < source.Length ? source[index] : '\0';

    private static int BlankCount(StringBuilder result, string source, int start, int count)
    {
        int index = start;
        while (index < source.Length && index < start + count)
        {
            Blank(result, source, index);
            index++;
        }

        return index;
    }

    private static int BlankUntil(
        StringBuilder result,
        string source,
        int start,
        Func<int, bool> isTerminator
    )
    {
        int index = start;
        while (index < source.Length && !isTerminator(index))
        {
            Blank(result, source, index);
            index++;
        }

        return index;
    }

    /// <summary>A regular string or char literal, honoring backslash escapes.</summary>
    private static int BlankQuoted(StringBuilder result, string source, int start, char quote)
    {
        int index = start + 1;
        while (index < source.Length && source[index] != quote)
        {
            index =
                source[index] == '\\'
                    ? BlankCount(result, source, index, 2)
                    : BlankCount(result, source, index, 1);
        }

        return index < source.Length ? index + 1 : index;
    }

    /// <summary>A verbatim (<c>@"..."</c>) string, where the only escape is a doubled quote.</summary>
    private static int BlankVerbatimString(StringBuilder result, string source, int start, int quoteStart)
    {
        BlankCount(result, source, start, quoteStart - start);
        int index = quoteStart + 1;

        while (index < source.Length)
        {
            if (source[index] == '"')
            {
                if (Peek(source, index + 1) != '"')
                {
                    return index + 1;
                }

                index = BlankCount(result, source, index, 2);
                continue;
            }

            index = BlankCount(result, source, index, 1);
        }

        return index;
    }

    /// <summary>A raw string literal, terminated by a quote run at least as long as its opener.</summary>
    private static int BlankRawString(
        StringBuilder result,
        string source,
        int start,
        int quoteStart,
        int quoteRun
    )
    {
        BlankCount(result, source, start, quoteStart - start);
        int index = quoteStart + quoteRun;

        while (index < source.Length)
        {
            if (source[index] != '"')
            {
                index = BlankCount(result, source, index, 1);
                continue;
            }

            int closingRun = 0;
            while (Peek(source, index + closingRun) == '"')
            {
                closingRun++;
            }

            if (closingRun >= quoteRun)
            {
                return index + closingRun;
            }

            index = BlankCount(result, source, index, closingRun);
        }

        return index;
    }

    /// <summary>
    /// Blanks one character, preserving newlines so reported line numbers stay truthful.
    /// </summary>
    private static void Blank(StringBuilder result, string source, int index)
    {
        if (source[index] is not '\n' and not '\r')
        {
            result[index] = ' ';
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "dms", "EdFi.DataManagementService.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from the test directory.");
    }

    private sealed record SourceFile(string RelativePath, string ScannableText);

    private sealed record SourceMatch(SourceFile File, int Index, int Length)
    {
        /// <summary>
        /// <c>path:line</c> plus the offending line's text, so a failure names the file and line
        /// and needs no further investigation to act on.
        /// </summary>
        public string Describe()
        {
            int line = 1;
            for (int position = 0; position < Index; position++)
            {
                if (File.ScannableText[position] == '\n')
                {
                    line++;
                }
            }

            int lineStart = File.ScannableText.LastIndexOf('\n', Math.Max(Index - 1, 0)) + 1;
            int lineEnd = File.ScannableText.IndexOf('\n', Index);
            string text = File.ScannableText[lineStart..(lineEnd < 0 ? File.ScannableText.Length : lineEnd)];

            return $"{File.RelativePath}:{line}: {text.Trim()}";
        }
    }
}
