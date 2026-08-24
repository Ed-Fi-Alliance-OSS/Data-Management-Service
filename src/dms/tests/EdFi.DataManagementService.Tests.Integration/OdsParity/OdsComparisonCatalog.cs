// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Tests.Common;

namespace EdFi.DataManagementService.Tests.Integration.OdsParity;

/// <summary>
/// The static, machine-readable ODS 7.3.2 comparison case definitions, loaded from the committed JSON.
/// </summary>
/// <remarks>
/// The case files are data, not code, so a reviewer can see which ODS version the expectations describe
/// and which approved difference each recorded divergence maps to without reading a test. The loader is
/// deliberately strict: a missing file, a missing required member, or a case that names an unknown
/// approved difference is an exception here rather than a quietly skipped case.
/// </remarks>
internal static class OdsComparisonCatalog
{
    private const string RepositoryRelativeDirectory =
        "src/dms/tests/EdFi.DataManagementService.Tests.Integration/OdsParity/CursorPartition";

    internal const string MetadataFileName = "ods-reference-metadata.json";
    internal const string DifferencesFileName = "approved-differences.json";

    internal static readonly string[] CaseFileNames =
    [
        "cursor-precedence-cases.json",
        "partition-difference-cases.json",
        "metadata-difference-cases.json",
    ];

    private static readonly Lazy<OdsComparisonDefinitions> _definitions = new(
        Load,
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    internal static OdsComparisonDefinitions Definitions => _definitions.Value;

    private static OdsComparisonDefinitions Load()
    {
        string directory = FixturePathResolver.ResolveRepositoryRelativePath(
            AppContext.BaseDirectory,
            RepositoryRelativeDirectory
        );

        JsonNode metadata = ReadJson(Path.Combine(directory, MetadataFileName));
        JsonNode differences = ReadJson(Path.Combine(directory, DifferencesFileName));

        List<ApprovedDifference> catalog =
        [
            .. differences["differences"]!
                .AsArray()
                .Select(entry => new ApprovedDifference(
                    entry!["id"]!.GetValue<string>(),
                    entry["bullet"]!.GetValue<int>(),
                    entry["summary"]!.GetValue<string>(),
                    entry["executable"]!.GetValue<bool>()
                )),
        ];

        List<ComparisonCase> cases = [];

        foreach (string fileName in CaseFileNames)
        {
            JsonNode file = ReadJson(Path.Combine(directory, fileName));

            cases.AddRange(file["cases"]!.AsArray().Select(entry => ComparisonCase.From(entry!, fileName)));
        }

        return new OdsComparisonDefinitions(metadata, catalog, cases);
    }

    private static JsonNode ReadJson(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"ODS comparison definition not found at '{path}'.", path);
        }

        return JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"ODS comparison definition '{path}' is not JSON.");
    }
}

/// <summary>The loaded definitions: reference metadata, the approved-difference catalog, and the cases.</summary>
internal sealed record OdsComparisonDefinitions(
    JsonNode Metadata,
    IReadOnlyList<ApprovedDifference> Catalog,
    IReadOnlyList<ComparisonCase> Cases
);

/// <summary>One entry of the approved-difference catalog.</summary>
internal sealed record ApprovedDifference(string Id, int Bullet, string Summary, bool Executable);

/// <summary>
/// One comparison case: the request to issue, the outcome DMS must produce, and the outcome ODS 7.3.2
/// is recorded as producing for the same request.
/// </summary>
internal sealed record ComparisonCase(
    string Id,
    string SourceFile,
    string Group,
    string Executor,
    string Query,
    string? Path,
    string? Document,
    string Shell,
    int? Seed,
    ExpectedOutcome Dms,
    ExpectedOutcome Ods,
    string Outcome,
    string? ApprovedDifference
)
{
    internal const string ParameterValidationShell = "parameterValidation";
    internal const string BadRequestShell = "badRequest";

    internal bool DeclaresDifference => Outcome == "difference";

    internal static ComparisonCase From(JsonNode entry, string sourceFile) =>
        new(
            entry["id"]!.GetValue<string>(),
            sourceFile,
            entry["group"]!.GetValue<string>(),
            entry["executor"]!.GetValue<string>(),
            entry["query"]?.GetValue<string>() ?? string.Empty,
            entry["path"]?.GetValue<string>(),
            entry["document"]?.GetValue<string>(),
            entry["shell"]?.GetValue<string>() ?? ParameterValidationShell,
            entry["seed"]?.GetValue<int>(),
            ExpectedOutcome.From(entry["dms"]!),
            ExpectedOutcome.From(entry["ods"]!),
            entry["outcome"]!.GetValue<string>(),
            entry["approvedDifference"]?.GetValue<string>()
        );
}

/// <summary>
/// One side of a comparison: the status, the ordered error list when the outcome is a rejection, and the
/// structured expectations when it is a success.
/// </summary>
/// <remarks>
/// Both sides use the same vocabulary on purpose. A recorded ODS outcome that could not be expressed in
/// the same terms as an observed DMS outcome could not be compared against it, and a case could then
/// claim a difference that never materializes.
/// </remarks>
internal sealed record ExpectedOutcome(int Status, IReadOnlyList<string>? Errors, JsonObject? Expect)
{
    internal static ExpectedOutcome From(JsonNode side) =>
        new(
            side["status"]!.GetValue<int>(),
            side["errors"] is JsonArray errors
                ? [.. errors.Select(error => error!.GetValue<string>())]
                : null,
            side["expect"]?.AsObject().DeepClone().AsObject()
        );

    /// <summary>
    /// Renders this side with the running host's configuration substituted into its placeholders, so a
    /// message or a published bound quoting a configured value cannot drift from the configuration the
    /// case is executed against.
    /// </summary>
    internal ExpectedOutcome Resolve(IReadOnlyDictionary<string, string> placeholders)
    {
        IReadOnlyList<string>? errors = Errors is null
            ? null
            : [.. Errors.Select(error => Substitute(error, placeholders))];

        JsonObject? expect = null;

        if (Expect is not null)
        {
            expect = [];

            foreach (var member in Expect)
            {
                expect[member.Key] = ResolveExpectation(member.Value, placeholders);
            }
        }

        return this with
        {
            Errors = errors,
            Expect = expect,
        };
    }

    private static JsonNode? ResolveExpectation(
        JsonNode? value,
        IReadOnlyDictionary<string, string> placeholders
    )
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonObject nested)
        {
            JsonObject resolved = [];

            foreach (var member in nested)
            {
                resolved[member.Key] = ResolveExpectation(member.Value, placeholders);
            }

            return resolved;
        }

        // A string expectation is a placeholder for a configured integer; every other value is literal.
        if (value.GetValueKind() == JsonValueKind.String)
        {
            string text = value.GetValue<string>();

            return JsonValue.Create(int.Parse(Substitute(text, placeholders), CultureInfo.InvariantCulture));
        }

        return value.DeepClone();
    }

    private static string Substitute(string text, IReadOnlyDictionary<string, string> placeholders)
    {
        string resolved = text;

        foreach (var placeholder in placeholders)
        {
            resolved = resolved.Replace(placeholder.Key, placeholder.Value, StringComparison.Ordinal);
        }

        return resolved;
    }
}
