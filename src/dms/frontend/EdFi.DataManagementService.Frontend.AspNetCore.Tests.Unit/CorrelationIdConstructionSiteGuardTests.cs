// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// A source-scanning guard over the two things nothing in the type system can prevent: a
/// <c>TraceId</c> built outside the one place that normalizes it, and a correlation ID routed
/// through the strict <c>Method</c>/<c>Path</c> sanitizer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a source scan.</b> Normalization deliberately does not live inside <c>TraceId</c>,
/// which sits in a published contract assembly that must not depend on frontend configuration, so
/// nothing structurally prevents a raw <c>new TraceId(...)</c>; this is the guard that decision
/// promised. The second scan exists because the strict sanitizer would break FR-LOG-6 parity, and
/// most sites that log a correlation ID assert nothing.
/// </para>
/// <para>
/// <b>How it looks.</b> Each production file is parsed with <see cref="CSharpSyntaxTree"/>.
/// Parsing is syntax-only - no compilation, no semantic model, no project references - so the
/// lexer takes care of comments, string literals and balanced argument lists, and nothing here has
/// to. An explicit <c>new TraceId(...)</c> is an <see cref="ObjectCreationExpressionSyntax"/>; a
/// target-typed <c>new(...)</c> is an <see cref="ImplicitObjectCreationExpressionSyntax"/> whose
/// type is read off the declaration around it - a variable, field or property type, or the
/// declared return type of the method, local function, accessor or expression-bodied member it
/// returns from.
/// </para>
/// <para>
/// <b>What it cannot see.</b> Without a semantic model a target-typed <c>new()</c> has no declared
/// type to read where the type comes from the other side: an argument position
/// (<c>Method(new(raw))</c>), a collection or object initializer element, an assignment, and a
/// <c>var</c> declaration. A lambda body counts only when the lambda declares a return type. The
/// sanitizer scan matches identifiers, so a correlation ID reaching one under a name that says
/// nothing about correlation is invisible to it.
/// </para>
/// </remarks>
[TestFixture]
[Parallelizable]
public class CorrelationIdConstructionSiteGuardTests
{
    /// <summary>
    /// The only production files allowed to construct a <see cref="Core.External.Model.TraceId"/>,
    /// each mapped to the exact number of constructions it may contain.
    /// </summary>
    /// <remarks>
    /// <c>AspNetCoreFrontend.cs</c> is the single ingestion point, and its two sites are
    /// <c>ExtractTraceIdFrom</c> and <c>CorrelationIdIngestion.ForServerGeneratedIdentifier</c>,
    /// both of which normalize. <c>No.cs</c> is the null-object factory, unreachable from an HTTP
    /// request (declined finding D-3). The value is a count rather than a bare path because a bare
    /// path permits the whole file: a second raw <c>new TraceId(...)</c> added beside the first
    /// would then change nothing the guard could see. Adding or raising an entry asserts that the
    /// new site either normalizes its input or cannot receive client input.
    /// </remarks>
    private static readonly Dictionary<string, int> PermittedTraceIdConstructionSites = new(
        StringComparer.Ordinal
    )
    {
        ["src/dms/core/EdFi.DataManagementService.Core/Model/No.cs"] = 1,
        ["src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/AspNetCoreFrontend.cs"] = 2,
    };

    /// <summary>
    /// The internally-controlled-value sanitizer under both of its names -
    /// <c>LogSanitizer.SanitizeInternalValueForLog</c> and the Core facade
    /// <c>LoggingSanitizer.SanitizeInternalValueForLogging</c>.
    /// </summary>
    private static readonly string[] InternalValueSanitizerNames =
    [
        "SanitizeInternalValueForLog",
        "SanitizeInternalValueForLogging",
    ];

