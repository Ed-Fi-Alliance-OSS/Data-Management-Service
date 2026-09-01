// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Configuration;

[TestFixture]
public class Given_The_DocumentCacheQualification_Catalog
{
    [Test]
    public void It_defines_every_required_threshold_area_for_each_provider()
    {
        foreach (PerfProvider provider in Enum.GetValues<PerfProvider>())
        {
            DocumentCacheQualification
                .ThresholdsFor(provider)
                .Select(threshold => threshold.Area)
                .Should()
                .BeEquivalentTo(DocumentCacheQualification.RequiredThresholdAreas);
        }
    }

    [Test]
    public void It_uses_provider_specific_threshold_ids()
    {
        foreach (DocumentCacheQualificationThreshold threshold in DocumentCacheQualification.Thresholds)
        {
            threshold
                .Id.Should()
                .StartWith(PerfProviders.ArtifactName(threshold.Provider) + "-", threshold.Measurement);
        }
    }

    [Test]
    public void It_keeps_threshold_values_positive()
    {
        DocumentCacheQualification
            .Thresholds.Should()
            .OnlyContain(threshold => threshold.Maximum > 0, "each threshold is an upper bound");
    }

    [Test]
    public void It_requires_a_durable_baseline_cursor_ticket_when_restart_from_beginning_fails()
    {
        DocumentCacheQualification
            .Thresholds.Where(threshold => threshold.Area == "restartFromBeginning")
            .Should()
            .OnlyContain(threshold =>
                threshold.FailureAction.Contains("durable-baseline-cursor", StringComparison.Ordinal)
            );
    }

    [Test]
    public void It_keeps_representative_scale_heavier_than_ci_guards()
    {
        DocumentCacheQualification
            .RepresentativeDocumentCount.Should()
            .BeGreaterThan(DocumentCacheQualification.CiGuardDocumentCount * 1_000);
        DocumentCacheQualification
            .RepresentativeOutageDistinctDocumentWrites.Should()
            .BeGreaterThan(DocumentCacheQualification.CiGuardWorkRowCount * 100);
        DocumentCacheQualification.RepresentativeSameDocumentContention.Should().BeGreaterThan(1);
    }

    [Test]
    public void It_points_ci_guards_at_bounded_query_plan_tests()
    {
        DocumentCacheQualification
            .CiGuardCommands.Should()
            .OnlyContain(command => command.Contains("DocumentCacheQueryPlan", StringComparison.Ordinal));
        DocumentCacheQualification
            .CiGuardCommands.Should()
            .OnlyContain(command => !command.Contains("primary-500k", StringComparison.Ordinal));
    }

    [Test]
    public void It_names_the_required_representative_result_artifacts()
    {
        DocumentCacheQualification
            .RequiredRepresentativeArtifacts.Should()
            .Contain(
                "threshold-results.json",
                "provider-specific pass/fail evidence must be machine-readable"
            );
        DocumentCacheQualification
            .RequiredRepresentativeArtifacts.Should()
            .Contain("provider-metrics/operator-cpu-io.json");
        DocumentCacheQualification
            .RequiredRepresentativeArtifacts.Should()
            .Contain("provider-metrics/postgresql-wal-vacuum-bloat.md");
        DocumentCacheQualification
            .RequiredRepresentativeArtifacts.Should()
            .Contain("provider-metrics/mssql-log-ghost-index.md");
    }
}
