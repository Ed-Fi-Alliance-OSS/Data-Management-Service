// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed record PostgresqlCdcProviderBarrierCaptureRequest(string ConnectionString, CdcBinding Binding)
{
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

internal sealed record PostgresqlCdcProviderBarrierCaptureResult
{
    private PostgresqlCdcProviderBarrierCaptureResult(
        string? postgresqlBarrierLsn,
        DateTimeOffset barrierCapturedAt,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        PostgresqlBarrierLsn = postgresqlBarrierLsn;
        BarrierCapturedAt = barrierCapturedAt;
        Diagnostics = diagnostics;
    }

    public string? PostgresqlBarrierLsn { get; }

    public DateTimeOffset BarrierCapturedAt { get; }

    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

    public bool Succeeded => PostgresqlBarrierLsn is not null && Diagnostics.Count == 0;

    public static PostgresqlCdcProviderBarrierCaptureResult Success(
        string postgresqlBarrierLsn,
        DateTimeOffset barrierCapturedAt
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postgresqlBarrierLsn);

        return new(postgresqlBarrierLsn, barrierCapturedAt, []);
    }

    public static PostgresqlCdcProviderBarrierCaptureResult Failure(
        DateTimeOffset barrierCapturedAt,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new(null, barrierCapturedAt, diagnostics);
    }
}

internal sealed record PostgresqlCdcProviderBarrierObservationRequest(
    string OperationId,
    CdcBinding Binding,
    DateTimeOffset ProjectionCaughtUpObservedAt,
    PostgresqlCdcProviderBarrierCaptureResult CapturedBarrier,
    CdcConnectorOffsetObservation ConnectorOffset,
    string? ExpectedConnectSourcePartitionHash = null
);

