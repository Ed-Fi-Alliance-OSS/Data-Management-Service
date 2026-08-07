// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_CdcArtifactNames_Request
{
    [Test]
    public void It_should_consume_the_complete_postgresql_artifact_name_inventory()
    {
        var request = CdcProviderSetupContractTestData.BuildPostgresqlRequest(
            artifactNames: CdcDms1320ArtifactNameTestAdapter.ForPostgresql()
        );

        request.ArtifactNames.Postgresql.Should().NotBeNull();
        request.ArtifactNames.SqlServer.Should().BeNull();
        request.ArtifactNames.Postgresql!.PublicationName.Value.Should().Be("dms_binding_publication");
        request.ArtifactNames.Postgresql.ReplicationSlotName.Value.Should().Be("dms_binding_slot");
    }

    [Test]
    public void It_should_consume_the_complete_sqlserver_artifact_name_inventory()
    {
        var request = CdcProviderSetupContractTestData.BuildSqlServerRequest(
            artifactNames: CdcDms1320ArtifactNameTestAdapter.ForSqlServer()
        );

        request.ArtifactNames.SqlServer.Should().NotBeNull();
        request.ArtifactNames.Postgresql.Should().BeNull();
        request.ArtifactNames.SqlServer!.GatingRoleName.Value.Should().Be("dms_binding_gate");
        request
            .ArtifactNames.SqlServer.CaptureInstanceNames.Should()
            .BeEquivalentTo(
                new Dictionary<CdcSourceTableKind, CdcSafeName>
                {
                    [CdcSourceTableKind.Document] = new("dms_binding_document"),
                    [CdcSourceTableKind.DocumentCache] = new("dms_binding_document_cache"),
                    [CdcSourceTableKind.CdcHeartbeat] = new("dms_binding_cdc_heartbeat"),
                }
            );
    }

    [Test]
    public void It_should_keep_generation_scoped_test_adapter_names_distinct()
    {
        var generationOne = CdcDms1320ArtifactNameTestAdapter.ForSqlServer("binding_g001").SqlServer!;
        var generationTwo = CdcDms1320ArtifactNameTestAdapter.ForSqlServer("binding_g002").SqlServer!;

        generationOne.GatingRoleName.Should().NotBe(generationTwo.GatingRoleName);
        generationOne
            .CaptureInstanceNames.Select(pair => pair.Value.Value)
            .Should()
            .NotIntersectWith(generationTwo.CaptureInstanceNames.Select(pair => pair.Value.Value));
    }

    [Test]
    public void It_should_not_expose_artifact_name_derivation_inputs()
    {
        var propertyNames = typeof(CdcProviderSetupRequest)
            .GetProperties()
            .Concat(typeof(CdcProviderArtifactNames).GetProperties())
            .Concat(typeof(CdcPostgresqlProviderArtifactNames).GetProperties())
            .Concat(typeof(CdcSqlServerProviderArtifactNames).GetProperties())
            .Select(property => property.Name);

        propertyNames
            .Should()
            .NotContain([
                "TenantDisplayName",
                "RawConnectionString",
                "ConnectionString",
                "PhysicalServerName",
                "ServerName",
                "DatabaseName",
                "ConnectorJson",
            ]);
    }
}

[TestFixture]
public class Given_CdcArtifactNames_Provider_Limits
{
    [Test]
    public void It_should_reject_postgresql_artifact_names_over_the_provider_identifier_limit()
    {
        Action action = () =>
            CdcProviderArtifactNames.ForPostgresql(
                new CdcSafeName(new string('a', 64)),
                new CdcSafeName("dms_binding_slot")
            );

        action.Should().Throw<ArgumentException>().WithMessage("*63 UTF-8 bytes*");
    }

    [Test]
    public void It_should_reject_sqlserver_capture_instance_names_over_the_provider_limit()
    {
        Action action = () =>
            CdcProviderArtifactNames.ForSqlServer(
                new CdcSafeName("dms_binding_gate"),
                RequiredSqlServerCaptureNames(documentName: new string('a', 101))
            );

        action.Should().Throw<ArgumentException>().WithMessage("*100 characters*");
    }

