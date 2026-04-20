// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public class Given_E2E_Feature_File_Guardrails
{
    private DirectoryInfo _repositoryRoot = null!;
    private DirectoryInfo _featuresDirectory = null!;

    [SetUp]
    public void Setup()
    {
        _repositoryRoot = FindRepositoryRoot();
        _featuresDirectory = new DirectoryInfo(
            Path.Combine(
                _repositoryRoot.FullName,
                "src",
                "dms",
                "tests",
                "EdFi.DataManagementService.Tests.E2E",
                "Features"
            )
        );
    }

    [Test]
    public void It_does_not_mix_fake_background_scenarios_with_relational_tags_in_the_same_feature_file()
    {
        string[] offendingFeatureFiles =
        [
            .. _featuresDirectory
                .EnumerateFiles("*.feature", SearchOption.AllDirectories)
                .Select(featureFile => new
                {
                    FeaturePath = Path.GetRelativePath(_repositoryRoot.FullName, featureFile.FullName),
                    Contents = File.ReadAllText(featureFile.FullName),
                })
                .Where(featureFile =>
                    featureFile.Contents.Contains("Scenario: 00 Background", StringComparison.Ordinal)
                    && featureFile.Contents.Contains(
                        "@relational-backend",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .Select(featureFile => featureFile.FeaturePath),
        ];

        offendingFeatureFiles
            .Should()
            .BeEmpty(
                "relational-tagged scenarios run under category filters, so fake seed scenarios in the same feature are skipped"
            );
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "build-dms.ps1")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from test output.");
    }
}
