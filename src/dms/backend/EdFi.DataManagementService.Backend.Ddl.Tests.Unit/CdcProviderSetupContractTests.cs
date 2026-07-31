// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

internal sealed class TestConnectorPrincipalProbeFactory : ICdcConnectorPrincipalProbeFactory;

internal static class CdcProviderSetupContractTestData
{
    internal static CdcProviderSetupRequest BuildPostgresqlRequest(
        IReadOnlyList<CdcSourceTableInventory>? sourceInventory = null,
        CdcProviderSetupMode mode = CdcProviderSetupMode.InitialCreateOrExactMatch,
        CdcProviderArtifactOutputRequest? artifactOutput = null
    ) =>
        new(
            provider: CdcProvider.Postgresql,
            mode: mode,
            boundPhysicalSourceFingerprint: new CdcSourceFingerprint(
                "dms-source-fingerprint-v1",
                "source-123"
            ),
            setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("setup_principal")),
            connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName("connector_principal")),
            artifactNames: CdcProviderArtifactNames.ForPostgresql(
                new CdcSafeName("dms_binding_publication"),
                new CdcSafeName("dms_binding_slot")
            ),
            artifactOutput: artifactOutput
                ?? new CdcProviderArtifactOutputRequest(IncludeManifestPayload: true),
            expectedSourceInventory: sourceInventory ?? BuildRequiredSourceInventory(),
            connectorPrincipalProbeFactory: new TestConnectorPrincipalProbeFactory()
        );

    internal static CdcProviderSetupResult BuildResult() =>
        new(
            Provider: CdcProvider.Postgresql,
            Mode: CdcProviderSetupMode.InitialCreateOrExactMatch,
            Outcome: CdcProviderSetupOutcome.CreatedOrMatched,
            BoundPhysicalSourceFingerprint: new CdcSourceFingerprint(
                "dms-source-fingerprint-v1",
                "source-123"
            ),
            ObservedSourceFingerprint: new CdcSourceFingerprint("dms-source-fingerprint-v1", "source-123"),
            ArtifactInventory:
            [
                new CdcProviderArtifactObservation(
                    CdcProviderArtifactKind.PostgresqlPublication,
                    new CdcSafeName("dms_binding_publication"),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string> { ["tables"] = "3" }
                ),
            ],
            GrantInventory:
            [
                new CdcGrantObservation(
                    CdcPrincipalKind.ConnectorPrincipal,
                    new CdcSafeName("connector_principal"),
                    CdcProviderArtifactKind.SourceTable,
                    new CdcSafeName("dms.Document"),
                    ["SELECT"],
                    []
                ),
            ],
            SourceTableInventory: BuildRequiredSourceInventory(),
            ExpectedMessageKeyColumns:
            [
                new CdcExpectedMessageKeyColumns(
                    CdcSourceTableKind.Document,
                    [new DbColumnName("DocumentUuid")]
                ),
            ],
            HeartbeatActionQuery: new CdcHeartbeatActionQuery(
                """UPDATE "dms"."CdcHeartbeat" SET "HeartbeatSequence" = "HeartbeatSequence" + 1 WHERE "HeartbeatId" = 1""",
                "7bda7f8a6f09c7b1e3a469f31eb1a05a05fb2be23e27a2f7ec330564a5d2e7c8"
            ),
            ProviderHistoryObservations:
            [
                new CdcProviderHistoryObservation(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    new CdcSafeName("dms_binding_slot"),
                    new Dictionary<string, string> { ["plugin"] = "pgoutput" },
                    CdcProviderRetryContinuityClassification.None
                ),
            ],
            ManifestPayload: new CdcProviderManifestPayload(
                new CdcSafeName("cdc-provider.pgsql.manifest.json"),
                """{"provider":"postgresql"}"""
            ),
            Diagnostics:
            [
                new CdcProviderDiagnostic(
                    Code: "CDC_SOURCE_TABLE_MISSING",
                    Category: CdcProviderDiagnosticCategory.MissingRequiredSourceObject,
                    Severity: CdcProviderDiagnosticSeverity.Error,
                    PrincipalKind: CdcPrincipalKind.None,
                    ArtifactKind: CdcProviderArtifactKind.SourceTable,
                    SafeName: new CdcSafeName("dms.CdcHeartbeat"),
                    ExpectedValue: "present",
                    ObservedValue: "missing",
                    ProviderErrorClass: "UndefinedTable",
                    Classification: CdcProviderRetryContinuityClassification.FailClosed
                ),
            ]
        );

    internal static IReadOnlyList<CdcSourceTableInventory> BuildRequiredSourceInventory() =>
        CdcSourceInventoryBuilder.BuildExpectedSourceInventory(SqlDialectFactory.Create(SqlDialect.Pgsql));
}

