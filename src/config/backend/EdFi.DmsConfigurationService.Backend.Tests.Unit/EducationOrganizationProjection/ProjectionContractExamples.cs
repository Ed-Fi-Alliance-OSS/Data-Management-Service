// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

/// <summary>The category and code the contract assigns to a DMS response (<c>x-ed-fi-cms-classification</c>).</summary>
public sealed record ContractClassification(string Schema, string? ProblemType, string Category, string Code);

/// <summary>
/// The checked-in DMS-1440 contract (reference/design/edorg-projection-DMS-1440), located by walking up from the test
/// output directory to the repository's design folder.
/// </summary>
public static partial class ProjectionContractExamples
{
    private static readonly Lazy<string> _contractDirectory = new(FindContractDirectory);

    public static string ExamplesDirectory => Path.Combine(_contractDirectory.Value, "contract", "examples");

    public static byte[] Bytes(string fileName) =>
        File.ReadAllBytes(Path.Combine(ExamplesDirectory, fileName));

    public static IEnumerable<string> ProblemExampleFiles =>
        Directory
            .EnumerateFiles(ExamplesDirectory, "problem-*.json")
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// Every schema of the OpenAPI document that carries <c>x-ed-fi-cms-classification</c>, with the problem
    /// <c>type</c> constant it declares (none for the non-problem 500 body).
    /// </summary>
    public static IReadOnlyList<ContractClassification> Classifications()
    {
        string[] lines = File.ReadAllLines(
            Path.Combine(_contractDirectory.Value, "education-organization-projection.v1.openapi.yaml")
        );

        List<ContractClassification> classifications = [];
        string? schema = null;
        ContractClassification? pending = null;
        foreach (string line in lines)
        {
            if (SchemaLine().Match(line) is { Success: true } schemaMatch)
            {
                if (pending is not null)
                {
                    classifications.Add(pending);
                }
                schema = schemaMatch.Groups[1].Value;
                pending = null;
            }
            else if (ClassificationLine().Match(line) is { Success: true } classification)
            {
                pending = new ContractClassification(
                    schema!,
                    null,
                    classification.Groups[1].Value,
                    classification.Groups[2].Value
                );
            }
            else if (pending is not null && TypeConstLine().Match(line) is { Success: true } type)
            {
                pending = pending with { ProblemType = type.Groups[1].Value };
            }
        }
        if (pending is not null)
        {
            classifications.Add(pending);
        }
        return classifications;
    }

    private static string FindContractDirectory()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "reference",
                "design",
                "edorg-projection-DMS-1440"
            );
            if (Directory.Exists(Path.Combine(candidate, "contract", "examples")))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            "The DMS-1440 contract folder was not found above the test directory."
        );
    }

    [GeneratedRegex(@"^    ([A-Za-z]+):\s*$")]
    private static partial Regex SchemaLine();

    [GeneratedRegex(@"x-ed-fi-cms-classification: \{ category: (\w+), code: (\w+)")]
    private static partial Regex ClassificationLine();

    [GeneratedRegex(@"type: \{ const: '([^']+)' \}")]
    private static partial Regex TypeConstLine();
}