    [Test]
    public void It_should_reject_sqlserver_gating_role_names_over_the_provider_limit()
    {
        Action action = () =>
            CdcProviderArtifactNames.ForSqlServer(
                new CdcSafeName(new string('a', 129)),
                RequiredSqlServerCaptureNames()
            );

        action.Should().Throw<ArgumentException>().WithMessage("*128 characters*");
    }

    [Test]
    public void It_should_reject_duplicate_sqlserver_capture_instance_names()
    {
        Action action = () =>
            CdcProviderArtifactNames.ForSqlServer(
                new CdcSafeName("dms_binding_gate"),
                RequiredSqlServerCaptureNames(
                    documentName: "dms_duplicate",
                    documentCacheName: "dms_duplicate"
                )
            );

        action.Should().Throw<ArgumentException>().WithMessage("*unique within the database*");
    }

    [Test]
    public void It_should_reject_artifact_names_for_more_than_the_selected_provider()
    {
        var mixedNames = new CdcProviderArtifactNames(
            CdcDms1320ArtifactNameTestAdapter.ForPostgresql().Postgresql,
            CdcDms1320ArtifactNameTestAdapter.ForSqlServer().SqlServer
        );

        Action action = () =>
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(artifactNames: mixedNames);

        action.Should().Throw<ArgumentException>().WithMessage("*only names for provider Postgresql*");
    }

    private static IReadOnlyDictionary<CdcSourceTableKind, CdcSafeName> RequiredSqlServerCaptureNames(
        string documentName = "dms_binding_document",
        string documentCacheName = "dms_binding_document_cache",
        string heartbeatName = "dms_binding_cdc_heartbeat"
    ) =>
        new Dictionary<CdcSourceTableKind, CdcSafeName>
        {
            [CdcSourceTableKind.Document] = new(documentName),
            [CdcSourceTableKind.DocumentCache] = new(documentCacheName),
            [CdcSourceTableKind.CdcHeartbeat] = new(heartbeatName),
        };
}

[TestFixture]
public class Given_CdcArtifactNames_Exact_Match_Validation
{
    [Test]
    public async Task It_should_fail_closed_when_sqlserver_capture_instance_name_is_not_binding_supplied()
    {
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.SqlServer,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.SourceFingerprint,
                        CdcSourceFingerprintMetadata.SafeArtifactName,
                        canCreateInInitialSetup: false,
                        observedSourceFingerprint: CdcProviderSetupContractTestData.SqlServerSourceFingerprint
                    ),
                    RecordingStep.Create(
                        CdcProviderArtifactKind.SqlServerCaptureInstance,
                        new CdcSafeName("derived_from_database_name")
                    ),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildSqlServerRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_BINDING_ARTIFACT_NAME_MISMATCH")
            .Which.Should()
            .Match<CdcProviderDiagnostic>(diagnostic =>
                diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && diagnostic.ExpectedValue!.Contains("dms_binding_document", StringComparison.Ordinal)
                && diagnostic.ObservedValue == "derived_from_database_name"
            );
    }

    [Test]
    public async Task It_should_fail_closed_when_sqlserver_gating_role_name_is_not_binding_supplied()
    {
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.SqlServer,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.SourceFingerprint,
                        CdcSourceFingerprintMetadata.SafeArtifactName,
                        canCreateInInitialSetup: false,
                        observedSourceFingerprint: CdcProviderSetupContractTestData.SqlServerSourceFingerprint,
                        grantInventory:
                        [
                            new CdcGrantObservation(
                                CdcPrincipalKind.ConnectorPrincipal,
                                new CdcSafeName("connector_principal"),
                                CdcProviderArtifactKind.SqlServerGatingRole,
                                new CdcSafeName("role.derived_from_database_name"),
                                ["MEMBER"],
                                []
                            ),
                        ]
                    ),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildSqlServerRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_BINDING_SQLSERVER_GATING_ROLE_NAME_MISMATCH")
            .Which.Should()
            .Match<CdcProviderDiagnostic>(diagnostic =>
                diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && diagnostic.ExpectedValue == "role.dms_binding_gate"
                && diagnostic.ObservedValue == "role.derived_from_database_name"
            );
    }
}
