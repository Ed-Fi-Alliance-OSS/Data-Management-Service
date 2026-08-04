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
public class Given_CdcProviderArtifactOutput
{
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"dms-cdc-artifact-output-{Guid.NewGuid():N}");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }

        if (File.Exists(_tempRoot))
        {
            File.Delete(_tempRoot);
        }
    }

    [Test]
    public async Task It_should_write_the_standalone_manifest_to_the_requested_artifact_directory()
    {
        var service = new CdcProviderSetupService([
            new TestProvider(CdcProvider.Postgresql, [BuildCompleteObservedProviderStateStep()]),
        ]);
        var request = CdcProviderSetupContractTestData.BuildPostgresqlRequest(
            artifactOutput: new CdcProviderArtifactOutputRequest(
                IncludeManifestPayload: true,
                ManifestOutputDirectoryPath: _tempRoot
            )
        );

        var result = await service.SetupAsync(request);

        var manifestPath = Path.Combine(_tempRoot, "cdc-provider.pgsql.manifest.json");
        File.Exists(manifestPath).Should().BeTrue();
        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        manifestJson.Should().Be(result.ManifestPayload!.Json);
        result.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_overwrite_a_stale_manifest_file_with_the_current_observed_result()
    {
        Directory.CreateDirectory(_tempRoot);
        var manifestPath = Path.Combine(_tempRoot, "cdc-provider.pgsql.manifest.json");
        await File.WriteAllTextAsync(manifestPath, "stale-manifest");
        var service = new CdcProviderSetupService([
            new TestProvider(CdcProvider.Postgresql, [BuildCompleteObservedProviderStateStep()]),
        ]);
        var request = CdcProviderSetupContractTestData.BuildPostgresqlRequest(
            artifactOutput: new CdcProviderArtifactOutputRequest(
                IncludeManifestPayload: true,
                ManifestOutputDirectoryPath: _tempRoot
            )
        );

        var result = await service.SetupAsync(request);
        var manifestJson = await File.ReadAllTextAsync(manifestPath);

        manifestJson.Should().Be(result.ManifestPayload!.Json);
        manifestJson.Should().NotContain("stale-manifest");
    }

    [Test]
    public async Task It_should_report_output_failures_without_masking_provider_setup_mismatches()
    {
        await File.WriteAllTextAsync(_tempRoot, "not-a-directory");
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.PostgresqlPublication,
                        new CdcSafeName("dms_binding_publication"),
                        CdcProviderArtifactState.Mismatched,
                        canCreateInInitialSetup: true
                    ),
                ]
            ),
        ]);
        var request = CdcProviderSetupContractTestData.BuildPostgresqlRequest(
            artifactOutput: new CdcProviderArtifactOutputRequest(
                IncludeManifestPayload: true,
                ManifestOutputDirectoryPath: _tempRoot
            )
        );

        var result = await service.SetupAsync(request);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISMATCH");
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_PROVIDER_ARTIFACT_OUTPUT_FAILED")
            .Which.Should()
            .Match<CdcProviderDiagnostic>(diagnostic =>
                diagnostic.SafeName.Value == "cdc-provider.pgsql.manifest.json"
                && diagnostic.ObservedValue == nameof(IOException)
                && diagnostic.ProviderErrorClass == nameof(IOException)
            );
        result.ManifestPayload!.Json.Should().Contain("CDC_PROVIDER_ARTIFACT_OUTPUT_FAILED");
        using var document = JsonDocument.Parse(result.ManifestPayload.Json);
        document.RootElement.GetProperty("opt_in_status").GetString().Should().Be("validation_failed");
    }

    [Test]
    public async Task It_should_report_invalid_manifest_output_paths_as_artifact_output_diagnostics()
    {
        var invalidOutputPath = Path.Combine(_tempRoot, $"invalid{'\0'}path");
        var service = new CdcProviderSetupService([
            new TestProvider(CdcProvider.Postgresql, [BuildCompleteObservedProviderStateStep()]),
        ]);
        var request = CdcProviderSetupContractTestData.BuildPostgresqlRequest(
            artifactOutput: new CdcProviderArtifactOutputRequest(
                IncludeManifestPayload: true,
                ManifestOutputDirectoryPath: invalidOutputPath
            )
        );

        var result = await service.SetupAsync(request);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_PROVIDER_ARTIFACT_OUTPUT_FAILED")
            .Which.Should()
            .Match<CdcProviderDiagnostic>(diagnostic =>
                diagnostic.SafeName.Value == "cdc-provider.pgsql.manifest.json"
                && diagnostic.ObservedValue == nameof(ArgumentException)
                && diagnostic.ProviderErrorClass == nameof(ArgumentException)
            );
        result.ManifestPayload!.Json.Should().Contain("CDC_PROVIDER_ARTIFACT_OUTPUT_FAILED");
        result.ManifestPayload.Json.Should().NotContain(_tempRoot);
        result.ManifestPayload.Json.Should().NotContain("invalid");
        using var document = JsonDocument.Parse(result.ManifestPayload.Json);
        document.RootElement.GetProperty("outcome").GetString().Should().Be("failed");
        document.RootElement.GetProperty("opt_in_status").GetString().Should().Be("enabled");
        document
            .RootElement.GetProperty("validation_diagnostics")
            .EnumerateArray()
            .Should()
            .Contain(diagnostic =>
                diagnostic.GetProperty("code").GetString() == "CDC_PROVIDER_ARTIFACT_OUTPUT_FAILED"
            );
    }

    private static CdcProviderSetupStep BuildCompleteObservedProviderStateStep() =>
        new(
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
                        sourceTableInventory: CdcProviderSetupContractTestData.BuildRequiredSourceInventory(),
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
        );
}