[TestFixture]
public class Given_CdcProviderSetupContract_Setup_Modes
{
    [Test]
    public void It_should_expose_initial_create_or_exact_match()
    {
        Enum.GetNames<CdcProviderSetupMode>()
            .Should()
            .Contain(nameof(CdcProviderSetupMode.InitialCreateOrExactMatch));
    }

    [Test]
    public void It_should_expose_validate_only()
    {
        Enum.GetNames<CdcProviderSetupMode>().Should().Contain(nameof(CdcProviderSetupMode.ValidateOnly));
    }
}

[TestFixture]
public class Given_CdcProviderSetupContract_Request
{
    [Test]
    public void It_should_require_caller_supplied_provider_artifact_names()
    {
        var request = CdcProviderSetupContractTestData.BuildPostgresqlRequest();

        request.ArtifactNames.Postgresql.Should().NotBeNull();
        request.ArtifactNames.Postgresql!.PublicationName.Value.Should().Be("dms_binding_publication");
        request.ArtifactNames.Postgresql.ReplicationSlotName.Value.Should().Be("dms_binding_slot");
    }

    [Test]
    public void It_should_require_the_three_fixed_emitted_source_tables_only()
    {
        var request = CdcProviderSetupContractTestData.BuildPostgresqlRequest();

        request
            .ExpectedSourceInventory.Select(table => table.TableKind)
            .Should()
            .BeEquivalentTo([
                CdcSourceTableKind.Document,
                CdcSourceTableKind.DocumentCache,
                CdcSourceTableKind.CdcHeartbeat,
            ]);
    }

    [Test]
    public void It_should_reject_missing_required_source_inventory()
    {
        var incompleteInventory = CdcProviderSetupContractTestData
            .BuildRequiredSourceInventory()
            .Where(table => table.TableKind != CdcSourceTableKind.CdcHeartbeat)
            .ToArray();

        Action action = () => CdcProviderSetupContractTestData.BuildPostgresqlRequest(incompleteInventory);

        action
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*dms.Document, dms.DocumentCache, and dms.CdcHeartbeat*");
    }

    [Test]
    public void It_should_reject_columns_that_are_not_in_table_ordinal_order()
    {
        Action action = () =>
            new CdcSourceTableInventory(
                CdcSourceTableKind.Document,
                new DbTableName(new DbSchemaName("dms"), "Document"),
                @"""dms"".""Document""",
                [
                    new CdcSourceColumnInventory(
                        new DbColumnName("DocumentUuid"),
                        @"""DocumentUuid""",
                        2,
                        "uuid",
                        IsNullable: false
                    ),
                    new CdcSourceColumnInventory(
                        new DbColumnName("DocumentId"),
                        @"""DocumentId""",
                        1,
                        "bigint",
                        IsNullable: false
                    ),
                ]
            );

        action.Should().Throw<ArgumentException>().WithMessage("*table-ordinal order*");
    }

    [Test]
    public void It_should_not_accept_a_free_form_heartbeat_action_query()
    {
        typeof(CdcProviderSetupRequest)
            .GetProperties()
            .Select(property => property.Name)
            .Should()
            .NotContain("HeartbeatActionQuery");
    }
}

