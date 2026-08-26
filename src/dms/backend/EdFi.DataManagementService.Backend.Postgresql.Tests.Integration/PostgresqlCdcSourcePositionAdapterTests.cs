// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;
using DdlCdcProvider = EdFi.DataManagementService.Backend.Ddl.CdcProvider;
using DdlCdcProviderSetupMode = EdFi.DataManagementService.Backend.Ddl.CdcProviderSetupMode;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("CdcProviderPosition")]
[Category("CdcSourceHistory")]
public class Given_PostgresqlCdcSourcePositionAdapterTests
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private static readonly DateTimeOffset ProjectionCaughtUpObservedAt = new(
        2026,
        8,
        18,
        12,
        0,
        0,
        TimeSpan.Zero
    );
    private const string OtherSourceFingerprint =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private NpgsqlDataSourceCache _dataSourceCache = null!;
    private MutableTimeProvider _timeProvider = null!;
    private PostgresqlCdcSourcePositionAdapter _adapter = null!;
    private string _instanceKey = null!;
    private string _connectorRoleName = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _instanceKey = $"i{Guid.NewGuid():N}";
        _dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        _timeProvider = new MutableTimeProvider(ProjectionCaughtUpObservedAt.AddSeconds(1));
        _adapter = new PostgresqlCdcSourcePositionAdapter(
            _dataSourceCache,
            new PostgresqlDocumentCacheProviderCommandTimeoutClassifier(),
            _timeProvider,
            NullLogger<PostgresqlCdcSourcePositionAdapter>.Instance
        );
        _connectorRoleName = $"cdc_connector_{_database.DatabaseName}";
        CreateConnectorRole(_connectorRoleName);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await DropReplicationSlotIfExistsAsync(BuildInventory().PostgresqlLogicalSlotName!);
        }

        _dataSourceCache?.Dispose();

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        DropConnectorRoleIfExists(_connectorRoleName);
    }

    [Test]
    public async Task It_captures_pg_current_wal_lsn_and_returns_a_reached_barrier_observation()
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();

        CoreCdc.CdcProviderBarrierCaptureResult capture = await _adapter.CaptureBarrierAsync(
            new(_database.ConnectionString, binding)
        );
        CoreCdc.CdcPostgresqlWalPosition capturedPosition = ParsePostgresqlLsn(capture.PostgresqlBarrierLsn!);

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(2));
        CoreCdc.CdcConnectorOffsetObservation connectorOffset = BuildConnectorOffset(
            binding,
            inventory,
            unchecked((long)capturedPosition.Value)
        );

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(3));
        CoreCdc.CdcProviderBarrierObservation observation = _adapter.ObserveProviderBarrier(
            new(OperationId(), binding, ProjectionCaughtUpObservedAt, capture, connectorOffset)
        );

        capture.Succeeded.Should().BeTrue();
        observation.BarrierState.Should().Be(CoreCdc.CdcProviderBarrierState.Reached);
        observation.PostgresqlBarrierLsn.Should().Be(capture.PostgresqlBarrierLsn);
        observation.CommittedPosition.Should().Be(capturedPosition.ToString());
        observation.SqlServerCommitLsn.Should().BeNull();
        observation.Diagnostics.Should().BeEmpty();
        ValidateBarrierObservation(observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task It_propagates_caller_cancellation_without_returning_a_barrier_result()
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        using CancellationTokenSource cancellationSource = new();
        await cancellationSource.CancelAsync();

        Func<Task> act = async () =>
            await _adapter.CaptureBarrierAsync(
                new(_database.ConnectionString, binding),
                cancellationSource.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_timestamps_the_barrier_after_the_wal_query_completes()
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();
        string connectionString = BuildSingleConnectionPoolConnectionString();

        await using NpgsqlConnection heldConnection = await _dataSourceCache
            .GetOrCreate(connectionString)
            .OpenConnectionAsync();

        DateTimeOffset captureStartedAt = ProjectionCaughtUpObservedAt.AddMilliseconds(100);
        DateTimeOffset projectionCaughtUpDuringCapture = captureStartedAt.AddSeconds(1);
        DateTimeOffset barrierAcceptedAt = projectionCaughtUpDuringCapture.AddSeconds(1);

        _timeProvider.Set(captureStartedAt);
        Task<CoreCdc.CdcProviderBarrierCaptureResult> captureTask = _adapter.CaptureBarrierAsync(
            new(connectionString, binding)
        );

        (await Task.WhenAny(captureTask, Task.Delay(TimeSpan.FromMilliseconds(100))))
            .Should()
            .NotBe(captureTask, "the held single-connection pool should block the WAL query");

        _timeProvider.Set(barrierAcceptedAt);
        await heldConnection.DisposeAsync();
        CoreCdc.CdcProviderBarrierCaptureResult capture = await captureTask.WaitAsync(
            TimeSpan.FromSeconds(10)
        );
        CoreCdc.CdcPostgresqlWalPosition capturedPosition = ParsePostgresqlLsn(capture.PostgresqlBarrierLsn!);

        _timeProvider.Set(barrierAcceptedAt.AddSeconds(1));
        CoreCdc.CdcConnectorOffsetObservation connectorOffset = BuildConnectorOffset(
            binding,
            inventory,
            unchecked((long)capturedPosition.Value)
        );

        _timeProvider.Set(barrierAcceptedAt.AddSeconds(2));
        CoreCdc.CdcProviderBarrierObservation observation = _adapter.ObserveProviderBarrier(
            new(OperationId(), binding, projectionCaughtUpDuringCapture, capture, connectorOffset)
        );

        capture.Succeeded.Should().BeTrue();
        capture.BarrierCapturedAt.Should().Be(barrierAcceptedAt);
        capture.BarrierCapturedAt.Should().BeAfter(projectionCaughtUpDuringCapture);
        observation.BarrierState.Should().Be(CoreCdc.CdcProviderBarrierState.Reached);
        observation.Diagnostics.Should().BeEmpty();
        ValidateBarrierObservation(observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task It_reports_not_reached_when_lsn_proc_is_behind_the_captured_barrier()
    {
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();

        CoreCdc.CdcProviderBarrierCaptureResult capture = await _adapter.CaptureBarrierAsync(
            new(_database.ConnectionString, binding)
        );
        CoreCdc.CdcPostgresqlWalPosition capturedPosition = ParsePostgresqlLsn(capture.PostgresqlBarrierLsn!);

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(2));
        CoreCdc.CdcConnectorOffsetObservation connectorOffset = BuildConnectorOffset(
            binding,
            inventory,
            unchecked((long)(capturedPosition.Value - 1))
        );

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(3));
        CoreCdc.CdcProviderBarrierObservation observation = _adapter.ObserveProviderBarrier(
            new(OperationId(), binding, ProjectionCaughtUpObservedAt, capture, connectorOffset)
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
            CoreCdc.CdcProviderBarrierCaptureResult.PostgresqlSuccess(
                "0/16B6C50",
                ProjectionCaughtUpObservedAt.AddSeconds(1)
            );

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(2));
        CoreCdc.CdcConnectorOffsetObservation connectorOffset = BuildConnectorOffset(
            binding,
            inventory,
            unchecked((long)ParsePostgresqlLsn(capture.PostgresqlBarrierLsn!).Value)
        );
        string operationId = OperationId();

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
                capture = CoreCdc.CdcProviderBarrierCaptureResult.SqlServerSuccess(
                    "00000000:016b6c50:0001",
                    "00000000:016b6c50:0002",
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
            new(operationId, binding, ProjectionCaughtUpObservedAt, capture, connectorOffset)
        );

        observation.BarrierState.Should().Be(CoreCdc.CdcProviderBarrierState.Unknown);
        observation.CommittedPosition.Should().BeNull();
        observation.Diagnostics.Should().Contain(diagnostic => diagnostic.Category == expectedDiagnostic);
    }

    [Test]
    public async Task It_reads_slot_publication_and_retained_wal_metadata_for_healthy_continuity()
    {
        AssumePostgresqlLogicalReplicationAvailable();
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        await CreateProviderArtifactsAsync();
        PostgresqlRetainedWalRange retainedRange = await ReadRetainedWalRangeAsync();
        CdcProviderSetupObservationMapping providerSetup = await ObserveProviderSetupAsync(binding);

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(2));
        CoreCdc.CdcSourceHistoryClassificationResult result = await _adapter.ObserveSourceHistoryAsync(
            new(
                OperationId(),
                binding,
                providerSetup.ProviderSetup,
                BuildConnectorOffset(binding, BuildInventory(), unchecked((long)retainedRange.Start.Value)),
                providerSetup.ProviderHistory
            )
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
            .Be(BuildInventory().PostgresqlLogicalSlotName);
        result.Observation.PositionEvidence.RetainedRangeStart.Should().Be(retainedRange.Start.ToString());
        result.Observation.PositionEvidence.RetainedRangeEnd.Should().Be(retainedRange.End.ToString());
        result.Observation.Diagnostics.Should().BeEmpty();
        result.IncidentCandidate.Should().BeNull();
        ValidateSourceHistoryObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task It_latches_terminal_provider_artifact_loss_when_the_binding_slot_is_missing()
    {
        AssumePostgresqlLogicalReplicationAvailable();
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        await CreateProviderArtifactsAsync();
        PostgresqlRetainedWalRange retainedRange = await ReadRetainedWalRangeAsync();
        await DropReplicationSlotIfExistsAsync(BuildInventory().PostgresqlLogicalSlotName!);
        CdcProviderSetupObservationMapping providerSetup = await ObserveProviderSetupAsync(binding);

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(2));
        CoreCdc.CdcSourceHistoryClassificationResult result = await _adapter.ObserveSourceHistoryAsync(
            new(
                OperationId(),
                binding,
                providerSetup.ProviderSetup,
                BuildConnectorOffset(binding, BuildInventory(), unchecked((long)retainedRange.Start.Value)),
                providerSetup.ProviderHistory
            )
        );

        result.Observation.Continuity.Should().Be(CoreCdc.CdcSourceHistoryContinuity.Lost);
        result
            .Observation.IncidentFailureCategory.Should()
            .Be(CoreCdc.CdcIncidentFailureCategory.ProviderArtifactMissing);
        result.IncidentCandidate.Should().NotBeNull();
        (await ReplicationSlotExistsAsync(BuildInventory().PostgresqlLogicalSlotName!))
            .Should()
            .BeFalse("source-history observation must not recreate missing provider artifacts");
        CoreCdc.CdcJsonContract.Serialize(result.Observation).Should().NotContain(_database.ConnectionString);
        CoreCdc.CdcJsonContract.Serialize(result.Observation).Should().NotContain(_database.DatabaseName);
        ValidateSourceHistoryObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task It_classifies_committed_offsets_before_retained_wal_as_a_history_gap()
    {
        AssumePostgresqlLogicalReplicationAvailable();
        CoreCdc.CdcBinding binding = await BuildBindingAsync();
        await CreateProviderArtifactsAsync();
        PostgresqlRetainedWalRange retainedRange = await ReadRetainedWalRangeAsync();
        CdcProviderSetupObservationMapping providerSetup = await ObserveProviderSetupAsync(binding);

        _timeProvider.Set(ProjectionCaughtUpObservedAt.AddSeconds(2));
        CoreCdc.CdcSourceHistoryClassificationResult result = await _adapter.ObserveSourceHistoryAsync(
            new(
                OperationId(),
                binding,
                providerSetup.ProviderSetup,
                BuildConnectorOffset(
                    binding,
                    BuildInventory(),
                    unchecked((long)(retainedRange.Start.Value - 1))
                ),
                providerSetup.ProviderHistory
            )
        );

        result.Observation.Continuity.Should().Be(CoreCdc.CdcSourceHistoryContinuity.Lost);
        result
            .Observation.IncidentFailureCategory.Should()
            .Be(CoreCdc.CdcIncidentFailureCategory.RetainedHistoryGap);
        result.Observation.RetainedRangeState.Should().Be(CoreCdc.CdcProviderRetainedRangeState.Gap);
        result.IncidentCandidate.Should().NotBeNull();
        ValidateSourceHistoryObservation(result.Observation, binding).Succeeded.Should().BeTrue();
    }

    private async Task CreateProviderArtifactsAsync()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        CdcProviderSetupResult result = await RunSetupAsync(connection);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        result.Diagnostics.Should().BeEmpty();
    }

    private async Task<CdcProviderSetupResult> RunSetupAsync(
        NpgsqlConnection connection,
        DdlCdcProviderSetupMode mode = DdlCdcProviderSetupMode.InitialCreateOrExactMatch
    )
    {
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();
        var service = new CdcProviderSetupService([new CdcPostgresqlHeartbeatPublicationProvider()]);
        var executor = new DbConnectionCdcProviderDatabaseExecutor(connection);

        return await service.SetupAsync(
            new CdcProviderSetupRequest(
                provider: DdlCdcProvider.Postgresql,
                mode: mode,
                boundPhysicalSourceFingerprint: CdcSourceFingerprintMetadata.Compute(
                    DdlCdcProvider.Postgresql,
                    await ReadDataStoreIdentityAsync(connection)
                ),
                setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("postgres")),
                connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName(_connectorRoleName)),
                artifactNames: CdcProviderArtifactNames.ForPostgresql(
                    new CdcSafeName(inventory.PostgresqlPublicationName!),
                    new CdcSafeName(inventory.PostgresqlLogicalSlotName!)
                ),
                artifactOutput: new CdcProviderArtifactOutputRequest(IncludeManifestPayload: false),
                expectedSourceInventory: _fixture.CdcSourceInventory,
                dmsManagedTableInventory: _fixture.CdcDmsManagedTableInventory,
                databaseExecutor: executor
            )
        );
    }

    private async Task<CdcProviderSetupObservationMapping> ObserveProviderSetupAsync(
        CoreCdc.CdcBinding binding
    )
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        CdcProviderSetupResult result = await RunSetupAsync(connection, DdlCdcProviderSetupMode.ValidateOnly);

        return CdcProviderSetupResultMapper.MapValidateOnlyResult(
            OperationId(),
            _timeProvider.GetUtcNow(),
            binding,
            result
        );
    }

    private async Task<CoreCdc.CdcBinding> BuildBindingAsync()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        Guid sourceIdentity = Guid.Parse(await ReadDataStoreIdentityAsync(connection));
        string sourceFingerprint = CoreCdc
            .CdcPhysicalSourceFingerprintCalculator.Compute(CoreCdc.CdcProvider.Postgresql, sourceIdentity)
            .Fingerprint!;
        CoreCdc.CdcArtifactInventory inventory = BuildInventory();

        return new(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            "dms-local",
            CoreCdc.CdcTargetValidator.DefaultBindingTenantKey,
            "1",
            _instanceKey,
            1,
            CoreCdc.CdcProvider.Postgresql,
            sourceFingerprint,
            inventory.ConnectorName,
            inventory.TopicName,
            3,
            CoreCdc.CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            CoreCdc.CdcJsonContract.CurrentContractVersion
        );
    }

    private CoreCdc.CdcArtifactInventory BuildInventory() =>
        CoreCdc
            .CdcArtifactNameGenerator.Render(
                new("dms-local", "edfi.dms", _instanceKey, 1, CoreCdc.CdcProvider.Postgresql)
            )
            .Inventory!;

    private string BuildSingleConnectionPoolConnectionString()
    {
        NpgsqlConnectionStringBuilder builder = new(_database.ConnectionString)
        {
            ApplicationName = $"EdFi.DMS.CdcBarrier.{Guid.NewGuid():N}",
            MaxPoolSize = 1,
            MinPoolSize = 0,
            Pooling = true,
        };

        return builder.ConnectionString;
    }

    private CoreCdc.CdcConnectorOffsetObservation BuildConnectorOffset(
        CoreCdc.CdcBinding binding,
        CoreCdc.CdcArtifactInventory inventory,
        long lsnProc
    )
    {
        string sourcePartitionHash = CoreCdc
            .CdcSourcePartitionHashCalculator.ComputePostgresql(inventory.ConnectorName)
            .Hash!;

        return new(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            OperationId(),
            _timeProvider.GetUtcNow(),
            binding.ToTargetIdentity(),
            CoreCdc.CdcProvider.Postgresql,
            binding.PhysicalSourceFingerprint,
            inventory.ConnectorName,
            inventory.TopicPrefix,
            CoreCdc.CdcConnectorOffsetMatchResult.Exact,
            sourcePartitionHash,
            false,
            false,
            lsnProc,
            null,
            null,
            null,
            []
        );
    }

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

    private async Task<PostgresqlRetainedWalRange> ReadRetainedWalRangeAsync()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT slot.restart_lsn::text, slot.confirmed_flush_lsn::text
            FROM pg_catalog.pg_replication_slots slot
            WHERE slot.slot_name = @slotName;
            """;
        command.Parameters.AddWithValue("slotName", BuildInventory().PostgresqlLogicalSlotName!);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var range = new PostgresqlRetainedWalRange(
            ParsePostgresqlLsn(reader.GetString(0)),
            ParsePostgresqlLsn(reader.GetString(1))
        );
        (await reader.ReadAsync()).Should().BeFalse();

        return range;
    }

    private async Task<bool> ReplicationSlotExistsAsync(string replicationSlotName)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_replication_slots slot
                WHERE slot.slot_name = @slotName
            );
            """;
        command.Parameters.AddWithValue("slotName", replicationSlotName);

        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task DropReplicationSlotIfExistsAsync(string replicationSlotName)
    {
        if (string.IsNullOrWhiteSpace(replicationSlotName))
        {
            return;
        }

        try
        {
            await using var connection = new NpgsqlConnection(_database.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT pg_catalog.pg_drop_replication_slot(slot.slot_name)
                FROM pg_catalog.pg_replication_slots slot
                WHERE slot.slot_name = @slotName
                  AND slot.active = false;
                """;
            command.Parameters.AddWithValue("slotName", replicationSlotName);
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException)
        {
            // The generated database cleanup owns dependent objects; slot cleanup is best-effort in teardown.
        }
        catch (NpgsqlException)
        {
            // The generated database cleanup owns dependent objects; slot cleanup is best-effort in teardown.
        }
    }

    private static async Task<string> ReadDataStoreIdentityAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "SourceIdentity"::text
            FROM dms."DataStoreIdentity"
            WHERE "DataStoreIdentitySingletonId" = 1;
            """;

        return (await command.ExecuteScalarAsync())!.ToString()!;
    }

    private static CoreCdc.CdcPostgresqlWalPosition ParsePostgresqlLsn(string value)
    {
        CoreCdc.CdcPostgresqlWalPositionResult result = CoreCdc.CdcPostgresqlProviderPosition.ParseWalLsn(
            value
        );

        result.Succeeded.Should().BeTrue();
        return result.Position!.Value;
    }

    private static void AssumePostgresqlLogicalReplicationAvailable()
    {
        using var connection = new NpgsqlConnection(Configuration.PostgresqlAdminConnectionString);
        connection.Open();

        string walLevel = ExecuteScalarText(connection, "SHOW wal_level;");
        int maxReplicationSlots = int.Parse(ExecuteScalarText(connection, "SHOW max_replication_slots;"));

        if (!string.Equals(walLevel, "logical", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Ignore(
                $"PostgreSQL logical replication tests require wal_level=logical; observed wal_level={walLevel}."
            );
        }

        if (maxReplicationSlots < 1)
        {
            Assert.Ignore(
                $"PostgreSQL logical replication tests require max_replication_slots >= 1; observed {maxReplicationSlots}."
            );
        }
    }

    private static string ExecuteScalarText(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!.ToString()!;
    }

    private static void CreateConnectorRole(string connectorRoleName)
    {
        using var connection = new NpgsqlConnection(Configuration.PostgresqlAdminConnectionString);
        connection.Open();

        DropConnectorRoleIfExists(connectorRoleName);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE ROLE {QuoteIdentifier(connectorRoleName)} WITH LOGIN REPLICATION NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;";
        command.ExecuteNonQuery();
    }

    private static void DropConnectorRoleIfExists(string? connectorRoleName)
    {
        if (string.IsNullOrWhiteSpace(connectorRoleName))
        {
            return;
        }

        try
        {
            using var connection = new NpgsqlConnection(Configuration.PostgresqlAdminConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP ROLE IF EXISTS {QuoteIdentifier(connectorRoleName)};";
            command.ExecuteNonQuery();
        }
        catch (PostgresException)
        {
            // Role cleanup is best-effort in teardown.
        }
        catch (NpgsqlException)
        {
            // Role cleanup is best-effort in teardown.
        }
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string OperationId() => "cdc-operation-postgresql-source-position";

    public enum InvalidBarrierInput
    {
        OperationId,
        SourceFingerprint,
        ConnectorName,
        TopicPrefix,
        CaptureProvider,
        SourcePartitionHash,
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Set(DateTimeOffset utcNowValue)
        {
            _utcNow = utcNowValue.ToUniversalTime();
        }
    }

    private sealed record PostgresqlRetainedWalRange(
        CoreCdc.CdcPostgresqlWalPosition Start,
        CoreCdc.CdcPostgresqlWalPosition End
    );
}
