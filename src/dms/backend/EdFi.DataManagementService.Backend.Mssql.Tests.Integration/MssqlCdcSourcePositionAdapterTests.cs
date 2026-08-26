// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;
using DdlCdcProvider = EdFi.DataManagementService.Backend.Ddl.CdcProvider;
using DdlCdcProviderSetupMode = EdFi.DataManagementService.Backend.Ddl.CdcProviderSetupMode;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
[Category("CdcProviderPosition")]
[Category("CdcSourceHistory")]
public class Given_MssqlCdcSourcePositionAdapter
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";
    private const string ConnectorPassword = "EdFi_Dms1!";
    private const string InstanceKey = "mssql-instance";
    private const string OtherSourceFingerprint =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly DateTimeOffset ProjectionCaughtUpObservedAt = new(
        2026,
        8,
        18,
        12,
        0,
        0,
        TimeSpan.Zero
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private MutableTimeProvider _timeProvider = null!;
    private MssqlCdcSourcePositionAdapter _adapter = null!;
    private string _connectorPrincipalName = null!;

    [SetUp]
    public async Task SetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server CDC source-position tests require a MssqlAdmin connection string."
        );

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _timeProvider = new MutableTimeProvider(ProjectionCaughtUpObservedAt.AddSeconds(1));
        _adapter = new MssqlCdcSourcePositionAdapter(
            new MssqlDocumentCacheProviderCommandTimeoutClassifier(),
            _timeProvider,
            NullLogger<MssqlCdcSourcePositionAdapter>.Instance
        );
        _connectorPrincipalName = $"cdc_connector_{Guid.NewGuid():N}";

        CreateConnectorLoginAndUser(_database.DatabaseName, _connectorPrincipalName);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        if (!string.IsNullOrWhiteSpace(_connectorPrincipalName))
        {
            DropConnectorLoginIfExists(_connectorPrincipalName);
        }
    }

    [Test]
    public async Task It_captures_heartbeat_after_image_and_returns_a_reached_barrier_observation()
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();
        CdcProviderSetupResult setup = await CreateProviderArtifactsAsync(binding, inventory);

        CoreCdc.CdcProviderBarrierCaptureResult capture = await CaptureBarrierWithHeartbeatAsync(
            binding,
            setup.HeartbeatActionQuery!.Sql
        );

        _timeProvider.Set(capture.BarrierCapturedAt.AddSeconds(1));
        CoreCdc.CdcConnectorOffsetObservation connectorOffset = BuildConnectorOffset(
            binding,
            inventory,
            capture.SqlServerCommitLsn!,
            capture.SqlServerChangeLsn!,
            capture.SqlServerEventSerialNo!.Value
        );

        _timeProvider.Set(capture.BarrierCapturedAt.AddSeconds(2));
        CoreCdc.CdcProviderBarrierObservation observation = _adapter.ObserveProviderBarrier(
            new(
                OperationId(),
                binding,
                ProjectionCaughtUpObservedAt,
                capture,
                connectorOffset,
                ExpectedSourcePartitionHash(inventory)
            )
        );

        capture.Succeeded.Should().BeTrue();
        observation.BarrierState.Should().Be(CoreCdc.CdcProviderBarrierState.Reached);
        observation.SqlServerCommitLsn.Should().Be(capture.SqlServerCommitLsn);
        observation.SqlServerChangeLsn.Should().Be(capture.SqlServerChangeLsn);
        observation.SqlServerEventSerialNo.Should().Be(2);
        observation
            .CommittedPosition.Should()
            .Be($"{capture.SqlServerCommitLsn}/{capture.SqlServerChangeLsn}/2");
        observation.PostgresqlBarrierLsn.Should().BeNull();
        observation.Diagnostics.Should().BeEmpty();
        ValidateBarrierObservation(observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task It_reports_not_reached_when_connector_event_serial_is_before_the_heartbeat_after_image()
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();
        CdcProviderSetupResult setup = await CreateProviderArtifactsAsync(binding, inventory);

        CoreCdc.CdcProviderBarrierCaptureResult capture = await CaptureBarrierWithHeartbeatAsync(
            binding,
            setup.HeartbeatActionQuery!.Sql
        );

        _timeProvider.Set(capture.BarrierCapturedAt.AddSeconds(1));
        CoreCdc.CdcConnectorOffsetObservation connectorOffset = BuildConnectorOffset(
            binding,
            inventory,
            capture.SqlServerCommitLsn!,
            capture.SqlServerChangeLsn!,
            1
        );

        _timeProvider.Set(capture.BarrierCapturedAt.AddSeconds(2));
        CoreCdc.CdcProviderBarrierObservation observation = _adapter.ObserveProviderBarrier(
            new(
                OperationId(),
                binding,
                ProjectionCaughtUpObservedAt,
                capture,
                connectorOffset,
                ExpectedSourcePartitionHash(inventory)
            )
        );

        observation.BarrierState.Should().Be(CoreCdc.CdcProviderBarrierState.NotReached);
        observation.CommittedPosition.Should().BeNull();
        observation
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CoreCdc.CdcDiagnosticCategory.InvalidOrdering);
        ValidateBarrierObservation(observation, binding).Succeeded.Should().BeTrue();
    }

    [TestCase(InvalidBarrierInput.OperationId, CoreCdc.CdcDiagnosticCategory.OperationMismatch)]
    [TestCase(InvalidBarrierInput.SourceFingerprint, CoreCdc.CdcDiagnosticCategory.SourceMismatch)]
    [TestCase(InvalidBarrierInput.ConnectorName, CoreCdc.CdcDiagnosticCategory.ArtifactNameMismatch)]
    [TestCase(InvalidBarrierInput.TopicPrefix, CoreCdc.CdcDiagnosticCategory.ArtifactNameMismatch)]
    [TestCase(InvalidBarrierInput.CaptureProvider, CoreCdc.CdcDiagnosticCategory.ProviderMismatch)]
    [TestCase(InvalidBarrierInput.SourcePartitionHash, CoreCdc.CdcDiagnosticCategory.SourceMismatch)]
    public async Task It_returns_unknown_barrier_when_comparison_evidence_is_invalid(
        InvalidBarrierInput invalidInput,
        CoreCdc.CdcDiagnosticCategory expectedDiagnostic
    )
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();
        CoreCdc.CdcProviderBarrierCaptureResult capture =
            CoreCdc.CdcProviderBarrierCaptureResult.SqlServerSuccess(
                "00000000:016b6c50:0001",
                "00000000:016b6c50:0002",
                ProjectionCaughtUpObservedAt.AddSeconds(1)
            );

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(2));
        CoreCdc.CdcConnectorOffsetObservation connectorOffset = BuildConnectorOffset(
            binding,
            inventory,
            capture.SqlServerCommitLsn!,
            capture.SqlServerChangeLsn!,
            capture.SqlServerEventSerialNo!.Value
        );
        string operationId = OperationId();
        string expectedSourcePartitionHash = ExpectedSourcePartitionHash(inventory);

        switch (invalidInput)
        {
            case InvalidBarrierInput.OperationId:
                operationId = "other-operation";
                break;
            case InvalidBarrierInput.SourceFingerprint:
                connectorOffset = connectorOffset with { PhysicalSourceFingerprint = OtherSourceFingerprint };
                break;
            case InvalidBarrierInput.ConnectorName:
                connectorOffset = connectorOffset with { ConnectorName = "edfi.dms.other.connector" };
                break;
            case InvalidBarrierInput.TopicPrefix:
                connectorOffset = connectorOffset with { TopicPrefix = "edfi.dms.other" };
                break;
            case InvalidBarrierInput.CaptureProvider:
                capture = CoreCdc.CdcProviderBarrierCaptureResult.PostgresqlSuccess(
                    "0/16B6C50",
                    ProjectionCaughtUpObservedAt.AddSeconds(1)
                );
                break;
            case InvalidBarrierInput.SourcePartitionHash:
                connectorOffset = connectorOffset with
                {
                    ConnectSourcePartitionHash = OtherSourceFingerprint,
                };
                break;
        }

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(3));
        CoreCdc.CdcProviderBarrierObservation observation = _adapter.ObserveProviderBarrier(
            new(
                operationId,
                binding,
                ProjectionCaughtUpObservedAt,
                capture,
                connectorOffset,
                expectedSourcePartitionHash
            )
        );

        observation.BarrierState.Should().Be(CoreCdc.CdcProviderBarrierState.Unknown);
        observation.CommittedPosition.Should().BeNull();
        observation.Diagnostics.Should().Contain(diagnostic => diagnostic.Category == expectedDiagnostic);
    }

    [Test]
    public async Task It_returns_unknown_barrier_when_expected_source_partition_hash_is_unavailable()
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();
        CoreCdc.CdcProviderBarrierCaptureResult capture =
            CoreCdc.CdcProviderBarrierCaptureResult.SqlServerSuccess(
                "00000000:016b6c50:0001",
                "00000000:016b6c50:0002",
                ProjectionCaughtUpObservedAt.AddSeconds(1)
            );

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(2));
        CoreCdc.CdcConnectorOffsetObservation connectorOffset = BuildConnectorOffset(
            binding,
            inventory,
            capture.SqlServerCommitLsn!,
            capture.SqlServerChangeLsn!,
            capture.SqlServerEventSerialNo!.Value
        );

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(3));
        CoreCdc.CdcProviderBarrierObservation observation = _adapter.ObserveProviderBarrier(
            new(OperationId(), binding, ProjectionCaughtUpObservedAt, capture, connectorOffset)
        );

        observation.BarrierState.Should().Be(CoreCdc.CdcProviderBarrierState.Unknown);
        observation.CommittedPosition.Should().BeNull();
        observation
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CoreCdc.CdcDiagnosticCategory.LocalStateUnavailable
                && diagnostic.Path == "$.expectedConnectSourcePartitionHash"
            );
    }

    [Test]
    public async Task It_provisions_capture_columns_from_the_shared_sql_server_source_inventory()
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();
        await CreateProviderArtifactsAsync(binding, inventory);

        IReadOnlyDictionary<string, string[]> columnsByCapture = await ReadCapturedColumnNamesAsync(
            inventory
        );

        foreach (CdcSourceTableInventory sourceTable in _fixture.CdcSourceInventory)
        {
            string captureName = CaptureNameForSourceTable(inventory, sourceTable.TableKind);
            columnsByCapture.Should().ContainKey(captureName);
            columnsByCapture[captureName]
                .Should()
                .Equal(
                    sourceTable
                        .Columns.OrderBy(column => column.Ordinal)
                        .Select(column => column.ColumnName.Value)
                );
        }
    }

    [Test]
    public async Task It_reads_capture_job_and_retained_lsn_metadata_for_healthy_continuity()
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();
        CdcProviderSetupResult setup = await CreateProviderArtifactsAsync(binding, inventory);
        CoreCdc.CdcProviderBarrierCaptureResult capture = await CaptureBarrierWithHeartbeatAsync(
            binding,
            setup.HeartbeatActionQuery!.Sql
        );
        SqlServerRetainedLsnRange retainedRange = await ReadRetainedLsnRangeAsync(inventory);

        _timeProvider.Set(capture.BarrierCapturedAt.AddSeconds(1));
        CoreCdc.CdcSourceHistoryClassificationResult result = await ObserveSourceHistoryAsync(
            binding,
            inventory,
            BuildConnectorOffset(binding, inventory, retainedRange.Start, retainedRange.Start, 2)
        );

        result.Observation.Continuity.Should().Be(CoreCdc.CdcSourceHistoryContinuity.Healthy);
        result
            .Observation.ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.ExactMatch);
        result
            .Observation.RetainedRangeState.Should()
            .Be(CoreCdc.CdcProviderRetainedRangeState.CoversCommittedOffset);
        result
            .Observation.PositionEvidence!.ProviderArtifactName.Should()
            .Be(inventory.SqlServerCaptureInstanceCdcHeartbeatName);
        result.Observation.SqlServerJobs.Should().Be(CoreCdc.CdcSqlServerCdcJobEvidence.Healthy);
        result.Observation.PositionEvidence.RetainedRangeStart.Should().NotBeNullOrWhiteSpace();
        result.Observation.PositionEvidence.RetainedRangeEnd.Should().NotBeNullOrWhiteSpace();
        result
            .Observation.Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CoreCdc.CdcDiagnosticSeverity.Error);
        result.IncidentCandidate.Should().BeNull();
        ValidateSourceHistoryObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task It_reports_unknown_source_history_when_expected_source_partition_hash_is_unavailable()
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();
        CoreCdc.CdcConnectorOffsetObservation connectorOffset = BuildConnectorOffset(
            binding,
            inventory,
            "00000023:00000138:0002",
            "00000023:00000139:0001",
            2
        );

        CoreCdc.CdcSourceHistoryClassificationResult result = await _adapter.ObserveSourceHistoryAsync(
            new(
                OperationId(),
                binding,
                BuildProviderSetupObservation(binding),
                connectorOffset,
                BuildHealthyProviderHistory(inventory)
            )
            {
                SqlServerSchemaHistory = BuildValidSchemaHistory(),
            }
        );

        result.Observation.Continuity.Should().Be(CoreCdc.CdcSourceHistoryContinuity.Unknown);
        result.Observation.IncidentFailureCategory.Should().BeNull();
        result.IncidentCandidate.Should().BeNull();
        result
            .Observation.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CoreCdc.CdcDiagnosticCategory.LocalStateUnavailable
                && diagnostic.Path == "$.expectedConnectSourcePartitionHash"
            );
        ValidateSourceHistoryObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task It_latches_terminal_provider_artifact_loss_when_the_capture_job_is_missing()
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();
        CdcProviderSetupResult setup = await CreateProviderArtifactsAsync(binding, inventory);
        CoreCdc.CdcProviderBarrierCaptureResult capture = await CaptureBarrierWithHeartbeatAsync(
            binding,
            setup.HeartbeatActionQuery!.Sql
        );

        await DropCdcJobAsync("capture");

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(30));
        CoreCdc.CdcSourceHistoryClassificationResult result = await ObserveSourceHistoryAsync(
            binding,
            inventory,
            BuildConnectorOffset(
                binding,
                inventory,
                capture.SqlServerCommitLsn!,
                capture.SqlServerChangeLsn!,
                capture.SqlServerEventSerialNo!.Value
            )
        );

        result.Observation.Continuity.Should().Be(CoreCdc.CdcSourceHistoryContinuity.Lost);
        result
            .Observation.ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.Missing);
        result
            .Observation.IncidentFailureCategory.Should()
            .Be(CoreCdc.CdcIncidentFailureCategory.ProviderArtifactMissing);
        result
            .Observation.SqlServerJobs!.CaptureJobState.Should()
            .Be(CoreCdc.CdcSqlServerCdcJobState.Missing);
        result.IncidentCandidate.Should().NotBeNull();
        CoreCdc.CdcJsonContract.Serialize(result.Observation).Should().NotContain(_database.DatabaseName);
        ValidateSourceHistoryObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task It_reports_unknown_without_latch_when_the_capture_job_is_disabled()
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();
        CdcProviderSetupResult setup = await CreateProviderArtifactsAsync(binding, inventory);
        CoreCdc.CdcProviderBarrierCaptureResult capture = await CaptureBarrierWithHeartbeatAsync(
            binding,
            setup.HeartbeatActionQuery!.Sql
        );

        await DisableCdcJobAsync("capture");

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(30));
        CoreCdc.CdcSourceHistoryClassificationResult result = await ObserveSourceHistoryAsync(
            binding,
            inventory,
            BuildConnectorOffset(
                binding,
                inventory,
                capture.SqlServerCommitLsn!,
                capture.SqlServerChangeLsn!,
                capture.SqlServerEventSerialNo!.Value
            )
        );

        result.Observation.Continuity.Should().Be(CoreCdc.CdcSourceHistoryContinuity.Unknown);
        result
            .Observation.ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.ExactMatch);
        result
            .Observation.RetainedRangeState.Should()
            .Be(CoreCdc.CdcProviderRetainedRangeState.CoversCommittedOffset);
        result
            .Observation.SqlServerJobs!.CaptureJobState.Should()
            .Be(CoreCdc.CdcSqlServerCdcJobState.Stopped);
        result
            .Observation.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CoreCdc.CdcDiagnosticCategory.InvalidObservation
                && diagnostic.Path == "$.providerHistory.sqlServerJobs"
            );
        result.IncidentCandidate.Should().BeNull();
        ValidateSourceHistoryObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task It_reports_unknown_when_job_health_metadata_is_unavailable()
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();
        CdcProviderSetupResult setup = await CreateProviderArtifactsAsync(binding, inventory);
        CoreCdc.CdcProviderBarrierCaptureResult capture = await CaptureBarrierWithHeartbeatAsync(
            binding,
            setup.HeartbeatActionQuery!.Sql
        );

        CdcProviderSetupObservationMapping providerSetup = await ObserveProviderSetupAsync(
            binding,
            inventory
        );
        CoreCdc.CdcProviderSourceHistoryEvidence unavailableProviderHistory =
            providerSetup.ProviderHistory with
            {
                ProviderArtifactState = CoreCdc.CdcProviderArtifactContinuityState.Unknown,
                RetainedRangeState = CoreCdc.CdcProviderRetainedRangeState.Unknown,
                RetainedRangeStart = null,
                RetainedRangeEnd = null,
                UnavailableFacts = [CoreCdc.CdcIncidentUnavailableFact.ProviderRetainedRange],
                SqlServerJobs = CoreCdc.CdcSqlServerCdcJobEvidence.Unknown,
                Diagnostics =
                [
                    new CoreCdc.CdcDiagnostic(
                        CoreCdc.CdcDiagnosticCategory.LocalStateUnavailable,
                        "$.providerHistory.sqlServerJobs",
                        "SQL Server CDC job health metadata was unavailable in provider setup evidence."
                    ),
                ],
            };

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(30));
        CoreCdc.CdcSourceHistoryClassificationResult result = await _adapter.ObserveSourceHistoryAsync(
            new(
                OperationId(),
                binding,
                providerSetup.ProviderSetup,
                BuildConnectorOffset(
                    binding,
                    inventory,
                    capture.SqlServerCommitLsn!,
                    capture.SqlServerChangeLsn!,
                    capture.SqlServerEventSerialNo!.Value
                ),
                unavailableProviderHistory
            )
            {
                SqlServerSchemaHistory = BuildValidSchemaHistory(),
                ExpectedConnectSourcePartitionHash = ExpectedSourcePartitionHash(inventory),
            }
        );

        result.Observation.Continuity.Should().Be(CoreCdc.CdcSourceHistoryContinuity.Unknown);
        result
            .Observation.ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.Unknown);
        result.Observation.RetainedRangeState.Should().Be(CoreCdc.CdcProviderRetainedRangeState.Unknown);
        result
            .Observation.SqlServerJobs!.CaptureJobState.Should()
            .Be(CoreCdc.CdcSqlServerCdcJobState.Unknown);
        result
            .Observation.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Category == CoreCdc.CdcDiagnosticCategory.LocalStateUnavailable
                && diagnostic.Path == "$.providerHistory.sqlServerJobs"
            );
        result.IncidentCandidate.Should().BeNull();
        ValidateSourceHistoryObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task It_latches_terminal_provider_artifact_loss_when_capture_columns_do_not_match_source_inventory()
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();
        CdcProviderSetupResult setup = await CreateProviderArtifactsAsync(binding, inventory);
        CoreCdc.CdcProviderBarrierCaptureResult capture = await CaptureBarrierWithHeartbeatAsync(
            binding,
            setup.HeartbeatActionQuery!.Sql
        );

        await RecreateDocumentCaptureWithMissingColumnAsync(inventory);

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(30));
        CoreCdc.CdcSourceHistoryClassificationResult result = await ObserveSourceHistoryAsync(
            binding,
            inventory,
            BuildConnectorOffset(
                binding,
                inventory,
                capture.SqlServerCommitLsn!,
                capture.SqlServerChangeLsn!,
                capture.SqlServerEventSerialNo!.Value
            )
        );

        result.Observation.Continuity.Should().Be(CoreCdc.CdcSourceHistoryContinuity.Lost);
        result
            .Observation.ProviderArtifactState.Should()
            .Be(CoreCdc.CdcProviderArtifactContinuityState.Recreated);
        result
            .Observation.IncidentFailureCategory.Should()
            .Be(CoreCdc.CdcIncidentFailureCategory.ProviderArtifactRecreated);
        result
            .Observation.Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISMATCH"
                && diagnostic.Component == CoreCdc.CdcDiagnosticComponent.SourceHistory
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance.ToString()
            );
        result.IncidentCandidate.Should().NotBeNull();
        ValidateSourceHistoryObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task It_latches_terminal_provider_artifact_loss_when_the_binding_heartbeat_capture_is_missing()
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();
        CdcProviderSetupResult setup = await CreateProviderArtifactsAsync(binding, inventory);
        CoreCdc.CdcProviderBarrierCaptureResult capture = await CaptureBarrierWithHeartbeatAsync(
            binding,
            setup.HeartbeatActionQuery!.Sql
        );

        await DropHeartbeatCaptureInstanceAsync(inventory.SqlServerCaptureInstanceCdcHeartbeatName!);

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(30));
        CoreCdc.CdcSourceHistoryClassificationResult result = await ObserveSourceHistoryAsync(
            binding,
            inventory,
            BuildConnectorOffset(
                binding,
                inventory,
                capture.SqlServerCommitLsn!,
                capture.SqlServerChangeLsn!,
                capture.SqlServerEventSerialNo!.Value
            )
        );

        result.Observation.Continuity.Should().Be(CoreCdc.CdcSourceHistoryContinuity.Lost);
        result
            .Observation.IncidentFailureCategory.Should()
            .Be(CoreCdc.CdcIncidentFailureCategory.ProviderArtifactMissing);
        result.IncidentCandidate.Should().NotBeNull();
        (await CaptureInstanceExistsAsync(inventory.SqlServerCaptureInstanceCdcHeartbeatName!))
            .Should()
            .BeFalse("source-history observation must not recreate missing provider artifacts");
        CoreCdc.CdcJsonContract.Serialize(result.Observation).Should().NotContain(_database.DatabaseName);
        ValidateSourceHistoryObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    private async Task<CdcProviderSetupResult> CreateProviderArtifactsAsync(
        CoreCdc.CdcBinding binding,
        CoreCdc.CdcArtifactInventory inventory
    )
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        CdcProviderSetupResult result = await RunSetupAsync(connection, binding, inventory);

        result
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.CreatedOrMatched, DescribeDiagnostics(result.Diagnostics));
        result
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        return result;
    }

    private async Task<CdcProviderSetupResult> RunSetupAsync(
        SqlConnection connection,
        CoreCdc.CdcBinding binding,
        CoreCdc.CdcArtifactInventory inventory,
        DdlCdcProviderSetupMode mode = DdlCdcProviderSetupMode.InitialCreateOrExactMatch
    )
    {
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);
        var executor = new DbConnectionCdcProviderDatabaseExecutor(
            connection,
            providerErrorIdentityMapper: MssqlCdcProviderErrorIdentityMapper.MapProviderErrorIdentity
        );

        return await service.SetupAsync(
            new CdcProviderSetupRequest(
                provider: DdlCdcProvider.SqlServer,
                mode: mode,
                boundPhysicalSourceFingerprint: new CdcSourceFingerprint(
                    CdcSourceFingerprintMetadata.Version,
                    binding.PhysicalSourceFingerprint
                ),
                setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("sa")),
                connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName(_connectorPrincipalName)),
                artifactNames: CdcProviderArtifactNames.ForSqlServer(
                    new CdcSafeName(inventory.SqlServerCdcGatingRoleName!),
                    new Dictionary<CdcSourceTableKind, CdcSafeName>
                    {
                        [CdcSourceTableKind.Document] = new(inventory.SqlServerCaptureInstanceDocumentName!),
                        [CdcSourceTableKind.DocumentCache] = new(
                            inventory.SqlServerCaptureInstanceDocumentCacheName!
                        ),
                        [CdcSourceTableKind.CdcHeartbeat] = new(
                            inventory.SqlServerCaptureInstanceCdcHeartbeatName!
                        ),
                    }
                ),
                artifactOutput: new CdcProviderArtifactOutputRequest(IncludeManifestPayload: false),
                expectedSourceInventory: _fixture.CdcSourceInventory,
                dmsManagedTableInventory: _fixture.CdcDmsManagedTableInventory,
                databaseExecutor: executor
            )
        );
    }

    private async Task<CdcProviderSetupObservationMapping> ObserveProviderSetupAsync(
        CoreCdc.CdcBinding binding,
        CoreCdc.CdcArtifactInventory inventory
    )
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        CdcProviderSetupResult result = await RunSetupAsync(
            connection,
            binding,
            inventory,
            DdlCdcProviderSetupMode.ValidateOnly
        );

        return CdcProviderSetupResultMapper.MapValidateOnlyResult(
            OperationId(),
            _timeProvider.GetUtcNow(),
            binding,
            result
        );
    }

    private async Task<CoreCdc.CdcSourceHistoryClassificationResult> ObserveSourceHistoryAsync(
        CoreCdc.CdcBinding binding,
        CoreCdc.CdcArtifactInventory inventory,
        CoreCdc.CdcConnectorOffsetObservation connectorOffset
    )
    {
        CdcProviderSetupObservationMapping providerSetup = await ObserveProviderSetupAsync(
            binding,
            inventory
        );

        return await _adapter.ObserveSourceHistoryAsync(
            new(
                OperationId(),
                binding,
                providerSetup.ProviderSetup,
                connectorOffset,
                providerSetup.ProviderHistory
            )
            {
                SqlServerSchemaHistory = BuildValidSchemaHistory(),
                ExpectedConnectSourcePartitionHash = ExpectedSourcePartitionHash(inventory),
            }
        );
    }

    private async Task<CoreCdc.CdcProviderBarrierCaptureResult> CaptureBarrierWithHeartbeatAsync(
        CoreCdc.CdcBinding binding,
        string heartbeatActionSql
    )
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(60));
        Task<CoreCdc.CdcProviderBarrierCaptureResult> captureTask = _adapter.CaptureBarrierAsync(
            new(_database.ConnectionString, binding)
            {
                CaptureWaitTimeout = TimeSpan.FromSeconds(30),
                PollInterval = TimeSpan.FromMilliseconds(250),
            },
            cancellation.Token
        );
        Task heartbeatTask = ExecuteHeartbeatUntilCaptureCompletesAsync(
            heartbeatActionSql,
            captureTask,
            cancellation.Token
        );

        CoreCdc.CdcProviderBarrierCaptureResult capture = await captureTask;
        await heartbeatTask;
        capture.Diagnostics.Should().BeEmpty();

        return capture;
    }

    private async Task ExecuteHeartbeatUntilCaptureCompletesAsync(
        string heartbeatActionSql,
        Task captureTask,
        CancellationToken cancellationToken
    )
    {
        while (!captureTask.IsCompleted)
        {
            _timeProvider.Advance(TimeSpan.FromSeconds(1));
            await ExecuteNonQueryAsync(heartbeatActionSql);
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private async Task<CoreCdc.CdcBinding> BuildBindingAsync()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        Guid sourceIdentity = Guid.Parse(await ReadDataStoreIdentityAsync(connection));
        string sourceFingerprint = CoreCdc
            .CdcPhysicalSourceFingerprintCalculator.Compute(CoreCdc.CdcProvider.SqlServer, sourceIdentity)
            .Fingerprint!;
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();

        return new(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            "dms-local",
            CoreCdc.CdcTargetValidator.DefaultBindingTenantKey,
            "1",
            InstanceKey,
            1,
            CoreCdc.CdcProvider.SqlServer,
            sourceFingerprint,
            inventory.ConnectorName,
            inventory.TopicName,
            3,
            CoreCdc.CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            CoreCdc.CdcJsonContract.CurrentContractVersion
        );
    }

    private static CoreCdc.CdcArtifactInventory BuildInventory() =>
        CoreCdc
            .CdcArtifactNameGenerator.Render(
                new("dms-local", "edfi.dms", InstanceKey, 1, CoreCdc.CdcProvider.SqlServer)
            )
            .Inventory!;

    private CoreCdc.CdcConnectorOffsetObservation BuildConnectorOffset(
        CoreCdc.CdcBinding binding,
        CoreCdc.CdcArtifactInventory inventory,
        string commitLsn,
        string changeLsn,
        long eventSerialNo
    ) =>
        new(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            OperationId(),
            _timeProvider.GetUtcNow(),
            binding.ToTargetIdentity(),
            CoreCdc.CdcProvider.SqlServer,
            binding.PhysicalSourceFingerprint,
            inventory.ConnectorName,
            inventory.TopicPrefix,
            CoreCdc.CdcConnectorOffsetMatchResult.Exact,
            ExpectedSourcePartitionHash(inventory),
            false,
            false,
            null,
            commitLsn,
            changeLsn,
            eventSerialNo,
            []
        );

    private CoreCdc.CdcProviderSetupObservation BuildProviderSetupObservation(CoreCdc.CdcBinding binding) =>
        new(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            OperationId(),
            _timeProvider.GetUtcNow(),
            binding.ToTargetIdentity(),
            CoreCdc.CdcProvider.SqlServer,
            binding.PhysicalSourceFingerprint,
            CoreCdc.CdcProviderSetupMode.ValidateOnly,
            CoreCdc.CdcProviderSetupOutcome.Satisfied,
            CoreCdc.CdcProviderSetupState.Matched,
            CoreCdc.CdcProviderSetupState.Matched,
            CoreCdc.CdcProviderSetupState.Matched,
            CoreCdc.CdcProviderSetupState.Matched,
            []
        );

    private static CoreCdc.CdcProviderSourceHistoryEvidence BuildHealthyProviderHistory(
        CoreCdc.CdcArtifactInventory inventory
    ) =>
        new(
            CoreCdc.CdcProviderArtifactContinuityState.ExactMatch,
            CoreCdc.CdcProviderRetainedRangeState.CoversCommittedOffset,
            inventory.SqlServerCaptureInstanceCdcHeartbeatName,
            "00000023:00000138:0000",
            "00000023:00000140:0000",
            []
        )
        {
            SqlServerJobs = CoreCdc.CdcSqlServerCdcJobEvidence.Healthy,
        };

    private string ExpectedSourcePartitionHash(CoreCdc.CdcArtifactInventory inventory) =>
        CoreCdc
            .CdcSourcePartitionHashCalculator.ComputeSqlServer(inventory.TopicPrefix, _database.DatabaseName)
            .Hash!;

    private static CoreCdc.CdcSqlServerSchemaHistoryEvidence BuildValidSchemaHistory() =>
        new(
            CoreCdc.CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission,
            CoreCdc.CdcSqlServerSchemaHistoryState.Valid
        );

    private CoreCdc.CdcContractValidationResult ValidateBarrierObservation(
        CoreCdc.CdcProviderBarrierObservation observation,
        CoreCdc.CdcBinding binding
    ) =>
        CoreCdc.CdcProviderBarrierObservationValidator.Validate(
            observation,
            new(
                OperationId(),
                binding.ToTargetIdentity(),
                binding.PhysicalSourceFingerprint,
                _timeProvider.GetUtcNow()
            )
        );

    private CoreCdc.CdcContractValidationResult ValidateSourceHistoryObservation(
        CoreCdc.CdcSourceHistoryObservation observation,
        CoreCdc.CdcBinding binding
    ) =>
        CoreCdc.CdcSourceHistoryObservationValidator.ValidateForBinding(
            observation,
            binding,
            new(
                OperationId(),
                binding.ToTargetIdentity(),
                binding.PhysicalSourceFingerprint,
                _timeProvider.GetUtcNow()
            )
        );

    private async Task DropHeartbeatCaptureInstanceAsync(string heartbeatCaptureInstanceName)
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sys.sp_cdc_disable_table
                @source_schema = N'dms',
                @source_name = N'CdcHeartbeat',
                @capture_instance = @captureInstanceName;
            """;
        command.Parameters.AddWithValue("@captureInstanceName", heartbeatCaptureInstanceName);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropCdcJobAsync(string jobType)
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sys.sp_cdc_drop_job @job_type = @jobType;
            """;
        command.Parameters.AddWithValue("@jobType", jobType);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DisableCdcJobAsync(string jobType)
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC msdb.dbo.sp_update_job
                @job_name = @jobName,
                @enabled = 0;
            """;
        command.Parameters.AddWithValue("@jobName", $"cdc.{_database.DatabaseName}_{jobType}");
        await command.ExecuteNonQueryAsync();
    }

    private async Task<bool> CaptureInstanceExistsAsync(string captureInstanceName)
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM cdc.change_tables
            WHERE capture_instance = @captureInstanceName;
            """;
        command.Parameters.AddWithValue("@captureInstanceName", captureInstanceName);

        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private async Task<IReadOnlyDictionary<string, string[]>> ReadCapturedColumnNamesAsync(
        CoreCdc.CdcArtifactInventory inventory
    )
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT capture_info.capture_instance, captured_column.column_name
            FROM cdc.change_tables capture_info
            INNER JOIN cdc.captured_columns captured_column
                ON captured_column.[object_id] = capture_info.[object_id]
            WHERE capture_info.capture_instance IN (
                @documentCaptureName,
                @documentCacheCaptureName,
                @heartbeatCaptureName
            )
            ORDER BY capture_info.capture_instance, captured_column.column_ordinal;
            """;
        command.Parameters.AddWithValue(
            "@documentCaptureName",
            inventory.SqlServerCaptureInstanceDocumentName!
        );
        command.Parameters.AddWithValue(
            "@documentCacheCaptureName",
            inventory.SqlServerCaptureInstanceDocumentCacheName!
        );
        command.Parameters.AddWithValue(
            "@heartbeatCaptureName",
            inventory.SqlServerCaptureInstanceCdcHeartbeatName!
        );

        Dictionary<string, List<string>> columnsByCapture = new(StringComparer.Ordinal);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string captureName = reader.GetString(0);
            if (!columnsByCapture.TryGetValue(captureName, out List<string>? columns))
            {
                columns = [];
                columnsByCapture[captureName] = columns;
            }

            columns.Add(reader.GetString(1));
        }

        return columnsByCapture.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.Ordinal
        );
    }

    private async Task RecreateDocumentCaptureWithMissingColumnAsync(CoreCdc.CdcArtifactInventory inventory)
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sys.sp_cdc_disable_table
                @source_schema = N'dms',
                @source_name = N'Document',
                @capture_instance = @captureInstanceName;

            EXEC sys.sp_cdc_enable_table
                @source_schema = N'dms',
                @source_name = N'Document',
                @capture_instance = @captureInstanceName,
                @supports_net_changes = 0,
                @role_name = @roleName,
                @index_name = NULL,
                @captured_column_list = N'[DocumentId], [DocumentUuid]',
                @filegroup_name = NULL,
                @allow_partition_switch = 0;
            """;
        command.Parameters.AddWithValue(
            "@captureInstanceName",
            inventory.SqlServerCaptureInstanceDocumentName!
        );
        command.Parameters.AddWithValue("@roleName", inventory.SqlServerCdcGatingRoleName!);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<SqlServerRetainedLsnRange> ReadRetainedLsnRangeAsync(
        CoreCdc.CdcArtifactInventory inventory
    )
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                capture_info.capture_instance,
                COALESCE(sys.fn_varbintohexstr(sys.fn_cdc_get_min_lsn(capture_info.capture_instance)), N'') AS retained_min_lsn,
                COALESCE(sys.fn_varbintohexstr(sys.fn_cdc_get_max_lsn()), N'') AS retained_max_lsn
            FROM cdc.change_tables capture_info
            WHERE capture_info.capture_instance IN (
                @documentCaptureName,
                @documentCacheCaptureName,
                @heartbeatCaptureName
            )
            ORDER BY capture_info.capture_instance;
            """;
        command.Parameters.AddWithValue(
            "@documentCaptureName",
            inventory.SqlServerCaptureInstanceDocumentName!
        );
        command.Parameters.AddWithValue(
            "@documentCacheCaptureName",
            inventory.SqlServerCaptureInstanceDocumentCacheName!
        );
        command.Parameters.AddWithValue(
            "@heartbeatCaptureName",
            inventory.SqlServerCaptureInstanceCdcHeartbeatName!
        );

        CoreCdc.CdcSqlServerLsn? retainedStart = null;
        string? retainedEnd = null;
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            CoreCdc.CdcSqlServerLsn minLsn = ParseSqlServerLsn(reader.GetString(1));
            if (retainedStart is null || minLsn.CompareTo(retainedStart.Value) > 0)
            {
                retainedStart = minLsn;
            }

            retainedEnd ??= ParseSqlServerLsn(reader.GetString(2)).ToString();
        }

        retainedStart.Should().NotBeNull();
        retainedEnd.Should().NotBeNullOrWhiteSpace();
        return new(retainedStart!.Value.ToString(), retainedEnd!);
    }

    private async Task ExecuteNonQueryAsync(string sql)
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadDataStoreIdentityAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONVERT(nvarchar(36), [SourceIdentity])
            FROM [dms].[DataStoreIdentity]
            WHERE [DataStoreIdentitySingletonId] = 1;
            """;

        return (await command.ExecuteScalarAsync())!.ToString()!;
    }

    private static CoreCdc.CdcSqlServerLsn ParseSqlServerLsn(string value)
    {
        CoreCdc.CdcSqlServerLsnResult result = CoreCdc.CdcSqlServerProviderPositionParser.ParseLsn(
            value,
            "$.lsn"
        );

        result.Succeeded.Should().BeTrue();
        return result.Lsn!.Value;
    }

    private static void CreateConnectorLoginAndUser(string databaseName, string connectorPrincipalName)
    {
        using var connection = new SqlConnection(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF SUSER_ID(N'{EscapeSqlLiteral(connectorPrincipalName)}') IS NULL
            BEGIN
                CREATE LOGIN {QuoteIdentifier(
                connectorPrincipalName
            )} WITH PASSWORD = '{ConnectorPassword}', CHECK_POLICY = OFF;
            END;

            USE {QuoteIdentifier(databaseName)};

            IF USER_ID(N'{EscapeSqlLiteral(connectorPrincipalName)}') IS NULL
            BEGIN
                CREATE USER {QuoteIdentifier(connectorPrincipalName)} FOR LOGIN {QuoteIdentifier(
                connectorPrincipalName
            )};
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static void DropConnectorLoginIfExists(string connectorPrincipalName)
    {
        SqlConnection.ClearAllPools();

        using var connection = new SqlConnection(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF SUSER_ID(N'{EscapeSqlLiteral(connectorPrincipalName)}') IS NOT NULL
            BEGIN
                DROP LOGIN {QuoteIdentifier(connectorPrincipalName)};
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    private static string EscapeSqlLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string CaptureNameForSourceTable(
        CoreCdc.CdcArtifactInventory inventory,
        CdcSourceTableKind tableKind
    ) =>
        tableKind switch
        {
            CdcSourceTableKind.Document => inventory.SqlServerCaptureInstanceDocumentName!,
            CdcSourceTableKind.DocumentCache => inventory.SqlServerCaptureInstanceDocumentCacheName!,
            CdcSourceTableKind.CdcHeartbeat => inventory.SqlServerCaptureInstanceCdcHeartbeatName!,
            _ => throw new ArgumentOutOfRangeException(
                nameof(tableKind),
                tableKind,
                "Unsupported CDC source table kind."
            ),
        };

    private static string OperationId() => "cdc-operation-mssql-source-position";

    public enum InvalidBarrierInput
    {
        OperationId,
        SourceFingerprint,
        ConnectorName,
        TopicPrefix,
        CaptureProvider,
        SourcePartitionHash,
    }

    private static string DescribeDiagnostics(IReadOnlyList<CdcProviderDiagnostic> diagnostics) =>
        string.Join(
            "; ",
            diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}:{diagnostic.ArtifactKind}:{diagnostic.SafeName.Value}:{diagnostic.ExpectedValue}->{diagnostic.ObservedValue}:error_class={diagnostic.ProviderErrorClass ?? "none"}:error_code={diagnostic.ProviderErrorCode ?? "none"}:error_state={diagnostic.ProviderErrorState ?? "none"}"
            )
        );

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Set(DateTimeOffset utcNowValue)
        {
            _utcNow = utcNowValue.ToUniversalTime();
        }

        public void Advance(TimeSpan value)
        {
            _utcNow = _utcNow.Add(value).ToUniversalTime();
        }
    }

    private sealed record SqlServerRetainedLsnRange(string Start, string End);
}