[TestFixture]
public class Given_CdcProviderSetupContract_Result
{
    [Test]
    public void It_should_expose_stable_result_fields()
    {
        typeof(CdcProviderSetupResult)
            .GetProperties()
            .Select(property => property.Name)
            .Should()
            .Contain([
                nameof(CdcProviderSetupResult.Provider),
                nameof(CdcProviderSetupResult.Mode),
                nameof(CdcProviderSetupResult.Outcome),
                nameof(CdcProviderSetupResult.BoundPhysicalSourceFingerprint),
                nameof(CdcProviderSetupResult.ObservedSourceFingerprint),
                nameof(CdcProviderSetupResult.ArtifactInventory),
                nameof(CdcProviderSetupResult.GrantInventory),
                nameof(CdcProviderSetupResult.SourceTableInventory),
                nameof(CdcProviderSetupResult.ExpectedMessageKeyColumns),
                nameof(CdcProviderSetupResult.HeartbeatActionQuery),
                nameof(CdcProviderSetupResult.ProviderHistoryObservations),
                nameof(CdcProviderSetupResult.ManifestPayload),
                nameof(CdcProviderSetupResult.Diagnostics),
            ]);
    }

    [Test]
    public void It_should_return_heartbeat_action_query_only_as_provider_metadata()
    {
        var result = CdcProviderSetupContractTestData.BuildResult();

        result.HeartbeatActionQuery.Should().NotBeNull();
        result.HeartbeatActionQuery!.Sql.Should().Contain(@"""dms"".""CdcHeartbeat""");
    }
}

[TestFixture]
public class Given_CdcProviderSetupContract_Diagnostics
{
    [Test]
    public void It_should_expose_required_diagnostic_categories()
    {
        Enum.GetNames<CdcProviderDiagnosticCategory>()
            .Should()
            .Contain([
                nameof(CdcProviderDiagnosticCategory.SetupPrincipalFailure),
                nameof(CdcProviderDiagnosticCategory.ConnectorPrincipalPrivilegeFailure),
                nameof(CdcProviderDiagnosticCategory.MissingRequiredSourceObject),
                nameof(CdcProviderDiagnosticCategory.WorkTableCaptureViolation),
                nameof(CdcProviderDiagnosticCategory.WorkTableGrantViolation),
                nameof(CdcProviderDiagnosticCategory.ProviderHistoryUnavailable),
                nameof(CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence),
            ]);
    }

    [Test]
    public void It_should_expose_stable_diagnostic_fields()
    {
        typeof(CdcProviderDiagnostic)
            .GetProperties()
            .Select(property => property.Name)
            .Should()
            .Contain([
                nameof(CdcProviderDiagnostic.Code),
                nameof(CdcProviderDiagnostic.Category),
                nameof(CdcProviderDiagnostic.Severity),
                nameof(CdcProviderDiagnostic.PrincipalKind),
                nameof(CdcProviderDiagnostic.ArtifactKind),
                nameof(CdcProviderDiagnostic.SafeName),
                nameof(CdcProviderDiagnostic.ExpectedValue),
                nameof(CdcProviderDiagnostic.ObservedValue),
                nameof(CdcProviderDiagnostic.ProviderErrorClass),
                nameof(CdcProviderDiagnostic.Classification),
            ]);
    }
}

[TestFixture]
public class Given_CdcProviderSetupContract_Serialization
{
    [Test]
    public void It_should_not_serialize_probe_factories_or_secret_shaped_fields()
    {
        var request = CdcProviderSetupContractTestData.BuildPostgresqlRequest();
        var json = JsonSerializer.Serialize(request);

        json.Should().NotContain(nameof(CdcProviderSetupRequest.ConnectorPrincipalProbeFactory));
        json.Should().NotContain("Credential");
        json.Should().NotContain("ConnectionString");
        json.Should().NotContain("Password");
        json.Should().NotContain("Secret");
        json.Should().NotContain("Tenant");
        json.Should().NotContain("DisplayName");
        json.Should().NotContain("ServerName");
        json.Should().NotContain("DatabaseName");
        json.Should().NotContain("ConnectorJson");
    }
}
