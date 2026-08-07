// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_CdcSourceFingerprint_Algorithm
{
    [Test]
    public void It_should_compute_the_postgresql_conformance_vector()
    {
        var fingerprint = CdcSourceFingerprintMetadata.Compute(
            CdcProvider.Postgresql,
            "f81d4fae-7dec-11d0-a765-00a0c91e6bf6"
        );

        fingerprint
            .Should()
            .Be(
                new CdcSourceFingerprint(
                    "dms-source-fingerprint-v1",
                    "sha256:193c47b34d9751c73d06dbf5ccf2655a1cce46154a4808f152d3db0e91b676bc"
                )
            );
    }

    [Test]
    public void It_should_compute_the_sqlserver_conformance_vector()
    {
        var fingerprint = CdcSourceFingerprintMetadata.Compute(
            CdcProvider.SqlServer,
            "f81d4fae-7dec-11d0-a765-00a0c91e6bf6"
        );

        fingerprint
            .Should()
            .Be(
                new CdcSourceFingerprint(
                    "dms-source-fingerprint-v1",
                    "sha256:1780ea8893149195e89a46c70698dfdf64e8e6f9b31c7b7e9a9872baff498d75"
                )
            );
    }

    [Test]
    public void It_should_normalize_source_identity_before_hashing()
    {
        var fingerprint = CdcSourceFingerprintMetadata.Compute(
            CdcProvider.Postgresql,
            "F81D4FAE-7DEC-11D0-A765-00A0C91E6BF6"
        );

        fingerprint.Should().Be(CdcProviderSetupContractTestData.PostgresqlSourceFingerprint);
    }

    [TestCase("")]
    [TestCase("not-a-uuid")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    public void It_should_reject_invalid_source_identities(string sourceIdentity)
    {
        Action action = () => CdcSourceFingerprintMetadata.Compute(CdcProvider.Postgresql, sourceIdentity);

        action.Should().Throw<ArgumentException>().WithMessage("*non-zero UUID*");
    }
}

[TestFixture]
public class Given_CdcSourceFingerprint_Metadata
{
    [TestCase("", "blank_source_identity")]
    [TestCase("not-a-uuid", "malformed_source_identity")]
    [TestCase("00000000-0000-0000-0000-000000000000", "zero_source_identity")]
    public async Task It_should_fail_closed_without_exposing_invalid_source_identity(
        string sourceIdentity,
        string expectedStatus
    )
    {
        var executor = new RecordingPostgresqlCdcExecutor(sourceIdentity: sourceIdentity);
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(databaseExecutor: executor)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result.ObservedSourceFingerprint.Should().BeNull();
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SourceFingerprint
                && observation.State == CdcProviderArtifactState.Mismatched
                && observation.SafeObservedValues["source_identity_status"] == expectedStatus
                && !observation.SafeObservedValues.ContainsKey("source_identity")
            );
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SOURCE_FINGERPRINT_INVALID"
                && diagnostic.ObservedValue == expectedStatus
            );

        if (!string.IsNullOrWhiteSpace(sourceIdentity))
        {
            result.ManifestPayload!.Json.Should().NotContain(sourceIdentity);
        }
    }
}
