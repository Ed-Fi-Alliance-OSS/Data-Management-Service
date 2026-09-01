// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Measurement;
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
