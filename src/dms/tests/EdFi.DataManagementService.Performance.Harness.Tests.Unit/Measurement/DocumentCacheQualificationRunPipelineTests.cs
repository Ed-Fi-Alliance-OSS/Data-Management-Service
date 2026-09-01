// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_DocumentCache_Representative_Fixture_Guards
{
    [Test]
    public void It_accepts_the_primary_500k_fixture()
    {
        FluentActions
            .Invoking(() =>
                DocumentCacheQualificationFixtureSetup.GuardRepresentativeFixture(PerfFixtureKind.Primary500k)
            )
            .Should()
            .NotThrow();
    }

    [Test]
    public void It_rejects_smaller_fixture_kinds()
    {
        FluentActions
            .Invoking(() =>
                DocumentCacheQualificationFixtureSetup.GuardRepresentativeFixture(PerfFixtureKind.Smoke10k)
            )
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*primary-500k*500,000*smoke-10k*10,000*");
    }
}

[TestFixture]
public class Given_DocumentCache_Representative_Evidence_Environment_Guards
{
    [Test]
    public void It_rejects_ci_even_when_general_evidence_settings_allow_ci()
    {
        DocumentCacheRepresentativeRunConfiguration configuration = Configuration(
            settings: EvidenceSettings(allowCi: true, storageNote: "local docker volume, not tmpfs")
        );

        FluentActions
            .Invoking(() =>
                DocumentCacheQualificationFixtureSetup.GuardRepresentativeEvidenceEnvironment(
                    configuration,
                    "true"
                )
            )
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*CI*tmpfs*");
    }

    [Test]
    public void It_rejects_tmpfs_storage_notes()
    {
        DocumentCacheRepresentativeRunConfiguration configuration = Configuration(
            settings: EvidenceSettings(allowCi: false, storageNote: "tmpfs-backed CI volume")
        );

        FluentActions
            .Invoking(() =>
                DocumentCacheQualificationFixtureSetup.GuardRepresentativeEvidenceEnvironment(
                    configuration,
                    null
                )
            )
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*tmpfs storage*");
    }

    [Test]
    public void It_accepts_a_non_tmpfs_storage_note_off_ci()
    {
        DocumentCacheRepresentativeRunConfiguration configuration = Configuration(
            settings: EvidenceSettings(allowCi: false, storageNote: "local docker volume, not tmpfs")
        );

        FluentActions
            .Invoking(() =>
                DocumentCacheQualificationFixtureSetup.GuardRepresentativeEvidenceEnvironment(
                    configuration,
                    null
                )
            )
            .Should()
            .NotThrow();
    }

    [Test]
    public void It_rejects_non_representative_fixtures_before_measurement()
    {
        DocumentCacheRepresentativeRunConfiguration configuration = Configuration(
            fixture: PerfFixtureKind.Smoke10k
        );

        FluentActions
            .Invoking(() =>
                DocumentCacheQualificationFixtureSetup.GuardRepresentativeEvidenceEnvironment(
                    configuration,
                    null
                )
            )
            .Should()
            .Throw<PerfObservationException>()
            .WithMessage("*primary-500k*");
    }

    private static DocumentCacheRepresentativeRunConfiguration Configuration(
        PerfFixtureKind? fixture = null,
        PerfEvidenceRunSettings? settings = null
    ) =>
        new(
            PerfProvider.Postgresql,
            Path.Combine(Path.GetTempPath(), "document-cache-results"),
            new string('a', 40),
            fixture ?? PerfFixtureKind.Primary500k,
            DocumentCacheRepresentativeRunConfigurationLoader.DefaultPageSize,
            (fixture ?? PerfFixtureKind.Primary500k).RowCount,
            DocumentCacheRepresentativeRunConfigurationLoader.DefaultProjectorConcurrency,
            DocumentCacheRepresentativeRunConfigurationLoader.DefaultWarmupStatusSamples,
            DocumentCacheRepresentativeRunConfigurationLoader.DefaultMeasuredStatusSamples,
            DocumentCacheQualification.RepresentativeOutageDistinctDocumentWrites,
            DocumentCacheQualification.RepresentativeSameDocumentContention,
            OperatorNote: null,
            settings ?? EvidenceSettings(allowCi: false, storageNote: "local docker volume, not tmpfs")
        );