    [Test]
    public void It_permits_a_trace_id_to_be_constructed_only_where_it_is_normalized_or_inert()
    {
        Construction[] matches = [.. ProductionSourceFiles().SelectMany(TraceIdConstructionsIn)];

        string[] offendingSites =
        [
            .. matches
                .Where(match => !PermittedTraceIdConstructionSites.ContainsKey(match.RelativePath))
                .Select(match => match.Description)
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

        // Counting, rather than distincting on the path, is also what makes the guard fail closed:
        // a scan that has quietly stopped matching finds zero where an entry expects one.
        Dictionary<string, Construction[]> constructionsByFile = matches
            .GroupBy(match => match.RelativePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        string[] countDiscrepancies =
        [
            .. PermittedTraceIdConstructionSites
                .Select(site =>
                    (
                        Path: site.Key,
                        Expected: site.Value,
                        Found: constructionsByFile.TryGetValue(site.Key, out Construction[]? found)
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
                    + "names, not the file as a whole. Read the site before changing the count - the "
                    + "count is the record that someone confirmed it. Discrepancies:{1}{2}",
                nameof(PermittedTraceIdConstructionSites),
                Environment.NewLine,
                string.Join(Environment.NewLine, countDiscrepancies)
            );
    }

    /// <summary>
    /// A permitted file whose construction count no longer matches, named with the direction it
    /// moved in and every construction found, so the failure can be acted on as it stands.
    /// </summary>
    private static string DescribeCountDiscrepancy(string relativePath, int expected, Construction[] found)
    {
        string verdict = found.Length switch
        {
            _ when found.Length > expected => "a NEW TraceId construction has appeared in an "
                + "ALREADY-PERMITTED file, so its entry did not have to change for this to land. "
                + "Confirm the new site normalizes its input - or cannot receive client input - "
                + "and only then raise the count",
            0 => "this file no longer constructs a TraceId, so the entry is stale. Remove it",
            _ => "a permitted construction has been removed. Lower the count",
        };

        return $"{relativePath}: expected {expected}, found {found.Length} - {verdict}."
            + string.Concat(found.Select(match => $"{Environment.NewLine}    {match.Description}"));
    }

    [Test]
    public void It_never_routes_a_correlation_id_through_the_strict_method_and_path_sanitizer()
    {
        List<string> offendingSites = [];
        int scannedCallSites = 0;

        foreach (SourceFile file in ProductionSourceFiles())
        {
            foreach (
                InvocationExpressionSyntax invocation in file
                    .Root.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
            )
            {
                if (!InternalValueSanitizerNames.Contains(InvokedName(invocation.Expression)))
                {
                    continue;
                }

                scannedCallSites++;

                ArgumentSyntax[] correlationIdArguments =
                [
                    .. invocation.ArgumentList.Arguments.Where(NamesACorrelationId),
                ];

                if (correlationIdArguments.Length > 0)
                {
                    offendingSites.Add(
                        $"{file.Describe(invocation)}: {invocation.Expression}"
                            + $"({string.Join(", ", correlationIdArguments.Select(argument => argument.ToString()))})"
                    );
                }
            }
        }

        offendingSites
            .Should()
            .BeEmpty(
                "the strict Method/Path allowlist strips punctuation an upstream correlation ID "
                    + "legitimately uses, so the logged value would differ from the correlationId in "
                    + "the response body for the same request. Use "
                    + "LoggingSanitizer.SanitizeCorrelationId, or pass the already-normalized value "
                    + "raw. Offending sites:{0}{1}",
                Environment.NewLine,
                string.Join(Environment.NewLine, offendingSites.Order(StringComparer.Ordinal))
            );

        // The assertion above is vacuous if no call site is found, which is what renaming either
        // sanitizer would cause. Production source invokes the pair a few hundred times, so a floor
        // of fifty fails loudly on a stale name while leaving room for call sites to come and go.
        scannedCallSites
            .Should()
            .BeGreaterThan(
                50,
                "{0} must still hold the sanitizer's real spelling. Finding almost no call sites "
                    + "means it was renamed and this guard has stopped guarding, not that the "
                    + "codebase stopped calling it",
                nameof(InternalValueSanitizerNames)
            );
    }

    /// <summary>
    /// A scan that quietly stops reaching production code still passes, so its coverage is itself
    /// asserted.
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

        // The six production files a substring match on "test" in the file name used to drop.
        // Named individually so that losing one is a decision someone makes here.
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
                "a production file must be scanned whatever its name spells. If one was renamed, "
                    + "update the list; if the filter dropped it, the filename hole has reopened. "
                    + "Missing:{0}{1}",
                Environment.NewLine,
                string.Join(Environment.NewLine, missing)
            );

        // Cross-checked against <IsTestProject>, deliberately a different mechanism from the
        // directory-name match the filter uses, so a project named outside the convention fails
        // here rather than quietly joining the scanned set.
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
                "a test project may construct a raw TraceId and hand a trace-shaped value to the "
                    + "strict sanitizer, so scanning one produces noise, not findings. Teach "
                    + "IsExcludedDirectory about this project's naming. Scanned:{0}{1}",
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

    /// <summary>Every construction of a <c>TraceId</c> in one file, in either spelling.</summary>
    private static IEnumerable<Construction> TraceIdConstructionsIn(SourceFile file) =>
        file
            .Root.DescendantNodes()
            .Where(node =>
                node switch
                {
                    ObjectCreationExpressionSyntax creation => NamesTraceId(creation.Type),
                    ImplicitObjectCreationExpressionSyntax creation => NamesTraceId(
                        DeclaredTypeTargetedBy(creation)
                    ),
                    _ => false,
                }
            )
            .Select(node => new Construction(file.RelativePath, file.Describe(node)));

    /// <summary>
    /// The declared type a target-typed <c>new(...)</c> converts to, in the three positions syntax
    /// alone settles: an initializer, an expression body, and a <c>return</c>.
    /// <see langword="null"/> elsewhere, where no type is named at the construction site.
    /// </summary>
    private static TypeSyntax? DeclaredTypeTargetedBy(ImplicitObjectCreationExpressionSyntax creation) =>
        creation.Parent switch
        {
            EqualsValueClauseSyntax initializer => DeclaredTypeOf(initializer.Parent),
            ArrowExpressionClauseSyntax body => DeclaredTypeOf(body.Parent),
            ReturnStatementSyntax statement => DeclaredTypeOf(EnclosingFunctionOf(statement)),
            _ => null,
        };

    /// <summary>
    /// The type a declaration declares. <see langword="null"/> for <c>var</c>, for a constructor,
    /// and for a lambda that leaves its return type to inference.
    /// </summary>
    private static TypeSyntax? DeclaredTypeOf(SyntaxNode? declaration) =>
        declaration switch
        {
            VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax variable } => variable.Type,
            ParameterSyntax parameter => parameter.Type,
            PropertyDeclarationSyntax property => property.Type,
            IndexerDeclarationSyntax indexer => indexer.Type,
            MethodDeclarationSyntax method => method.ReturnType,
            LocalFunctionStatementSyntax function => function.ReturnType,
            OperatorDeclarationSyntax @operator => @operator.ReturnType,
            ConversionOperatorDeclarationSyntax conversion => conversion.Type,
            ParenthesizedLambdaExpressionSyntax lambda => lambda.ReturnType,
            AccessorDeclarationSyntax { Parent.Parent: SyntaxNode member } => DeclaredTypeOf(member),
            _ => null,
        };

    /// <summary>
    /// The function a <c>return</c> belongs to. A lambda stops the walk: a <c>return</c> inside one
    /// leaves the lambda, so reading the enclosing member's type there would be wrong.
    /// </summary>
    private static SyntaxNode? EnclosingFunctionOf(SyntaxNode node) =>
        node.Ancestors()
            .FirstOrDefault(ancestor =>
                ancestor
                    is AnonymousFunctionExpressionSyntax
                        or LocalFunctionStatementSyntax
                        or AccessorDeclarationSyntax
                        or BaseMethodDeclarationSyntax
            );

    /// <summary>Whether a type names <c>TraceId</c>, however qualified or annotated.</summary>
    private static bool NamesTraceId(TypeSyntax? type) =>
        type switch
        {
            NullableTypeSyntax nullable => NamesTraceId(nullable.ElementType),
            QualifiedNameSyntax qualified => NamesTraceId(qualified.Right),
            AliasQualifiedNameSyntax alias => NamesTraceId(alias.Name),
            SimpleNameSyntax name => name.Identifier.ValueText == "TraceId",
            _ => false,
        };

    /// <summary>The name a call invokes, ignoring any receiver or type arguments.</summary>
    private static string? InvokedName(ExpressionSyntax invoked) =>
        invoked switch
        {
            MemberAccessExpressionSyntax member => InvokedName(member.Name),
            MemberBindingExpressionSyntax binding => InvokedName(binding.Name),
            SimpleNameSyntax name => name.Identifier.ValueText,
            _ => null,
        };

    /// <summary>
    /// Whether an argument mentions an identifier that says "correlation ID". Only identifiers are
    /// read, so a string literal containing the word does not trip it.
    /// </summary>
    private static bool NamesACorrelationId(ArgumentSyntax argument) =>
        argument
            .DescendantNodesAndSelf()
            .OfType<SimpleNameSyntax>()
            .Select(name => name.Identifier.ValueText)
            .Any(name =>
                name.Contains("trace", StringComparison.OrdinalIgnoreCase)
                || name.Contains("correlation", StringComparison.OrdinalIgnoreCase)
            );

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

            yield return new SourceFile(
                relativePath,
                CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)
            );
        }
    }

    /// <summary>
    /// Build output and every test project. A test is allowed to construct a raw <c>TraceId</c> -
    /// most must, to exercise the code under test - and to pass a trace-shaped value to the strict
    /// sanitizer to prove what it does to one.
    /// </summary>
    /// <remarks>
    /// The decision is made on the <b>directories</b> a file sits in, never on its own name. An
    /// earlier spelling matched the substring "test" anywhere, which silently dropped six
    /// production files whose names spell those four letters across a word boundary.
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

    /// <summary>One <c>TraceId</c> construction, as <c>path:line: text</c>.</summary>
    private sealed record Construction(string RelativePath, string Description);

    private sealed record SourceFile(string RelativePath, SyntaxTree Tree)
    {
        public SyntaxNode Root { get; } = Tree.GetRoot();

        /// <summary><c>path:line</c> plus the line's text, so a failure needs no follow-up.</summary>
        public string Describe(SyntaxNode node)
        {
            FileLinePositionSpan span = Tree.GetLineSpan(node.Span);
            TextLine line = Tree.GetText().Lines[span.StartLinePosition.Line];

            return $"{RelativePath}:{span.StartLinePosition.Line + 1}: {line.ToString().Trim()}";
        }
    }
}
