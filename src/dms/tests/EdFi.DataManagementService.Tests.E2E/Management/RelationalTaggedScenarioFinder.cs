// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.Management;

internal static class RelationalTaggedScenarioFinder
{
    private const string RelationalBackendTag = "@relational-backend";

    internal static IReadOnlyList<RelationalTaggedScenario> FindTaggedScenarios(string featuresDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(featuresDirectoryPath))
        {
            throw new ArgumentException(
                "A features directory path is required.",
                nameof(featuresDirectoryPath)
            );
        }

        if (!Directory.Exists(featuresDirectoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Features directory was not found: {featuresDirectoryPath}"
            );
        }

        List<RelationalTaggedScenario> taggedScenarios = [];

        foreach (
            string featureFilePath in Directory
                .EnumerateFiles(featuresDirectoryPath, "*.feature", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
        )
        {
            taggedScenarios.AddRange(
                FindTaggedScenariosInFeatureFile(featureFilePath, featuresDirectoryPath)
            );
        }

        return taggedScenarios;
    }

    private static IEnumerable<RelationalTaggedScenario> FindTaggedScenariosInFeatureFile(
        string featureFilePath,
        string featuresDirectoryPath
    )
    {
        List<string> featureTags = [];
        List<string> ruleTags = [];
        List<string> pendingTags = [];
        int lineNumber = 0;

        foreach (string rawLine in File.ReadLines(featureFilePath))
        {
            lineNumber++;

            string line = rawLine.Trim();

            if (line.Length is 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('@'))
            {
                pendingTags.AddRange(
                    line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                );
                continue;
            }

            if (line.StartsWith("Feature:", StringComparison.Ordinal))
            {
                featureTags = [.. pendingTags];
                ruleTags = [];
                pendingTags.Clear();
                continue;
            }

            if (line.StartsWith("Rule:", StringComparison.Ordinal))
            {
                ruleTags = [.. pendingTags];
                pendingTags.Clear();
                continue;
            }

            if (
                line.StartsWith("Scenario:", StringComparison.Ordinal)
                || line.StartsWith("Scenario Outline:", StringComparison.Ordinal)
            )
            {
                bool isScenarioOutline = line.StartsWith("Scenario Outline:", StringComparison.Ordinal);
                List<string> effectiveTags = [.. featureTags, .. ruleTags, .. pendingTags];

                if (effectiveTags.Contains(RelationalBackendTag, StringComparer.OrdinalIgnoreCase))
                {
                    yield return new RelationalTaggedScenario(
                        Path.GetRelativePath(featuresDirectoryPath, featureFilePath),
                        lineNumber,
                        line[(line.IndexOf(':') + 1)..].Trim(),
                        isScenarioOutline
                    );
                }

                pendingTags.Clear();
                continue;
            }

            if (
                line.StartsWith("Background:", StringComparison.Ordinal)
                || line.StartsWith("Examples:", StringComparison.Ordinal)
            )
            {
                pendingTags.Clear();
                continue;
            }

            pendingTags.Clear();
        }
    }

    internal sealed record RelationalTaggedScenario(
        string FeaturePath,
        int LineNumber,
        string ScenarioName,
        bool IsScenarioOutline
    );
}
