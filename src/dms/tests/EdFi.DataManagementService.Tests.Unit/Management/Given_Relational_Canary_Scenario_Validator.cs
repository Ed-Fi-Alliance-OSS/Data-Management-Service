// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit.Management;

[TestFixture]
public class Given_Relational_Canary_Scenario_Validator
{
    private DirectoryInfo _featuresDirectory = null!;

    [SetUp]
    public void Setup()
    {
        _featuresDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"relational-canary-validator-{Guid.NewGuid():N}")
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
    public void It_allows_exactly_one_relational_scenario()
    {
        WriteFeatureFile(
            "Resources/Canary.feature",
            """
            Feature: Canary

                @API-184 @relational-backend
                Scenario: Relational canary
                    Given a canary scenario
            """
        );

        Action action = () =>
            RelationalCanaryScenarioValidator.AssertExactlyOneTaggedScenario(_featuresDirectory.FullName);

        action.Should().NotThrow();
    }

    [Test]
    public void It_fails_when_no_relational_scenarios_exist()
    {
        WriteFeatureFile(
            "Resources/Canary.feature",
            """
            Feature: Canary

                @API-184
                Scenario: Legacy scenario
                    Given a legacy scenario
            """
        );

        Action action = () =>
            RelationalCanaryScenarioValidator.AssertExactlyOneTaggedScenario(_featuresDirectory.FullName);

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*requires exactly one concrete scenario tagged with '@relational-backend', but found none*"
            );
    }

    [Test]
    public void It_fails_when_multiple_relational_scenarios_exist()
    {
        WriteFeatureFile(
            "Resources/Canary.feature",
            """
            Feature: Canary

                @API-184 @relational-backend
                Scenario: Relational canary
                    Given a canary scenario
            """
        );

        WriteFeatureFile(
            "Resources/Another.feature",
            """
            Feature: Another

                @API-185 @relational-backend
                Scenario: Another relational scenario
                    Given another canary scenario
            """
        );

        Action action = () =>
            RelationalCanaryScenarioValidator.AssertExactlyOneTaggedScenario(_featuresDirectory.FullName);

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*requires exactly one concrete scenario tagged with '@relational-backend'*but found 2*Another.feature*Another relational scenario*Canary.feature*Relational canary*"
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

        IReadOnlyList<RelationalCanaryScenarioValidator.RelationalTaggedScenario> taggedScenarios =
            RelationalCanaryScenarioValidator.FindTaggedScenarios(_featuresDirectory.FullName);

        taggedScenarios.Should().HaveCount(2);
        taggedScenarios
            .Select(scenario => scenario.ScenarioName)
            .Should()
            .Equal("First scenario", "Second scenario");
    }

    [Test]
    public void It_fails_when_relational_tag_is_applied_to_a_scenario_outline()
    {
        WriteFeatureFile(
            "Resources/Outline.feature",
            """
            Feature: Outline

                @API-184 @relational-backend
                Scenario Outline: Relational canary outline
                    Given a canary scenario for "<value>"

                Examples:
                    | value |
                    | one   |
                    | two   |
            """
        );

        Action action = () =>
            RelationalCanaryScenarioValidator.AssertExactlyOneTaggedScenario(_featuresDirectory.FullName);

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*requires exactly one concrete scenario tagged with '@relational-backend'. Tagged Scenario Outline entries are not supported*Outline.feature*Relational canary outline*"
            );
    }

    [Test]
    public void It_fails_when_relational_tag_is_applied_to_a_scenario_outline_with_one_example()
    {
        WriteFeatureFile(
            "Resources/SingleExampleOutline.feature",
            """
            Feature: Single example outline

                @API-184 @relational-backend
                Scenario Outline: Relational single example outline
                    Given a canary scenario for "<value>"

                Examples:
                    | value |
                    | one   |
            """
        );

        Action action = () =>
            RelationalCanaryScenarioValidator.AssertExactlyOneTaggedScenario(_featuresDirectory.FullName);

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*requires exactly one concrete scenario tagged with '@relational-backend'. Tagged Scenario Outline entries are not supported*SingleExampleOutline.feature*Relational single example outline*"
            );
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
