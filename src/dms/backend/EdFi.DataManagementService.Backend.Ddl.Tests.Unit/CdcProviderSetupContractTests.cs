// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

internal sealed class TestConnectorPrincipalProbeFactory : ICdcConnectorPrincipalProbeFactory
{
    public Task<CdcConnectorPrincipalProbeResult> ProbeAsync(
        CdcProviderSetupRequest request,
        CancellationToken cancellationToken
    ) => Task.FromResult(new CdcConnectorPrincipalProbeResult());
}

internal static class CdcProviderSetupContractTestData
{
    internal const string SourceIdentity = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";
    internal const string OtherSourceIdentity = "11111111-1111-1111-1111-111111111111";

    internal static CdcSourceFingerprint PostgresqlSourceFingerprint =>
        CdcSourceFingerprintMetadata.Compute(CdcProvider.Postgresql, SourceIdentity);

    internal static CdcSourceFingerprint OtherPostgresqlSourceFingerprint =>
        CdcSourceFingerprintMetadata.Compute(CdcProvider.Postgresql, OtherSourceIdentity);

    internal static CdcSourceFingerprint SqlServerSourceFingerprint =>
        CdcSourceFingerprintMetadata.Compute(CdcProvider.SqlServer, SourceIdentity);

    internal static CdcSourceFingerprint OtherSqlServerSourceFingerprint =>
        CdcSourceFingerprintMetadata.Compute(CdcProvider.SqlServer, OtherSourceIdentity);

    internal static CdcProviderSetupRequest BuildPostgresqlRequest(
        IReadOnlyList<CdcSourceTableInventory>? sourceInventory = null,
        CdcProviderSetupMode mode = CdcProviderSetupMode.InitialCreateOrExactMatch,
        CdcProviderArtifactOutputRequest? artifactOutput = null,
        CdcProviderArtifactNames? artifactNames = null,
        ICdcProviderDatabaseExecutor? databaseExecutor = null,
        CdcPostgresqlInitialReplicationSlotProof? postgresqlInitialReplicationSlotProof = null,
        ICdcConnectorPrincipalProbeFactory? connectorPrincipalProbeFactory = null
    ) =>
        new(
            provider: CdcProvider.Postgresql,
            mode: mode,
            boundPhysicalSourceFingerprint: PostgresqlSourceFingerprint,
            setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("setup_principal")),
            connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName("connector_principal")),
            artifactNames: artifactNames ?? CdcDms1320ArtifactNameTestAdapter.ForPostgresql(),
            artifactOutput: artifactOutput
                ?? new CdcProviderArtifactOutputRequest(IncludeManifestPayload: true),
            expectedSourceInventory: sourceInventory ?? BuildRequiredSourceInventory(),
            postgresqlInitialReplicationSlotProof: postgresqlInitialReplicationSlotProof,
            connectorPrincipalProbeFactory: connectorPrincipalProbeFactory
                ?? new TestConnectorPrincipalProbeFactory(),
            databaseExecutor: databaseExecutor
        );

    internal static CdcProviderSetupRequest BuildSqlServerRequest(
        IReadOnlyList<CdcSourceTableInventory>? sourceInventory = null,
        CdcProviderSetupMode mode = CdcProviderSetupMode.InitialCreateOrExactMatch,
        CdcProviderArtifactOutputRequest? artifactOutput = null,
        CdcProviderArtifactNames? artifactNames = null,
        ICdcProviderDatabaseExecutor? databaseExecutor = null,
        ICdcConnectorPrincipalProbeFactory? connectorPrincipalProbeFactory = null
    ) =>
        new(
            provider: CdcProvider.SqlServer,
            mode: mode,
            boundPhysicalSourceFingerprint: SqlServerSourceFingerprint,
            setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("setup_principal")),
            connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName("connector_principal")),
            artifactNames: artifactNames ?? CdcDms1320ArtifactNameTestAdapter.ForSqlServer(),
            artifactOutput: artifactOutput
                ?? new CdcProviderArtifactOutputRequest(IncludeManifestPayload: true),
            expectedSourceInventory: sourceInventory ?? BuildSqlServerRequiredSourceInventory(),
            connectorPrincipalProbeFactory: connectorPrincipalProbeFactory
                ?? new TestConnectorPrincipalProbeFactory(),
            databaseExecutor: databaseExecutor
        );

    internal static CdcProviderSetupResult BuildResult() =>
        new(
            Provider: CdcProvider.Postgresql,
            Mode: CdcProviderSetupMode.InitialCreateOrExactMatch,
            Outcome: CdcProviderSetupOutcome.CreatedOrMatched,
            BoundPhysicalSourceFingerprint: PostgresqlSourceFingerprint,
            ObservedSourceFingerprint: PostgresqlSourceFingerprint,
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

    internal static IReadOnlyList<CdcSourceTableInventory> BuildSqlServerRequiredSourceInventory() =>
        CdcSourceInventoryBuilder.BuildExpectedSourceInventory(SqlDialectFactory.Create(SqlDialect.Mssql));

    internal static CdcPostgresqlInitialReplicationSlotProof BuildPostgresqlInitialSlotProof(
        string replicationSlotName = "dms_binding_slot",
        CdcSourceFingerprint? sourceFingerprint = null,
        string? databaseIdentityToken = null,
        string retainedRestartLsn = "0_16B6C50",
        string retainedConfirmedFlushLsn = "0_16B6C50"
    ) =>
        new(
            new CdcSafeName(replicationSlotName),
            sourceFingerprint ?? PostgresqlSourceFingerprint,
            new CdcSafeName(
                databaseIdentityToken
                    ?? CdcPostgresqlInitialReplicationSlotProof.CreateDatabaseIdentityToken("dms_test").Value
            ),
            retainedRestartLsn,
            retainedConfirmedFlushLsn
        );
}

