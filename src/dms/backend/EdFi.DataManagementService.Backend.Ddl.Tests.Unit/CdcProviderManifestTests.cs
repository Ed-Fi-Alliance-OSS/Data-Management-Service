// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_CdcProviderManifest_Emitter
{
    [Test]
    public void It_should_emit_the_standalone_postgresql_manifest_file_name()
    {
        var payload = CdcProviderManifestEmitter.CreatePayload(BuildManifestResult());

        payload.FileName.Value.Should().Be("cdc-provider.pgsql.manifest.json");
    }

    [Test]
    public void It_should_emit_the_standalone_sql_server_manifest_file_name()
    {
        var result = BuildManifestResult() with { Provider = CdcProvider.SqlServer };

        var payload = CdcProviderManifestEmitter.CreatePayload(result);

        payload.FileName.Value.Should().Be("cdc-provider.mssql.manifest.json");
    }

    [Test]
    public void It_should_serialize_the_sql_server_manifest_provider_token()
    {
        var result = BuildManifestResult() with { Provider = CdcProvider.SqlServer };

        var payload = CdcProviderManifestEmitter.CreatePayload(result);

        using var document = JsonDocument.Parse(payload.Json);
        document.RootElement.GetProperty("provider").GetString().Should().Be("sqlserver");
    }

    [Test]
    public void It_should_serialize_observed_provider_metadata()
    {
        var payload = CdcProviderManifestEmitter.CreatePayload(BuildManifestResult());

        using var document = JsonDocument.Parse(payload.Json);
        var root = document.RootElement;

        root.GetProperty("manifest_version").GetString().Should().Be("1");
        root.GetProperty("provider").GetString().Should().Be("postgresql");
        root.GetProperty("mode").GetString().Should().Be("initial_create_or_exact_match");
        root.GetProperty("outcome").GetString().Should().Be("created_or_matched");
        root.GetProperty("opt_in_status").GetString().Should().Be("enabled");
        root.GetProperty("observed_source_fingerprint")
            .GetProperty("value")
            .GetString()
            .Should()
            .Be(CdcProviderSetupContractTestData.PostgresqlSourceFingerprint.Value);
        root.GetProperty("source_table_inventory").EnumerateArray().Should().HaveCount(3);
        root.GetProperty("provider_artifacts").EnumerateArray().Should().HaveCount(2);
        root.GetProperty("grant_inventory").EnumerateArray().Should().HaveCount(2);
        root.GetProperty("heartbeat_action_query")
            .GetProperty("sha256_hash")
            .GetString()
            .Should()
            .Be("hash-123");
        root.GetProperty("provider_history_observations").EnumerateArray().Should().HaveCount(1);
        root.GetProperty("validation_diagnostics").EnumerateArray().Should().HaveCount(1);
    }

    [Test]
    public void It_should_emit_provider_error_identity_only_when_present()
    {
        var payloadWithoutIdentity = CdcProviderManifestEmitter.CreatePayload(BuildManifestResult());
        var diagnosticWithIdentity = BuildManifestResult().Diagnostics.Single() with
        {
            ProviderErrorCode = "51000",
            ProviderErrorState = "7",
        };
        var payloadWithIdentity = CdcProviderManifestEmitter.CreatePayload(
            BuildManifestResult() with
            {
                Diagnostics = [diagnosticWithIdentity],
            }
        );

        payloadWithoutIdentity.Json.Should().NotContain("provider_error_code");
        payloadWithoutIdentity.Json.Should().NotContain("provider_error_state");

        using var document = JsonDocument.Parse(payloadWithIdentity.Json);
        var diagnostic = document.RootElement.GetProperty("validation_diagnostics").EnumerateArray().Single();

        diagnostic.GetProperty("provider_error_code").GetString().Should().Be("51000");
        diagnostic.GetProperty("provider_error_state").GetString().Should().Be("7");
    }

    [Test]
    public void It_should_use_deterministic_ordering()
    {
        var result = BuildManifestResult();
        var reversed = result with
        {
            ArtifactInventory = result.ArtifactInventory.Reverse().ToArray(),
            GrantInventory = result.GrantInventory.Reverse().ToArray(),
            SourceTableInventory = result.SourceTableInventory.Reverse().ToArray(),
            ProviderHistoryObservations = result.ProviderHistoryObservations.Reverse().ToArray(),
            Diagnostics = result.Diagnostics.Reverse().ToArray(),
        };

        var firstPayload = CdcProviderManifestEmitter.CreatePayload(result);
        var secondPayload = CdcProviderManifestEmitter.CreatePayload(reversed);

        secondPayload.Json.Should().Be(firstPayload.Json);
        firstPayload
            .Json.Should()
            .Contain(
                """
                      "artifact_kind": "postgresql_publication",
                      "artifact_name": "dms_binding_publication",
                """
            );
        firstPayload.Json.Should().Contain("\"plugin\": \"pgoutput\"");
        firstPayload.Json.Should().Contain("\"publish\": \"insert,update,delete\"");
        firstPayload.Json.Should().Contain("\"tables\": \"3\"");
    }

    [Test]
    public void It_should_exclude_ordinary_schema_fingerprints_and_bound_binding_fingerprint()
    {
        var result = BuildManifestResult() with
        {
            BoundPhysicalSourceFingerprint =
                CdcProviderSetupContractTestData.OtherPostgresqlSourceFingerprint,
        };

        var payload = CdcProviderManifestEmitter.CreatePayload(result);

        payload.Json.Should().Contain(CdcProviderSetupContractTestData.PostgresqlSourceFingerprint.Value);
        payload
            .Json.Should()
            .NotContain(CdcProviderSetupContractTestData.OtherPostgresqlSourceFingerprint.Value);
        payload.Json.Should().NotContain("effective_schema_hash");
        payload.Json.Should().NotContain("resource_key_seed_hash");
        payload.Json.Should().NotContain("relational_mapping_version");
        payload.Json.Should().NotContain("connection_string");
        payload.Json.Should().NotContain("password");
        payload.Json.Should().NotContain("document_payload");
    }

    private static CdcProviderSetupResult BuildManifestResult()
    {
        var sourceInventory = CdcProviderSetupContractTestData.BuildRequiredSourceInventory();

        return new CdcProviderSetupResult(
            Provider: CdcProvider.Postgresql,
            Mode: CdcProviderSetupMode.InitialCreateOrExactMatch,
            Outcome: CdcProviderSetupOutcome.CreatedOrMatched,
            BoundPhysicalSourceFingerprint: CdcProviderSetupContractTestData.PostgresqlSourceFingerprint,
            ObservedSourceFingerprint: CdcProviderSetupContractTestData.PostgresqlSourceFingerprint,
            ArtifactInventory:
            [
                new CdcProviderArtifactObservation(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    new CdcSafeName("dms_binding_slot"),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>
                    {
                        ["plugin"] = "pgoutput",
                        ["database_matches_current"] = "True",
                        ["database_identity_token"] = CdcPostgresqlInitialReplicationSlotProof
                            .CreateDatabaseIdentityToken("dms_test")
                            .Value,
                    }
                ),
                new CdcProviderArtifactObservation(
                    CdcProviderArtifactKind.PostgresqlPublication,
                    new CdcSafeName("dms_binding_publication"),
                    CdcProviderArtifactState.Created,
                    new Dictionary<string, string> { ["tables"] = "3", ["publish"] = "insert,update,delete" }
                ),
            ],
            GrantInventory:
            [
                new CdcGrantObservation(
                    CdcPrincipalKind.ConnectorPrincipal,
                    new CdcSafeName("connector_principal"),
                    CdcProviderArtifactKind.SourceTable,
                    new CdcSafeName("dms.CdcHeartbeat"),
                    ["UPDATE", "SELECT"],
                    [new DbColumnName("HeartbeatAt"), new DbColumnName("HeartbeatSequence")]
                ),
                new CdcGrantObservation(
                    CdcPrincipalKind.ConnectorPrincipal,
                    new CdcSafeName("connector_principal"),
                    CdcProviderArtifactKind.SourceTable,
                    new CdcSafeName("dms.Document"),
                    ["SELECT"],
                    []
                ),
            ],
            SourceTableInventory: sourceInventory,
            ExpectedMessageKeyColumns:
            [
                new CdcExpectedMessageKeyColumns(
                    CdcSourceTableKind.Document,
                    [new DbColumnName("DocumentUuid")]
                ),
                new CdcExpectedMessageKeyColumns(
                    CdcSourceTableKind.DocumentCache,
                    [new DbColumnName("DocumentUuid")]
                ),
            ],
            HeartbeatActionQuery: new CdcHeartbeatActionQuery(
                """UPDATE "dms"."CdcHeartbeat" SET "HeartbeatSequence" = "HeartbeatSequence" + 1 WHERE "HeartbeatId" = 1""",
                "hash-123"
            ),
            ProviderHistoryObservations:
            [
                new CdcProviderHistoryObservation(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    new CdcSafeName("dms_binding_slot"),
                    new Dictionary<string, string>
                    {
                        ["confirmed_flush_lsn"] = "0/16B6C50",
                        ["restart_lsn"] = "0/16B6C18",
                    },
                    CdcProviderRetryContinuityClassification.None
                ),
            ],
            ManifestPayload: null,
            Diagnostics:
            [
                new CdcProviderDiagnostic(
                    Code: "CDC_PROVIDER_HISTORY_UNAVAILABLE",
                    Category: CdcProviderDiagnosticCategory.ProviderHistoryUnavailable,
                    Severity: CdcProviderDiagnosticSeverity.Warning,
                    PrincipalKind: CdcPrincipalKind.SetupPrincipal,
                    ArtifactKind: CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    SafeName: new CdcSafeName("dms_binding_slot"),
                    ExpectedValue: "readable",
                    ObservedValue: "timeout",
                    ProviderErrorClass: "Timeout",
                    Classification: CdcProviderRetryContinuityClassification.SourceHistoryUnknown
                ),
            ]
        );
    }
}