internal sealed record PostgresqlCdcSourceHistoryObservationRequest(
    string ConnectionString,
    string OperationId,
    CdcBinding Binding,
    CdcProviderSetupObservation? ProviderSetup,
    CdcConnectorOffsetObservation ConnectorOffset
)
{
    public CdcIncident? LatchedIncident { get; init; }

    public string? ExpectedConnectSourcePartitionHash { get; init; }

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

internal sealed class PostgresqlCdcSourcePositionAdapter(
    NpgsqlDataSourceCache dataSourceCache,
    IDocumentCacheProviderCommandTimeoutClassifier timeoutClassifier,
    TimeProvider timeProvider,
    ILogger<PostgresqlCdcSourcePositionAdapter> logger
) : ICdcProviderSourcePositionAdapter
{
    private const string CurrentWalLsnPath = "$.postgresqlBarrierLsn";
    private const string ProviderHistoryPath = "$.providerHistory";
    private const string PostgresqlReplicationSlotPath = "$.providerHistory.postgresqlReplicationSlot";
    private const string PostgresqlPublicationPath = "$.providerHistory.postgresqlPublication";
    private const string PostgresqlRetainedRangeStartPath = "$.providerHistory.retainedRangeStart";
    private const string PostgresqlRetainedRangeEndPath = "$.providerHistory.retainedRangeEnd";

    private static readonly string[] _expectedPublicationTables =
    [
        "dms.CdcHeartbeat",
        "dms.Document",
        "dms.DocumentCache",
    ];

    private readonly NpgsqlDataSourceCache _dataSourceCache =
        dataSourceCache ?? throw new ArgumentNullException(nameof(dataSourceCache));
    private readonly IDocumentCacheProviderCommandTimeoutClassifier _timeoutClassifier =
        timeoutClassifier ?? throw new ArgumentNullException(nameof(timeoutClassifier));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<PostgresqlCdcSourcePositionAdapter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    CdcProvider ICdcProviderSourcePositionAdapter.Provider => CdcProvider.Postgresql;

    async Task<CdcProviderBarrierCaptureResult> ICdcProviderSourcePositionAdapter.CaptureBarrierAsync(
        CdcProviderBarrierCaptureRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        PostgresqlCdcProviderBarrierCaptureResult result = await CaptureBarrierAsync(
                new(request.ConnectionString, request.Binding) { CommandTimeout = request.CommandTimeout },
                cancellationToken
            )
            .ConfigureAwait(false);

        return result.Succeeded
            ? CdcProviderBarrierCaptureResult.PostgresqlSuccess(
                result.PostgresqlBarrierLsn!,
                result.BarrierCapturedAt
            )
            : CdcProviderBarrierCaptureResult.Failure(
                CdcProvider.Postgresql,
                result.BarrierCapturedAt,
                result.Diagnostics
            );
    }

    CdcProviderBarrierObservation ICdcProviderSourcePositionAdapter.ObserveProviderBarrier(
        CdcProviderBarrierObservationRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return ObserveProviderBarrier(
            new(
                request.OperationId,
                request.Binding,
                request.ProjectionCaughtUpObservedAt,
                ToPostgresqlCaptureResult(request.CapturedBarrier),
                request.ConnectorOffset,
                request.ExpectedConnectSourcePartitionHash
            )
        );
    }

    Task<CdcSourceHistoryClassificationResult> ICdcProviderSourcePositionAdapter.ObserveSourceHistoryAsync(
        CdcSourceHistoryObservationRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        return ObserveSourceHistoryAsync(
            new(
                request.ConnectionString,
                request.OperationId,
                request.Binding,
                request.ProviderSetup,
                request.ConnectorOffset
            )
            {
                LatchedIncident = request.LatchedIncident,
                ExpectedConnectSourcePartitionHash = request.ExpectedConnectSourcePartitionHash,
                CommandTimeout = request.CommandTimeout,
            },
            cancellationToken
        );
    }

    public async Task<PostgresqlCdcProviderBarrierCaptureResult> CaptureBarrierAsync(
        PostgresqlCdcProviderBarrierCaptureRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Binding);

        DateTimeOffset capturedAt = UtcNow();
        CdcDiagnosticCollector diagnostics = new();
        ValidatePostgresqlBinding(request.Binding, "$.binding.provider", diagnostics);

        if (diagnostics.HasDiagnostics)
        {
            return PostgresqlCdcProviderBarrierCaptureResult.Failure(capturedAt, diagnostics.Diagnostics);
        }

        try
        {
            NpgsqlDataSource dataSource = _dataSourceCache.GetOrCreate(request.ConnectionString);
            await using NpgsqlConnection connection = await dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlCommand command = connection.CreateCommand();

            command.CommandText = """SELECT pg_catalog.pg_current_wal_lsn()::text;""";
            command.CommandTimeout = GetCommandTimeoutSeconds(request.CommandTimeout);

            string? lsn = (
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            )?.ToString();
            CdcPostgresqlWalPositionResult parseResult = CdcPostgresqlProviderPosition.ParseWalLsn(
                lsn,
                CurrentWalLsnPath
            );
            AddDiagnostics(diagnostics, parseResult.Diagnostics);

            return parseResult.Succeeded && lsn is not null
                ? PostgresqlCdcProviderBarrierCaptureResult.Success(lsn, capturedAt)
                : PostgresqlCdcProviderBarrierCaptureResult.Failure(capturedAt, diagnostics.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            diagnostics.LocalStateUnavailable(
                CurrentWalLsnPath,
                "CDC PostgreSQL provider barrier capture was cancelled."
            );
            return PostgresqlCdcProviderBarrierCaptureResult.Failure(UtcNow(), diagnostics.Diagnostics);
        }
        catch (Exception exception) when (_timeoutClassifier.IsProviderCommandTimeout(exception))
        {
            LogProviderObservationFailure(exception, "provider-barrier-timeout");
            diagnostics.LocalStateUnavailable(
                CurrentWalLsnPath,
                "CDC PostgreSQL provider barrier capture timed out."
            );
            return PostgresqlCdcProviderBarrierCaptureResult.Failure(UtcNow(), diagnostics.Diagnostics);
        }
        catch (Exception exception)
        {
            LogProviderObservationFailure(exception, "provider-barrier-failed");
            diagnostics.LocalStateUnavailable(
                CurrentWalLsnPath,
                "CDC PostgreSQL provider barrier capture failed."
            );
            return PostgresqlCdcProviderBarrierCaptureResult.Failure(UtcNow(), diagnostics.Diagnostics);
        }
    }

    public CdcProviderBarrierObservation ObserveProviderBarrier(
        PostgresqlCdcProviderBarrierObservationRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Binding);
        ArgumentNullException.ThrowIfNull(request.CapturedBarrier);
        ArgumentNullException.ThrowIfNull(request.ConnectorOffset);

        CdcDiagnosticCollector diagnostics = new();
        ValidatePostgresqlBinding(request.Binding, "$.binding.provider", diagnostics);
        AddDiagnostics(diagnostics, request.CapturedBarrier.Diagnostics);

        string? expectedSourcePartitionHash = ResolveExpectedPostgresqlSourcePartitionHash(
            request.Binding,
            request.ExpectedConnectSourcePartitionHash,
            diagnostics
        );
        CdcContractValidationResult connectorOffsetValidation =
            CdcConnectorOffsetObservationValidator.ValidateForBinding(
                request.ConnectorOffset,
                request.Binding,
                new(
                    request.OperationId,
                    request.Binding.ToTargetIdentity(),
                    request.Binding.PhysicalSourceFingerprint,
                    UtcNow()
                ),
                expectedSourcePartitionHash
            );
        AddDiagnostics(diagnostics, connectorOffsetValidation.Diagnostics);

        CdcProviderBarrierState barrierState = request.CapturedBarrier.Succeeded
            ? CdcProviderBarrierState.NotReached
            : CdcProviderBarrierState.Unknown;
        string? committedPosition = null;

        if (request.CapturedBarrier.PostgresqlBarrierLsn is not null)
        {
            CdcPostgresqlWalPositionResult barrierResult = CdcPostgresqlProviderPosition.ParseWalLsn(
                request.CapturedBarrier.PostgresqlBarrierLsn,
                CurrentWalLsnPath
            );
            AddDiagnostics(diagnostics, barrierResult.Diagnostics);

            if (barrierResult.Position is not null && connectorOffsetValidation.Succeeded)
            {
                CdcProviderPositionComparisonResult comparison =
                    CdcPostgresqlProviderPosition.CompareCommittedOffsetToBarrier(
                        barrierResult.Position.Value,
                        new(
                            request.ConnectorOffset.SourcePartitionMatchResult,
                            request.ConnectorOffset.IsSnapshot,
                            request.ConnectorOffset.IsNull,
                            request.ConnectorOffset.LsnProc
                        )
                    );
                AddDiagnostics(diagnostics, comparison.Diagnostics);

                if (comparison.Succeeded)
                {
                    barrierState = CdcProviderBarrierState.Reached;
                    committedPosition = comparison.CommittedPosition;
                }
            }
        }

        return new(
            CdcJsonContract.CurrentContractVersion,
            request.OperationId,
            UtcNow(),
            request.Binding.ToTargetIdentity(),
            CdcProvider.Postgresql,
            request.Binding.PhysicalSourceFingerprint,
            request.ProjectionCaughtUpObservedAt.ToUniversalTime(),
            request.CapturedBarrier.BarrierCapturedAt.ToUniversalTime(),
            request.ConnectorOffset.ObservedAt.ToUniversalTime(),
            barrierState,
            request.CapturedBarrier.PostgresqlBarrierLsn,
            null,
            null,
            null,
            committedPosition,
            [.. diagnostics.Diagnostics]
        );
    }

    public async Task<CdcSourceHistoryClassificationResult> ObserveSourceHistoryAsync(
        PostgresqlCdcSourceHistoryObservationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Binding);
        ArgumentNullException.ThrowIfNull(request.ConnectorOffset);

        CdcDiagnosticCollector diagnostics = new();
        ValidatePostgresqlBinding(request.Binding, "$.binding.provider", diagnostics);

        CdcProviderSourceHistoryEvidence? providerHistory = null;
        if (!diagnostics.HasDiagnostics)
        {
            providerHistory = await ReadProviderHistoryEvidenceAsync(
                    request.ConnectionString,
                    request.Binding,
                    request.CommandTimeout,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        return CdcSourceHistoryContinuityClassifier.Evaluate(
            new(request.OperationId, UtcNow(), UtcNow(), request.Binding)
            {
                ProviderSetup = request.ProviderSetup,
                ConnectorOffset = request.ConnectorOffset,
                ProviderHistory = providerHistory,
                LatchedIncident = request.LatchedIncident,
                ExpectedConnectSourcePartitionHash = request.ExpectedConnectSourcePartitionHash,
                Diagnostics = [.. diagnostics.Diagnostics],
            }
        );
    }

    private static PostgresqlCdcProviderBarrierCaptureResult ToPostgresqlCaptureResult(
        CdcProviderBarrierCaptureResult captureResult
    )
    {
        ArgumentNullException.ThrowIfNull(captureResult);

        if (
            captureResult.Provider == CdcProvider.Postgresql
            && captureResult.PostgresqlBarrierLsn is not null
            && captureResult.Diagnostics.Count == 0
        )
        {
            return PostgresqlCdcProviderBarrierCaptureResult.Success(
                captureResult.PostgresqlBarrierLsn,
                captureResult.BarrierCapturedAt
            );
        }

        return PostgresqlCdcProviderBarrierCaptureResult.Failure(
            captureResult.BarrierCapturedAt,
            captureResult.Provider == CdcProvider.Postgresql
                ? captureResult.Diagnostics
                :
                [
                    .. captureResult.Diagnostics,
                    new(
                        CdcDiagnosticCategory.ProviderMismatch,
                        "$.capturedBarrier.provider",
                        "CDC provider barrier capture result provider did not match PostgreSQL."
                    ),
                ]
        );
    }

    private async Task<CdcProviderSourceHistoryEvidence> ReadProviderHistoryEvidenceAsync(
        string connectionString,
        CdcBinding binding,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        if (artifactNameResult.Inventory is null)
        {
            return UnknownProviderHistory(null, artifactNameResult.Diagnostics);
        }

        CdcArtifactInventory inventory = artifactNameResult.Inventory;
        string slotName =
            inventory.PostgresqlLogicalSlotName
            ?? throw new InvalidOperationException("PostgreSQL logical slot name was not rendered.");
        string publicationName =
            inventory.PostgresqlPublicationName
            ?? throw new InvalidOperationException("PostgreSQL publication name was not rendered.");

        try
        {
            NpgsqlDataSource dataSource = _dataSourceCache.GetOrCreate(connectionString);
            await using NpgsqlConnection connection = await dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            PostgresqlReplicationSlotMetadata slot = await ReadReplicationSlotAsync(
                    connection,
                    slotName,
                    commandTimeout,
                    cancellationToken
                )
                .ConfigureAwait(false);
            PostgresqlPublicationMetadata publication = await ReadPublicationAsync(
                    connection,
                    publicationName,
                    commandTimeout,
                    cancellationToken
                )
                .ConfigureAwait(false);

            return CreateProviderHistoryEvidence(slot, publication, slotName, publicationName);
        }
        catch (OperationCanceledException)
        {
            return UnknownProviderHistory(
                slotName,
                [
                    new(
                        CdcDiagnosticCategory.LocalStateUnavailable,
                        ProviderHistoryPath,
                        "CDC PostgreSQL provider source-history observation was cancelled."
                    ),
                ]
            );
        }
        catch (Exception exception) when (_timeoutClassifier.IsProviderCommandTimeout(exception))
        {
            LogProviderObservationFailure(exception, "provider-history-timeout");
            return UnknownProviderHistory(
                slotName,
                [
                    new(
                        CdcDiagnosticCategory.LocalStateUnavailable,
                        ProviderHistoryPath,
                        "CDC PostgreSQL provider source-history observation timed out."
                    ),
                ]
            );
        }
        catch (Exception exception)
        {
            LogProviderObservationFailure(exception, "provider-history-failed");
            return UnknownProviderHistory(
                slotName,
                [
                    new(
                        CdcDiagnosticCategory.LocalStateUnavailable,
                        ProviderHistoryPath,
                        "CDC PostgreSQL provider source-history observation failed."
                    ),
                ]
            );
        }
    }

    private static CdcProviderSourceHistoryEvidence CreateProviderHistoryEvidence(
        PostgresqlReplicationSlotMetadata slot,
        PostgresqlPublicationMetadata publication,
        string slotName,
        string publicationName
    )
    {
        CdcDiagnosticCollector diagnostics = new();
        AddDiagnostics(diagnostics, slot.Diagnostics);
        AddDiagnostics(diagnostics, publication.Diagnostics);

        if (!slot.Exists)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                PostgresqlReplicationSlotPath,
                "CDC PostgreSQL binding-derived logical replication slot is missing."
            );
            return new(
                CdcProviderArtifactContinuityState.Missing,
                CdcProviderRetainedRangeState.Unknown,
                slotName,
                null,
                null,
                [CdcIncidentUnavailableFact.ProviderArtifact]
            )
            {
                Diagnostics = [.. diagnostics.Diagnostics],
            };
        }

        if (!publication.Exists)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                PostgresqlPublicationPath,
                "CDC PostgreSQL binding-derived publication is missing."
            );
            return new(
                CdcProviderArtifactContinuityState.Missing,
                CdcProviderRetainedRangeState.Unknown,
                publicationName,
                slot.RestartLsn,
                slot.ConfirmedFlushLsn,
                [CdcIncidentUnavailableFact.ProviderArtifact]
            )
            {
                Diagnostics = [.. diagnostics.Diagnostics],
            };
        }

        if (!slot.IsExactMatch || !publication.IsExactMatch)
        {
            return new(
                CdcProviderArtifactContinuityState.Recreated,
                CdcProviderRetainedRangeState.Unknown,
                !slot.IsExactMatch ? slotName : publicationName,
                slot.RestartLsn,
                slot.ConfirmedFlushLsn,
                [CdcIncidentUnavailableFact.ProviderArtifact]
            )
            {
                Diagnostics = [.. diagnostics.Diagnostics],
            };
        }

        CdcProviderRetainedRangeState retainedRangeState = ValidateRetainedRange(slot, diagnostics);
        return new(
            CdcProviderArtifactContinuityState.ExactMatch,
            retainedRangeState,
            slotName,
            slot.RestartLsn,
            slot.ConfirmedFlushLsn,
            retainedRangeState == CdcProviderRetainedRangeState.Unknown
                ? [CdcIncidentUnavailableFact.ProviderRetainedRange]
                : []
        )
        {
            Diagnostics = [.. diagnostics.Diagnostics],
        };
    }

    private static CdcProviderRetainedRangeState ValidateRetainedRange(
        PostgresqlReplicationSlotMetadata slot,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcPostgresqlWalPositionResult start = CdcPostgresqlProviderPosition.ParseWalLsn(
            slot.RestartLsn,
            PostgresqlRetainedRangeStartPath
        );
        CdcPostgresqlWalPositionResult end = CdcPostgresqlProviderPosition.ParseWalLsn(
            slot.ConfirmedFlushLsn,
            PostgresqlRetainedRangeEndPath
        );
        AddDiagnostics(diagnostics, start.Diagnostics);
        AddDiagnostics(diagnostics, end.Diagnostics);

        if (start.Position is null || end.Position is null)
        {
            return CdcProviderRetainedRangeState.Unknown;
        }

        if (
            string.Equals(slot.WalStatus, "lost", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(slot.InvalidationReason)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                PostgresqlReplicationSlotPath,
                "CDC PostgreSQL logical replication slot retained WAL is no longer continuous."
            );
            return CdcProviderRetainedRangeState.Gap;
        }

        if (start.Position.Value.CompareTo(end.Position.Value) > 0)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidOrdering,
                PostgresqlRetainedRangeStartPath,
                "CDC PostgreSQL retained range start must not be after retained range end."
            );
            return CdcProviderRetainedRangeState.Unknown;
        }

        return CdcProviderRetainedRangeState.CoversCommittedOffset;
    }

    private static CdcProviderSourceHistoryEvidence UnknownProviderHistory(
        string? providerArtifactName,
        IReadOnlyList<CdcDiagnostic> diagnostics
    ) =>
        new(
            CdcProviderArtifactContinuityState.Unknown,
            CdcProviderRetainedRangeState.Unknown,
            providerArtifactName,
            null,
            null,
            [CdcIncidentUnavailableFact.ProviderRetainedRange]
        )
        {
            Diagnostics = diagnostics,
        };

    private static async Task<PostgresqlReplicationSlotMetadata> ReadReplicationSlotAsync(
        NpgsqlConnection connection,
        string slotName,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                slot.plugin AS plugin,
                slot.slot_type AS slot_type,
                slot.database AS database_name,
                current_database() AS expected_database_name,
                slot.temporary AS temporary,
                slot.active AS active,
                COALESCE(to_jsonb(slot)->>'two_phase', 'unsupported') AS two_phase,
                slot.restart_lsn::text AS restart_lsn,
                slot.confirmed_flush_lsn::text AS confirmed_flush_lsn,
                COALESCE(to_jsonb(slot)->>'wal_status', 'unavailable') AS wal_status,
                COALESCE(to_jsonb(slot)->>'invalidation_reason', '') AS invalidation_reason
            FROM pg_catalog.pg_replication_slots slot
            WHERE slot.slot_name = @slotName;
            """;
        command.CommandTimeout = GetCommandTimeoutSeconds(commandTimeout);
        command.Parameters.AddWithValue("slotName", slotName);

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return PostgresqlReplicationSlotMetadata.Missing();
        }

        string? plugin = ReadOptionalString(reader, "plugin");
        string? slotType = ReadOptionalString(reader, "slot_type");
        string? databaseName = ReadOptionalString(reader, "database_name");
        string expectedDatabaseName = ReadRequiredString(reader, "expected_database_name");
        bool temporary = ReadRequiredBoolean(reader, "temporary");
        string twoPhase = ReadRequiredString(reader, "two_phase");
        string? restartLsn = ReadOptionalString(reader, "restart_lsn");
        string? confirmedFlushLsn = ReadOptionalString(reader, "confirmed_flush_lsn");
        string walStatus = ReadRequiredString(reader, "wal_status");
        string invalidationReason = ReadRequiredString(reader, "invalidation_reason");
        CdcDiagnosticCollector diagnostics = new();

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                PostgresqlReplicationSlotPath,
                "CDC PostgreSQL logical replication slot metadata returned multiple rows."
            );
        }

        bool exact =
            string.Equals(plugin, "pgoutput", StringComparison.Ordinal)
            && string.Equals(slotType, "logical", StringComparison.Ordinal)
            && string.Equals(databaseName, expectedDatabaseName, StringComparison.Ordinal)
            && !temporary
            && (
                string.Equals(twoPhase, "unsupported", StringComparison.Ordinal)
                || string.Equals(twoPhase, "false", StringComparison.OrdinalIgnoreCase)
            );

        if (!exact)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                PostgresqlReplicationSlotPath,
                "CDC PostgreSQL logical replication slot must be a permanent pgoutput slot for the current database."
            );
        }

        return new(
            true,
            exact,
            restartLsn,
            confirmedFlushLsn,
            walStatus,
            invalidationReason,
            [.. diagnostics.Diagnostics]
        );
    }

    private static async Task<PostgresqlPublicationMetadata> ReadPublicationAsync(
        NpgsqlConnection connection,
        string publicationName,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        PostgresqlPublicationProperties? properties = await ReadPublicationPropertiesAsync(
                connection,
                publicationName,
                commandTimeout,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (properties is null)
        {
            return PostgresqlPublicationMetadata.Missing();
        }

        IReadOnlyList<PostgresqlPublicationTableMetadata> tables = await ReadPublicationTablesAsync(
                connection,
                publicationName,
                commandTimeout,
                cancellationToken
            )
            .ConfigureAwait(false);
        CdcDiagnosticCollector diagnostics = new();

        string[] observedTables = tables
            .Select(table => table.TableName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expectedTables = _expectedPublicationTables.Order(StringComparer.Ordinal).ToArray();
        bool tableSetMatches = observedTables.SequenceEqual(expectedTables, StringComparer.Ordinal);
        bool tableShapesMatch = tables.All(table => table.PublishesAllColumns && table.RowFilterAbsent);
        bool exact =
            properties.PublishesInsert
            && properties.PublishesUpdate
            && properties.PublishesDelete
            && !properties.PublishesTruncate
            && !properties.PublishesAllTables
            && !properties.PublishViaPartitionRoot
            && tableSetMatches
            && tableShapesMatch;

        if (!exact)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                PostgresqlPublicationPath,
                "CDC PostgreSQL publication must capture exactly dms.CdcHeartbeat, dms.Document, and dms.DocumentCache with insert/update/delete changes."
            );
        }

        return new(true, exact, [.. diagnostics.Diagnostics]);
    }

    private static async Task<PostgresqlPublicationProperties?> ReadPublicationPropertiesAsync(
        NpgsqlConnection connection,
        string publicationName,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                publication.pubinsert,
                publication.pubupdate,
                publication.pubdelete,
                publication.pubtruncate,
                publication.puballtables,
                COALESCE(to_jsonb(publication)->>'pubviaroot', 'false') AS publish_via_partition_root
            FROM pg_catalog.pg_publication publication
            WHERE publication.pubname = @publicationName;
            """;
        command.CommandTimeout = GetCommandTimeoutSeconds(commandTimeout);
        command.Parameters.AddWithValue("publicationName", publicationName);

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new(
            reader.GetBoolean(0),
            reader.GetBoolean(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4),
            string.Equals(reader.GetString(5), "true", StringComparison.OrdinalIgnoreCase)
        );
    }

    private static async Task<IReadOnlyList<PostgresqlPublicationTableMetadata>> ReadPublicationTablesAsync(
        NpgsqlConnection connection,
        string publicationName,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                namespace_info.nspname || '.' || table_info.relname AS table_name,
                publication_table.prattrs IS NULL AS publishes_all_columns,
                publication_table.prqual IS NULL AS row_filter_absent
            FROM pg_catalog.pg_publication_rel publication_table
            INNER JOIN pg_catalog.pg_publication publication
                ON publication.oid = publication_table.prpubid
            INNER JOIN pg_catalog.pg_class table_info
                ON table_info.oid = publication_table.prrelid
            INNER JOIN pg_catalog.pg_namespace namespace_info
                ON namespace_info.oid = table_info.relnamespace
            WHERE publication.pubname = @publicationName
            ORDER BY namespace_info.nspname, table_info.relname;
            """;
        command.CommandTimeout = GetCommandTimeoutSeconds(commandTimeout);
        command.Parameters.AddWithValue("publicationName", publicationName);

        List<PostgresqlPublicationTableMetadata> tables = [];
        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(new(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        }

        return tables;
    }

    private static void ValidatePostgresqlBinding(
        CdcBinding binding,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (binding.Provider != CdcProvider.Postgresql)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ProviderMismatch,
                path,
                "CDC PostgreSQL source-position adapter requires a PostgreSQL binding."
            );
        }
    }

    private static string? ResolveExpectedPostgresqlSourcePartitionHash(
        CdcBinding binding,
        string? expectedConnectSourcePartitionHash,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (expectedConnectSourcePartitionHash is not null)
        {
            return expectedConnectSourcePartitionHash;
        }

        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        AddDiagnostics(diagnostics, artifactNameResult.Diagnostics);
        if (artifactNameResult.Inventory is null)
        {
            return null;
        }

        CdcSourcePartitionHashResult sourcePartitionHash = CdcSourcePartitionHashCalculator.ComputePostgresql(
            artifactNameResult.Inventory.TopicPrefix
        );
        AddDiagnostics(diagnostics, sourcePartitionHash.Diagnostics);

        return sourcePartitionHash.Hash;
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static int GetCommandTimeoutSeconds(TimeSpan timeout)
    {
        double timeoutSeconds = Math.Ceiling(timeout.TotalSeconds);
        if (timeoutSeconds < 1)
        {
            return 1;
        }

        return timeoutSeconds > int.MaxValue ? int.MaxValue : (int)timeoutSeconds;
    }

    private void LogProviderObservationFailure(Exception exception, string outcome)
    {
        _logger.LogDebug(
            "PostgreSQL CDC source-position observation failed with outcome {Outcome}; exception type {ExceptionType}",
            outcome,
            exception.GetType().Name
        );
    }

    private static string? ReadOptionalString(NpgsqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string ReadRequiredString(NpgsqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.GetString(ordinal);
    }

    private static bool ReadRequiredBoolean(NpgsqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.GetBoolean(ordinal);
    }

    private static void AddDiagnostics(
        CdcDiagnosticCollector collector,
        IReadOnlyList<CdcDiagnostic>? diagnostics
    )
    {
        if (diagnostics is null)
        {
            return;
        }

        foreach (CdcDiagnostic diagnostic in diagnostics.Where(diagnostic => diagnostic is not null))
        {
            collector.Add(diagnostic);
        }
    }

    private sealed record PostgresqlReplicationSlotMetadata(
        bool Exists,
        bool IsExactMatch,
        string? RestartLsn,
        string? ConfirmedFlushLsn,
        string WalStatus,
        string InvalidationReason,
        IReadOnlyList<CdcDiagnostic> Diagnostics
    )
    {
        public static PostgresqlReplicationSlotMetadata Missing() =>
            new(false, false, null, null, "unavailable", "", []);
    }

    private sealed record PostgresqlPublicationMetadata(
        bool Exists,
        bool IsExactMatch,
        IReadOnlyList<CdcDiagnostic> Diagnostics
    )
    {
        public static PostgresqlPublicationMetadata Missing() => new(false, false, []);
    }

    private sealed record PostgresqlPublicationProperties(
        bool PublishesInsert,
        bool PublishesUpdate,
        bool PublishesDelete,
        bool PublishesTruncate,
        bool PublishesAllTables,
        bool PublishViaPartitionRoot
    );

    private sealed record PostgresqlPublicationTableMetadata(
        string TableName,
        bool PublishesAllColumns,
        bool RowFilterAbsent
    );
}