internal static class CdcDms1320ArtifactNameTestAdapter
{
    // Temporary DMS-1320 test adapter until the DMS-1319/19-00 shared deterministic helper lands.
    internal static CdcProviderArtifactNames ForPostgresql(string generation = "binding") =>
        CdcProviderArtifactNames.ForPostgresql(
            new CdcSafeName($"dms_{generation}_publication"),
            new CdcSafeName($"dms_{generation}_slot")
        );

    internal static CdcProviderArtifactNames ForSqlServer(string generation = "binding") =>
        CdcProviderArtifactNames.ForSqlServer(
            new CdcSafeName($"dms_{generation}_gate"),
            new Dictionary<CdcSourceTableKind, CdcSafeName>
            {
                [CdcSourceTableKind.Document] = new($"dms_{generation}_document"),
                [CdcSourceTableKind.DocumentCache] = new($"dms_{generation}_document_cache"),
                [CdcSourceTableKind.CdcHeartbeat] = new($"dms_{generation}_cdc_heartbeat"),
            }
        );
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
    public void It_should_expose_metadata_safe_postgresql_initial_slot_proof()
    {
        var proof = CdcProviderSetupContractTestData.BuildPostgresqlInitialSlotProof();

        var request = CdcProviderSetupContractTestData.BuildPostgresqlRequest(
            postgresqlInitialReplicationSlotProof: proof
        );

        request.PostgresqlInitialReplicationSlotProof.Should().Be(proof);
        proof.DatabaseIdentityToken.Value.Should().StartWith("postgresql_database_identity_sha256:");
        proof.DatabaseIdentityToken.Value.Should().NotContain("dms_test");
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
        var misorderedInventory = CdcProviderSetupContractTestData
            .BuildRequiredSourceInventory()
            .Select(table =>
                table.TableKind == CdcSourceTableKind.Document
                    ? new CdcSourceTableInventory(
                        table.TableKind,
                        table.TableName,
                        table.EmittedQuotedTableName,
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
                    )
                    : table
            )
            .ToArray();

        Action action = () =>
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(sourceInventory: misorderedInventory);

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
        var request = CdcProviderSetupContractTestData.BuildPostgresqlRequest(
            postgresqlInitialReplicationSlotProof: CdcProviderSetupContractTestData.BuildPostgresqlInitialSlotProof()
        );
        var json = JsonSerializer.Serialize(request);

        json.Should().NotContain(nameof(CdcProviderSetupRequest.ConnectorPrincipalProbeFactory));
        json.Should().NotContain(nameof(CdcProviderSetupRequest.DatabaseExecutor));
        json.Should().Contain(nameof(CdcProviderSetupRequest.PostgresqlInitialReplicationSlotProof));
        json.Should().Contain("dms_binding_slot");
        json.Should().Contain("0_16B6C50");
        json.Should().Contain("postgresql_database_identity_sha256");
        json.Should().NotContain("dms_test");
        json.Should().NotContain("Credential");
        json.Should().NotContain("ConnectionString");
        json.Should().NotContain("Password");
        json.Should().NotContain("Secret");
        json.Should().NotContain("Tenant");
        json.Should().NotContain("DisplayName");
        json.Should().NotContain("ServerName");
        json.Should().NotContain("DatabaseName");
        json.Should().NotContain("ConnectorJson");
        json.Should().NotContain(CdcProviderSetupContractTestData.SourceIdentity);
    }
}