[TestFixture]
public class Given_CdcProviderManifest_Setup_Service
{
    [Test]
    public async Task It_should_create_manifest_payload_when_artifact_output_requests_it()
    {
        var service = new CdcProviderSetupService([
            new TestProvider(CdcProvider.Postgresql, [BuildObservedProviderStateStep()]),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.ManifestPayload.Should().NotBeNull();
        result.ManifestPayload!.FileName.Value.Should().Be("cdc-provider.pgsql.manifest.json");
        result.ManifestPayload.Json.Should().Contain("\"observed_source_fingerprint\"");
    }

    [Test]
    public async Task It_should_not_create_manifest_payload_when_artifact_output_does_not_request_it()
    {
        var service = new CdcProviderSetupService([
            new TestProvider(CdcProvider.Postgresql, [BuildObservedProviderStateStep()]),
        ]);
        var request = CdcProviderSetupContractTestData.BuildPostgresqlRequest(
            artifactOutput: new CdcProviderArtifactOutputRequest(IncludeManifestPayload: false)
        );

        var result = await service.SetupAsync(request);

        result.ManifestPayload.Should().BeNull();
    }

    private static CdcProviderSetupStep BuildObservedProviderStateStep() =>
        new(
            CdcProviderArtifactKind.PostgresqlPublication,
            new CdcSafeName("dms_binding_publication"),
            canCreateInInitialSetup: true,
            (_, _) =>
                Task.FromResult(
                    new CdcProviderSetupStepResult(
                        observedSourceFingerprint: CdcProviderSetupContractTestData.PostgresqlSourceFingerprint,
                        artifactInventory:
                        [
                            new CdcProviderArtifactObservation(
                                CdcProviderArtifactKind.PostgresqlPublication,
                                new CdcSafeName("dms_binding_publication"),
                                CdcProviderArtifactState.Matched,
                                new Dictionary<string, string> { ["tables"] = "3" }
                            ),
                        ],
                        sourceTableInventory: CdcProviderSetupContractTestData.BuildRequiredSourceInventory()
                    )
                )
        );
}
