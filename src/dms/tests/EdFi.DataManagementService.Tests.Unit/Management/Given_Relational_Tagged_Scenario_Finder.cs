// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit.Management;

[TestFixture]
public class Given_Relational_Tagged_Scenario_Finder
{
    private DirectoryInfo _featuresDirectory = null!;

    [SetUp]
    public void Setup()
    {
        _featuresDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"relational-tagged-scenario-finder-{Guid.NewGuid():N}")
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (_featuresDirectory.Exists)
        {
            _featuresDirectory.Delete(recursive: true);
        }
    }

    [Test]
    public void It_returns_no_results_when_no_relational_scenarios_exist()
    {
        WriteFeatureFile(
            "Resources/Legacy.feature",
            """
            Feature: Legacy

                @API-184
                Scenario: Legacy scenario
                    Given a legacy scenario
            """
        );

        IReadOnlyList<RelationalTaggedScenarioFinder.RelationalTaggedScenario> taggedScenarios =
            RelationalTaggedScenarioFinder.FindTaggedScenarios(_featuresDirectory.FullName);

        taggedScenarios.Should().BeEmpty();
    }

    [Test]
    public void It_finds_multiple_relational_scenarios()
    {
        WriteFeatureFile(
            "Resources/Relational.feature",
            """
            Feature: Relational

                @API-184 @relational-backend
                Scenario: Relational update scenario
                    Given a relational scenario
            """
        );

        WriteFeatureFile(
            "Resources/Another.feature",
            """
            Feature: Another

                @API-185 @relational-backend
                Scenario: Another relational scenario
                    Given another relational scenario
            """
        );

        IReadOnlyList<RelationalTaggedScenarioFinder.RelationalTaggedScenario> taggedScenarios =
            RelationalTaggedScenarioFinder.FindTaggedScenarios(_featuresDirectory.FullName);

        taggedScenarios.Should().HaveCount(2);
        taggedScenarios
            .Select(scenario => $"{scenario.FeaturePath}:{scenario.ScenarioName}")
            .Should()
            .Equal(
                "Resources/Another.feature:Another relational scenario",
                "Resources/Relational.feature:Relational update scenario"
            );
    }

    [Test]
    public void It_counts_feature_level_relational_tags_for_each_scenario()
    {
        WriteFeatureFile(
            "Resources/FeatureTagged.feature",
            """
            @relational-backend
            Feature: Feature tagged

                Scenario: First scenario
                    Given a first scenario

                Scenario: Second scenario
                    Given a second scenario
            """
        );

        IReadOnlyList<RelationalTaggedScenarioFinder.RelationalTaggedScenario> taggedScenarios =
            RelationalTaggedScenarioFinder.FindTaggedScenarios(_featuresDirectory.FullName);

        taggedScenarios.Should().HaveCount(2);
        taggedScenarios
            .Select(scenario => scenario.ScenarioName)
            .Should()
            .Equal("First scenario", "Second scenario");
    }

    [Test]
    public void It_treats_tagged_scenario_outlines_like_relational_scenarios()
    {
        WriteFeatureFile(
            "Resources/Outline.feature",
            """
            Feature: Outline

                @API-184 @relational-backend
                Scenario Outline: Relational outline
                    Given a relational scenario for "<value>"

                Examples:
                    | value |
                    | one   |
                    | two   |
            """
        );

        IReadOnlyList<RelationalTaggedScenarioFinder.RelationalTaggedScenario> taggedScenarios =
            RelationalTaggedScenarioFinder.FindTaggedScenarios(_featuresDirectory.FullName);

        taggedScenarios.Should().ContainSingle();
        taggedScenarios[0].ScenarioName.Should().Be("Relational outline");
        taggedScenarios[0].IsScenarioOutline.Should().BeTrue();
    }

    private void WriteFeatureFile(string relativePath, string contents)
    {
        string featureFilePath = Path.Combine(
            _featuresDirectory.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)
        );

        string? parentDirectory = Path.GetDirectoryName(featureFilePath);

        if (parentDirectory is not null)
        {
            Directory.CreateDirectory(parentDirectory);
        }

        File.WriteAllText(featureFilePath, contents.ReplaceLineEndings(Environment.NewLine));
    }
}
