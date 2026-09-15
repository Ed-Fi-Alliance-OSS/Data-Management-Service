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
/// A correlation ID routed through <c>SanitizeInternalValueForLog</c> - the strict
/// <c>Method</c>/<c>Path</c> sanitizer for internally-controlled values. That
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
/// <b>Known blind spot.</b> The scan is textual, and the construction it misses most easily is
/// the one this repository writes most often: <c>return new(...);</c> inside a member whose
/// declared return type is <c>TraceId</c>. Neither pattern mentions <c>return</c>, so neither
/// sees it. <c>TraceId Foo() =&gt; new(...)</c> is missed for a closely related reason -
/// <see cref="TargetTypedTraceIdConstruction"/> runs from the type name straight to <c>=</c> or
/// <c>=&gt;</c>, so a parameter list breaks the match and only parameterless members such as
/// <c>TraceId Foo =&gt; new(</c> are caught. That is not a hypothetical style: <c>return new(</c>
/// appears 127 times in the production source under <c>src/dms/core</c> alone.
/// </para>
/// <para>
/// Nothing is escaping the guard through it today. At the time of writing the only production
/// member in the repository whose return type is <c>TraceId</c> is
/// <c>AspNetCoreFrontend.ExtractTraceIdFrom</c>, which delegates to the ingestion point rather
/// than constructing anything; every other <c>TraceId</c>-typed production member is a
/// <c>{ get; init; }</c> property on a backend contract record, which constructs nothing. The
/// exposure is that the first factory to return a freshly built <c>TraceId</c> would be the first
/// one, and would add no failing test - which is precisely the regression the permitted-site
/// counts exist to make visible. Closing it means matching on the declared return type rather
/// than on a declarator, which in practice means a Roslyn walk over
/// <c>ObjectCreationExpressionSyntax</c> and <c>ImplicitObjectCreationExpressionSyntax</c> with
/// the semantic model to hand. This paragraph is the disclosure, not the fix.
/// </para>
/// <para>
/// The remaining blind spots are narrower. A <c>TraceId</c> produced by a target-typed
/// <c>new(...)</c> in an argument position - <c>Method(new(raw))</c>, where only the parameter
/// type names <c>TraceId</c> - is invisible to the scan, as is a value reaching a sanitizer
/// through a variable whose name says nothing about correlation, and as is a sanitizer call on a
/// string literal (literals are blanked along with comments, and a literal is not a correlation
/// ID). It catches the shapes a regression realistically takes, not every shape one could take.
/// </para>
/// </remarks>
[TestFixture]
[Parallelizable]
public class CorrelationIdConstructionSiteGuardTests
{
    /// <summary>
    /// The only two production files allowed to construct a <see cref="Core.External.Model.TraceId"/>,
    /// as repository-relative paths, each mapped to the exact number of constructions it is
    /// permitted to contain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AspNetCoreFrontend.cs</c> is the single ingestion point: it is the one place a
    /// correlation ID is normalized, and every other production path reaches a <c>TraceId</c>
    /// through it. <c>No.cs</c> is the null-object factory - <c>No.CreateFrontendRequest</c>,
    /// whose only caller is <c>No.RequestInfo</c>, which has no production callers at all, so it
    /// is unreachable from an HTTP request and cannot carry client input (declined finding D-3).
    /// </para>
    /// <para>
    /// <b>Why a count and not just a file.</b> A permission recorded as a bare path is a permission
    /// for the whole file, so a <i>second</i> raw <c>new TraceId(...)</c> added next to the first -
    /// in <c>AspNetCoreFrontend.cs</c> above all, the file most likely to grow one - would change
    /// nothing the guard could see, and would ship without a failing test. The count is what makes
    /// the second one visible. It is not a stylistic cap: each construction below was read and
    /// found to normalize its input, and a new one is not covered by that reading.
    /// </para>
    /// <para>
    /// <b>Why AspNetCoreFrontend.cs is 2.</b> Measured, not assumed. One is the ingestion path in
    /// <c>ExtractTraceIdFrom</c>, which normalizes the header or falls back to the server-generated
    /// identifier; the other is <c>CorrelationIdIngestion.ForServerGeneratedIdentifier</c>, which
    /// exists in this file precisely so that no other file has to construct a <c>TraceId</c> and
    /// whose parameter is already normalized by its caller.
    /// </para>
    /// <para>
    /// Adding or raising an entry here is a deliberate act: it asserts that the new site either
    /// normalizes its input or cannot receive client input.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, int> PermittedTraceIdConstructionSites = new(
        StringComparer.Ordinal
    )
    {
        ["src/dms/core/EdFi.DataManagementService.Core/Model/No.cs"] = 1,
        ["src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs"] = 2,
    };

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
    /// A call to the internally-controlled-value sanitizer under either of its two names -
    /// <c>LogSanitizer.SanitizeInternalValueForLog</c> and the Core facade
    /// <c>LoggingSanitizer.SanitizeInternalValueForLogging</c>. The argument text is inspected
    /// separately, because a regex cannot reliably match a balanced argument list.
    /// </summary>
    /// <remarks>
    /// Renaming either method without updating this pattern would leave the scan matching nothing,
    /// and a scan that matches nothing still reports success.
    /// <see cref="It_never_routes_a_correlation_id_through_the_strict_method_and_path_sanitizer"/>
    /// therefore asserts that the pattern still finds calls in production source, so the guard
    /// fails closed rather than silently lapsing.
    /// </remarks>
    private static readonly Regex InternalValueSanitizerCall = new(
        @"\bSanitizeInternalValueForLog(?:ging)?\s*\(",
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
                .Where(match => !PermittedTraceIdConstructionSites.ContainsKey(match.File.RelativePath))
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

        // An entry here permits an exact number of constructions in that file, so the check is on
        // the count and not merely on the set of files. Distincting on the path - the earlier
        // spelling - made a second construction added beside an already-permitted one invisible:
        // the set of files was unchanged, so the guard passed while a site that may normalize
        // nothing shipped. Counting closes that, and still fails on the opposite staleness, an
        // entry that matches nothing at all.
        Dictionary<string, SourceMatch[]> constructionsByFile = matches
            .GroupBy(match => match.File.RelativePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        string[] countDiscrepancies =
        [
            .. PermittedTraceIdConstructionSites
                .Select(site =>
                    (
                        Path: site.Key,
                        Expected: site.Value,
                        Found: constructionsByFile.TryGetValue(site.Key, out SourceMatch[]? found)
                            ? found
                            : []
                    )
                )
                .Where(site => site.Found.Length != site.Expected)
                .Select(site => DescribeCountDiscrepancy(site.Path, site.Expected, site.Found))
                .Order(StringComparer.Ordinal),
        ];

        countDiscrepancies
            .Should()
            .BeEmpty(
                "each entry in {0} permits an exact number of TraceId constructions in the file it "
                    + "names, not the file as a whole. Read the site before changing the count: the "
                    + "count is the record that someone confirmed every construction in that file "
                    + "either normalizes its input or cannot receive client input, and editing it to "
                    + "match reality is how that record is lost. Discrepancies:{1}{2}",
                nameof(PermittedTraceIdConstructionSites),
                Environment.NewLine,
                string.Join(Environment.NewLine, countDiscrepancies)
            );
    }

    /// <summary>
    /// One permitted file whose construction count no longer matches, named together with the
    /// direction it moved in and every construction actually found, so the failure can be acted on
    /// without re-running the scan by hand.
    /// </summary>
    private static string DescribeCountDiscrepancy(string relativePath, int expected, SourceMatch[] found)
    {
        string verdict = found.Length switch
        {
            _ when found.Length > expected => "a NEW TraceId construction has appeared inside an "
                + "ALREADY-PERMITTED file. The file's entry did not have to change for this to "
                + "land, which is why nothing else caught it. Confirm the new site normalizes its "
                + "input through AspNetCoreFrontend.ExtractTraceIdFrom - or cannot receive client "
                + "input at all - and only then raise the count",
            0 => "this file no longer constructs a TraceId at all, so the entry is stale and is "
                + "quietly permitting a file that no longer needs permission. Remove it",
            _ => "a permitted TraceId construction has been removed. Lower the count so the entry "
                + "keeps naming reality rather than covering a site that is gone",
        };

        return $"{relativePath}: expected {expected}, found {found.Length} - {verdict}."
            + string.Concat(found.Select(match => $"{Environment.NewLine}    {match.Describe()}"));
    }

    [Test]
    public void It_never_routes_a_correlation_id_through_the_strict_method_and_path_sanitizer()
    {
        List<string> offendingSites = [];
        int scannedCallSites = 0;

        foreach (SourceFile file in ProductionSourceFiles())
        {
            foreach (SourceMatch match in FindMatches(file, InternalValueSanitizerCall))
            {
                scannedCallSites++;

                string argument = ArgumentListAt(file.ScannableText, match.Index + match.Length - 1);

                if (LooksLikeACorrelationId(argument))
                {
                    offendingSites.Add(
                        $"{match.Describe()}: SanitizeInternalValueForLog...({argument.Trim()})"
                    );
                }
            }
        }

        offendingSites
            .Should()
            .BeEmpty(
                "SanitizeInternalValueForLog/SanitizeInternalValueForLogging apply the strict "
                    + "Method/Path allowlist, which strips punctuation an upstream correlation ID "
                    + "legitimately uses. The logged value would then differ from the correlationId "
                    + "in the response body for the same request. Use "
                    + "LoggingSanitizer.SanitizeCorrelationId, or pass the already-normalized value "
                    + "raw. Offending sites:{0}{1}",
                Environment.NewLine,
                string.Join(Environment.NewLine, offendingSites.Order(StringComparer.Ordinal))
            );

        // The assertion above is vacuous if the pattern matches nothing, which is exactly what a
        // rename of either sanitizer would cause. Production source calls the pair well over a
        // hundred times, so a floor of fifty fails loudly on a stale pattern while leaving room
        // for ordinary call sites to come and go.
        scannedCallSites
            .Should()
            .BeGreaterThan(
                50,
                "{0} must still match the sanitizer's real spelling. Finding almost no call sites "
                    + "means the method was renamed and this guard has silently stopped guarding, "
                    + "not that the codebase stopped calling it",
                nameof(InternalValueSanitizerCall)
            );
    }

    /// <summary>
    /// The guard above is only worth what its file list is worth. A scan that quietly stops
    /// reaching production code still passes, so the coverage of the scan is itself asserted.
    /// </summary>
    [Test]
    public void It_scans_production_source_and_no_test_project_source()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] scanned = [.. ProductionSourceFiles().Select(file => file.RelativePath)];

        scanned
            .Should()
            .Contain(
                "src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs",
                "the scan is meaningless if it does not reach the single ingestion point the whole "
                    + "normalization contract is built around"
            );

        scanned
            .Should()
            .NotContain(
                "src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/"
                    + "CorrelationIdConstructionSiteGuardTests.cs",
                "a test file is allowed to name the very patterns this fixture forbids, this one "
                    + "most of all"
            );

        // The six production files a substring match on "test" - in the file name - used to drop.
        // Named individually rather than derived, so that deleting or renaming one is a decision
        // someone makes here rather than a silent narrowing of the scan.
        string[] namesThatSpellTestAcrossAWordBoundary =
        [
            "src/dms/backend/EdFi.DataManagementService.Backend/Composite/RelationalCompositeStatementRewriter.cs",
            "src/dms/backend/EdFi.DataManagementService.Backend/RelationalCompositeStoredAuthorization.cs",
            "src/dms/core/EdFi.DataManagementService.Core/DocumentCache/Cdc/CdcAggregateStatusEvaluator.cs",
            "src/dms/core/EdFi.DataManagementService.Core/DocumentCache/Cdc/CdcBindingStateStore.cs",
            "src/dms/core/EdFi.DataManagementService.Core/DocumentCache/Cdc/LocalCdcBindingStateStore.cs",
            "src/dms/core/EdFi.DataManagementService.Core/Startup/ValidateStartupInstancesTask.cs",
        ];

        string[] missing =
        [
            .. namesThatSpellTestAcrossAWordBoundary.Except(scanned).Order(StringComparer.Ordinal),
        ];

        missing
            .Should()
            .BeEmpty(
                "a production file must be scanned whatever its name spells. If one of these was "
                    + "renamed or removed, update the list; if the filter dropped it, the filename "
                    + "hole has reopened. Missing:{0}{1}",
                Environment.NewLine,
                string.Join(Environment.NewLine, missing)
            );

        // Cross-checked against <IsTestProject>, deliberately a different mechanism from the
        // directory-name match the filter uses, so a test project named outside the convention
        // fails here loudly instead of quietly joining the scanned set.
        string[] testProjectDirectories = TestProjectDirectories(repositoryRoot);

        string[] scannedTestProjectFiles =
        [
            .. scanned
                .Where(path =>
                    Array.Exists(
                        testProjectDirectories,
                        directory => path.StartsWith(directory, StringComparison.Ordinal)
                    )
                )
                .Order(StringComparer.Ordinal),
        ];

        scannedTestProjectFiles
            .Should()
            .BeEmpty(
                "a test project is allowed to construct a raw TraceId and to hand a trace-shaped "
                    + "value to the strict sanitizer, so scanning one produces noise, not findings. "
                    + "Teach IsExcludedDirectory about this project's naming. Scanned:{0}{1}",
                Environment.NewLine,
                string.Join(Environment.NewLine, scannedTestProjectFiles)
            );

        testProjectDirectories
            .Should()
            .NotBeEmpty("if no test project were found the check above would be vacuously true");
    }

    /// <summary>
    /// Repository-relative directories, each with a trailing slash, of every project under
    /// <c>src/dms</c> declaring <c>&lt;IsTestProject&gt;true&lt;/IsTestProject&gt;</c>.
    /// </summary>
    private static string[] TestProjectDirectories(string repositoryRoot) =>
        [
            .. Directory
                .EnumerateFiles(
                    Path.Combine(repositoryRoot, "src", "dms"),
                    "*.csproj",
                    SearchOption.AllDirectories
                )
                .Where(project =>
                    File.ReadAllText(project)
                        .Replace(" ", string.Empty)
                        .Replace("\t", string.Empty)
                        .Contains("<IsTestProject>true</IsTestProject>", StringComparison.OrdinalIgnoreCase)
                )
                .Select(project =>
                    Path.GetRelativePath(repositoryRoot, Path.GetDirectoryName(project)!).Replace('\\', '/')
                    + "/"
                ),
        ];

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
    /// <remarks>
    /// The decision is made on the <b>directories</b> a file sits in, never on the file's own
    /// name. An earlier spelling asked whether any segment - the file name included - contained
    /// the substring "test", which silently dropped production files whose names happen to spell
    /// those four letters across a word boundary: <c>ValidateStartupInstancesTask</c>,
    /// <c>CdcAggregateStatusEvaluator</c>, <c>RelationalCompositeStoredAuthorization</c>,
    /// <c>RelationalCompositeStatementRewriter</c>, <c>CdcBindingStateStore</c> and
    /// <c>LocalCdcBindingStateStore</c>. A guard that quietly stops looking at production code is
    /// worse than no guard, because it still reports success.
    /// <see cref="It_scans_production_source_and_no_test_project_source"/> holds this to its word.
    /// </remarks>
    private static bool IsExcluded(string relativePath)
    {
        string[] segments = relativePath.Split('/');

        // Length - 1 stops before the file name, which is never a directory.
        for (int index = 0; index < segments.Length - 1; index++)
        {
            if (IsExcludedDirectory(segments[index]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Build output, the <c>src/dms/tests</c> container directory, and any project directory named
    /// for a test assembly - <c>*.Tests</c>, <c>*.Tests.Unit</c>, <c>*.Tests.Integration</c>,
    /// <c>*.Tests.E2E</c>, and the shared <c>*.Tests.Common</c> helper libraries.
    /// </summary>
    private static bool IsExcludedDirectory(string segment) =>
        segment is "bin" or "obj"
        || segment.Equals("tests", StringComparison.OrdinalIgnoreCase)
        || segment.EndsWith(".Tests", StringComparison.Ordinal)
        || segment.Contains(".Tests.", StringComparison.Ordinal);

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
