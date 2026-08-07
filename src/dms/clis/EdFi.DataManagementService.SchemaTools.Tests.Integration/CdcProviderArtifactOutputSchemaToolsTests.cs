// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

[TestFixture]
public class Given_CdcProviderArtifactOutput
{
    private string _outputDir = null!;

    [SetUp]
    public void SetUp()
    {
        _outputDir = Path.Combine(
            Path.GetTempPath(),
            $"api-schema-tools-cdc-artifact-output-{Guid.NewGuid():N}"
        );
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_outputDir))
        {
            Directory.Delete(_outputDir, recursive: true);
        }
    }

    [Test]
    public async Task It_should_persist_the_standalone_manifest_when_internal_setup_requests_artifacts()
    {
        var service = new CdcProviderSetupService([new ArtifactOutputTestProvider()]);
        var request = BuildPostgresqlRequest(
            new CdcProviderArtifactOutputRequest(
                IncludeManifestPayload: true,
                ManifestOutputDirectoryPath: _outputDir
            )
        );

        var result = await service.SetupAsync(request);

        var manifestPath = Path.Combine(_outputDir, "cdc-provider.pgsql.manifest.json");
        File.Exists(manifestPath).Should().BeTrue();
        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        manifestJson.Should().Be(result.ManifestPayload!.Json);
        manifestJson.Should().Contain("\"observed_source_fingerprint\"");
        manifestJson.Should().NotContain(_outputDir);
    }

    [Test]
    public void It_should_not_emit_cdc_provider_artifacts_from_ordinary_ddl_emit()
    {
        var fixturePath = CliTestHelper.GetMinimalSchemaPath();

        var (exitCode, _, _) = CliTestHelper.RunCli(
            "ddl",
            "emit",
            "--schema",
            fixturePath,
            "--output",
            _outputDir,
            "--dialect",
            "both",
            "--ddl-manifest"
        );

        exitCode.Should().Be(0);
        File.Exists(Path.Combine(_outputDir, "cdc-provider.pgsql.manifest.json")).Should().BeFalse();
        File.Exists(Path.Combine(_outputDir, "cdc-provider.mssql.manifest.json")).Should().BeFalse();
    }

    private static CdcProviderSetupRequest BuildPostgresqlRequest(
        CdcProviderArtifactOutputRequest artifactOutput
    )
    {
        var emission = CdcSchemaToolsTestMetadata.BuildMinimalDdlEmission(SqlDialect.Pgsql);

        return new(
            provider: CdcProvider.Postgresql,
            mode: CdcProviderSetupMode.ValidateOnly,
            boundPhysicalSourceFingerprint: CdcSourceFingerprintMetadata.Compute(
                CdcProvider.Postgresql,
                "f81d4fae-7dec-11d0-a765-00a0c91e6bf6"
            ),
            setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("setup_principal")),
            connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName("connector_principal")),
            artifactNames: CdcProviderArtifactNames.ForPostgresql(
                new CdcSafeName("dms_binding_publication"),
                new CdcSafeName("dms_binding_slot")
            ),
            artifactOutput: artifactOutput,
            expectedSourceInventory: emission.CdcSourceInventory,
            dmsManagedTableInventory: emission.CdcDmsManagedTableInventory
        );
    }

    private sealed class ArtifactOutputTestProvider : ICdcProviderSetupProvider
    {
        public CdcProvider Provider => CdcProvider.Postgresql;

        public IReadOnlyList<CdcProviderSetupStep> BuildSetupSteps(CdcProviderSetupRequest request) =>
            [
                new CdcProviderSetupStep(
                    CdcProviderArtifactKind.PostgresqlPublication,
                    new CdcSafeName("dms_binding_publication"),
                    canCreateInInitialSetup: true,
                    (context, _) =>
                        Task.FromResult(
                            new CdcProviderSetupStepResult(
                                observedSourceFingerprint: context.Request.BoundPhysicalSourceFingerprint,
                                artifactInventory:
                                [
                                    new CdcProviderArtifactObservation(
                                        CdcProviderArtifactKind.HeartbeatTable,
                                        new CdcSafeName("dms.CdcHeartbeat"),
                                        CdcProviderArtifactState.Matched,
                                        new Dictionary<string, string> { ["singleton"] = "1" }
                                    ),
                                    new CdcProviderArtifactObservation(
                                        CdcProviderArtifactKind.PostgresqlPublication,
                                        new CdcSafeName("dms_binding_publication"),
                                        CdcProviderArtifactState.Matched,
                                        new Dictionary<string, string> { ["tables"] = "3" }
                                    ),
                                    new CdcProviderArtifactObservation(
                                        CdcProviderArtifactKind.PostgresqlReplicationSlot,
                                        new CdcSafeName("dms_binding_slot"),
                                        CdcProviderArtifactState.Matched,
                                        new Dictionary<string, string> { ["plugin"] = "pgoutput" }
                                    ),
                                ],
                                sourceTableInventory: context.Request.ExpectedSourceInventory,
                                expectedMessageKeyColumns:
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
                                heartbeatActionQuery: new CdcHeartbeatActionQuery(
                                    """UPDATE "dms"."CdcHeartbeat" SET "HeartbeatSequence" = "HeartbeatSequence" + 1 WHERE "HeartbeatId" = 1""",
                                    "hash-123"
                                )
                            )
                        )
                ),
            ];
    }
}