    private static PerfEvidenceRunSettings EvidenceSettings(bool allowCi, string storageNote) =>
        new(
            "postgres:16.8-alpine",
            "sha256:951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0",
            storageNote,
            allowCi,
            [PerfEvidenceRunSettings.DefaultAllowedDirtyPrefix]
        );
}

[TestFixture]
public class Given_DocumentCache_Qualification_Runtime_Phase_Artifacts
{
    [Test]
    public void It_declares_a_metric_and_transcript_path_for_each_runtime_phase()
    {
        IReadOnlyList<string> metricPaths = DocumentCacheQualificationRunPipeline.PhaseMetricRelativePaths(
            "postgresql"
        );
        IReadOnlyList<string> transcriptPaths =
            DocumentCacheQualificationRunPipeline.CommandTranscriptRelativePaths("postgresql");

        metricPaths
            .Should()
            .Equal(
                "phase-metrics/postgresql-preflight-guards.json",
                "phase-metrics/postgresql-disabled-canonical-write-samples.json",
                "phase-metrics/postgresql-offline-activation-first-baseline.json",
                "phase-metrics/postgresql-tracking-canonical-write-overhead.json",
                "phase-metrics/postgresql-status-empty-work-latency.json",
                "phase-metrics/postgresql-online-rebuild-clear-reseed-drain.json",
                "phase-metrics/postgresql-interrupted-rebuild-restart-from-beginning.json",
                "phase-metrics/postgresql-outage-distinct-document-writes.json",
                "phase-metrics/postgresql-outage-work-row-growth.json",
                "phase-metrics/postgresql-status-large-work-inventory-latency.json",
                "phase-metrics/postgresql-outage-drain.json",
                "phase-metrics/postgresql-status-small-work-inventory-latency.json",
                "phase-metrics/postgresql-same-document-enqueue-ack-contention.json",
                "phase-metrics/postgresql-explicit-integrity-scrub.json",
                "phase-metrics/postgresql-post-run-final-counts.json"
            );
        transcriptPaths.Should().HaveSameCount(metricPaths);
        transcriptPaths.Should().OnlyHaveUniqueItems();
        transcriptPaths
            .Should()
            .Contain("command-transcripts/postgresql-offline-activation-first-baseline.md");
        transcriptPaths
            .Should()
            .Contain("command-transcripts/postgresql-interrupted-rebuild-restart-from-beginning.md");
        transcriptPaths
            .Should()
            .Contain("command-transcripts/postgresql-same-document-enqueue-ack-contention.md");
    }

    [Test]
    public void It_rejects_blank_provider_artifact_names()
    {
        FluentActions
            .Invoking(() => DocumentCacheQualificationRunPipeline.PhaseMetricRelativePaths(" "))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*Provider artifact name*");
    }
}

[TestFixture]
public class Given_DocumentCache_Qualification_Phase_Metrics
{
    [Test]
    public void It_serializes_as_camel_case_and_omits_absent_optional_sections()
    {
        DocumentCacheQualificationPhaseCounts counts = new(
            "postgresql",
            SourceDocumentRows: 500_000,
            DmsDocumentRows: 500_005,
            DocumentCacheRows: 500_005,
            DocumentProjectionWorkRows: 0,
            "Tracking",
            CacheAheadRecoveryRequired: false
        );
        DocumentCacheQualificationPhaseMetrics artifact = DocumentCacheQualificationPhaseMetrics.Create(
            "postgresql",
            "status-empty-work-latency",
            "2026-08-31T12:00:00Z",
            TimeSpan.FromMilliseconds(42.5),
            counts,
            counts,
            [new DocumentCacheQualificationPhaseMetricValue("queuePresence", "Empty", "value")],
            latency: new PerfLatencySummary(1, 2, 1.5, 1, 2, [1, 2])
        );

        string json = PerfArtifactJson.Serialize(artifact);

        json.Should().Contain("\"schemaVersion\"");
        json.Should().Contain("\"countsBefore\"");
        json.Should().Contain("\"projectionLifecycleState\": \"Tracking\"");
        json.Should().Contain("\"queuePresence\"");
        json.Should().Contain("\"latency\"");
        json.Should().NotContain("\"commandResult\"");
        json.Should().NotContain("\"statusSnapshot\"");
    }
}
