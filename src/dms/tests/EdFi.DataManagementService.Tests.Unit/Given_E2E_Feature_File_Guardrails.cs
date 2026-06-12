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
    private DirectoryInfo _dmsFeaturesDirectory = null!;
    private DirectoryInfo _instanceManagementFeaturesDirectory = null!;

    [SetUp]
    public void Setup()
    {
        _repositoryRoot = FindRepositoryRoot();
        _dmsFeaturesDirectory = new DirectoryInfo(
            Path.Combine(
                _repositoryRoot.FullName,
                "src",
                "dms",
                "tests",
                "EdFi.DataManagementService.Tests.E2E",
                "Features"
            )
        );
        _instanceManagementFeaturesDirectory = new DirectoryInfo(
            Path.Combine(
                _repositoryRoot.FullName,
                "src",
                "dms",
                "tests",
                "EdFi.InstanceManagement.Tests.E2E",
                "Features"
            )
        );
    }

    [Test]
    public void It_does_not_mix_fake_background_scenarios_with_relational_tags_in_the_same_feature_file()
    {
        string[] offendingFeatureFiles =
        [
            .. _dmsFeaturesDirectory
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

    [Test]
    public void It_assigns_exactly_one_relational_ci_shard_tag_to_every_relational_backend_scenario()
    {
        string[] offendingScenarios = EnumerateScenariosWithTags(_dmsFeaturesDirectory)
            .Where(s => s.Tags.Contains("@relational-backend", StringComparer.OrdinalIgnoreCase))
            .Where(s =>
                s.Tags.Count(t => t.StartsWith("@relational-ci-shard-", StringComparison.OrdinalIgnoreCase))
                != 1
            )
            .Select(s => $"{s.RelativePath}:{s.LineNumber} ({s.Title})")
            .ToArray();

        offendingScenarios
            .Should()
            .BeEmpty(
                "each @relational-backend scenario must carry exactly one @relational-ci-shard-N tag so PR CI shards balance and never overlap"
            );
    }

    [Test]
    public void It_never_applies_a_relational_ci_shard_tag_outside_the_relational_lane()
    {
        string[] offendingScenarios = EnumerateScenariosWithTags(_dmsFeaturesDirectory)
            .Where(s =>
                s.Tags.Any(t => t.StartsWith("@relational-ci-shard-", StringComparison.OrdinalIgnoreCase))
            )
            .Where(s => !s.Tags.Contains("@relational-backend", StringComparer.OrdinalIgnoreCase))
            .Select(s => $"{s.RelativePath}:{s.LineNumber} ({s.Title})")
            .ToArray();

        offendingScenarios
            .Should()
            .BeEmpty("@relational-ci-shard-N tags belong only to scenarios in the relational lane");
    }

    [Test]
    public void It_only_uses_relational_ci_shard_numbers_in_the_supported_range()
    {
        string[] offendingTags = EnumerateScenariosWithTags(_dmsFeaturesDirectory)
            .SelectMany(s =>
                s.Tags.Where(t => t.StartsWith("@relational-ci-shard-", StringComparison.OrdinalIgnoreCase))
                    .Select(t => new
                    {
                        s.RelativePath,
                        s.LineNumber,
                        Tag = t,
                    })
            )
            .Where(x =>
            {
                string suffix = x.Tag.Substring("@relational-ci-shard-".Length);
                return !int.TryParse(suffix, out int n) || n < 1 || n > 4;
            })
            .Select(x => $"{x.RelativePath}:{x.LineNumber} ({x.Tag})")
            .ToArray();

        offendingTags
            .Should()
            .BeEmpty(
                "shard numbers must be in [1..4]; rebalancing the matrix size is a separate, deliberate change"
            );
    }

    [Test]
    public void It_assigns_exactly_one_instance_management_ci_shard_tag_to_every_instance_management_scenario()
    {
        string[] offendingScenarios = EnumerateScenariosWithTags(_instanceManagementFeaturesDirectory)
            .Where(s =>
                s.Tags.Count(t =>
                    t.StartsWith("@instance-management-ci-shard-", StringComparison.OrdinalIgnoreCase)
                ) != 1
            )
            .Select(s => $"{s.RelativePath}:{s.LineNumber} ({s.Title})")
            .ToArray();

        offendingScenarios
            .Should()
            .BeEmpty(
                "every instance management E2E scenario must carry exactly one @instance-management-ci-shard-N tag so PR CI shards never drop coverage"
            );
    }

    [Test]
    public void It_only_uses_instance_management_ci_shard_numbers_in_the_supported_range()
    {
        string[] offendingTags = EnumerateScenariosWithTags(_instanceManagementFeaturesDirectory)
            .SelectMany(s =>
                s.Tags.Where(t =>
                        t.StartsWith("@instance-management-ci-shard-", StringComparison.OrdinalIgnoreCase)
                    )
                    .Select(t => new
                    {
                        s.RelativePath,
                        s.LineNumber,
                        Tag = t,
                    })
            )
            .Where(x =>
            {
                string suffix = x.Tag.Substring("@instance-management-ci-shard-".Length);
                return !int.TryParse(suffix, out int n) || n < 1 || n > 2;
            })
            .Select(x => $"{x.RelativePath}:{x.LineNumber} ({x.Tag})")
            .ToArray();

        offendingTags
            .Should()
            .BeEmpty(
                "instance management shard numbers must be in [1..2]; rebalancing the matrix size is a separate, deliberate change"
            );
    }

    /// <summary>
    /// Enumerates every Scenario / Scenario Outline under the Features tree along with the
    /// feature-level and scenario-level tags that attach to it.
    /// </summary>
    /// <remarks>
    /// SpecFlow / Reqnroll treat comment lines (those starting with a hash, '#') as transparent
    /// inside a tag block; tags above such a comment still attach to the scenario below it,
    /// so the backward walk skips blank lines and hash-prefixed lines and only stops at the
    /// first non-tag, non-comment line.
    /// </remarks>
    private IEnumerable<ScenarioRecord> EnumerateScenariosWithTags(DirectoryInfo featuresDirectory)
    {
        foreach (
            FileInfo featureFile in featuresDirectory.EnumerateFiles("*.feature", SearchOption.AllDirectories)
        )
        {
            string[] lines = File.ReadAllLines(featureFile.FullName);
            string relativePath = Path.GetRelativePath(_repositoryRoot.FullName, featureFile.FullName);
            IReadOnlyCollection<string> featureTags = [];

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("Feature:", StringComparison.Ordinal))
                {
                    featureTags = CollectTagsAbove(lines, i);
                    continue;
                }

                bool isScenario =
                    trimmed.StartsWith("Scenario:", StringComparison.Ordinal)
                    || trimmed.StartsWith("Scenario Outline:", StringComparison.Ordinal);

                if (!isScenario)
                {
                    continue;
                }

                string[] tags = [.. featureTags, .. CollectTagsAbove(lines, i)];

                yield return new ScenarioRecord(relativePath.Replace('\\', '/'), i + 1, trimmed, tags);
            }
        }
    }

    private static string[] CollectTagsAbove(string[] lines, int lineIndex)
    {
        var tags = new List<string>();
        for (int j = lineIndex - 1; j >= 0; j--)
        {
            string previous = lines[j].Trim();

            if (previous.Length == 0)
            {
                continue;
            }

            if (previous.StartsWith('#'))
            {
                continue;
            }

            if (!previous.StartsWith('@'))
            {
                break;
            }

            tags.AddRange(previous.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        }

        return [.. tags];
    }

    private sealed record ScenarioRecord(
        string RelativePath,
        int LineNumber,
        string Title,
        IReadOnlyCollection<string> Tags
    );

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
