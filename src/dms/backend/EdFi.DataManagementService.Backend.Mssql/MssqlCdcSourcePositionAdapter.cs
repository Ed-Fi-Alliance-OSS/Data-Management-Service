// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed record MssqlCdcProviderBarrierCaptureRequest(string ConnectionString, CdcBinding Binding)
{
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan CaptureWaitTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

internal sealed record MssqlCdcProviderBarrierCaptureResult
{
    private MssqlCdcProviderBarrierCaptureResult(
        string? sqlServerCommitLsn,
        string? sqlServerChangeLsn,
        long? sqlServerEventSerialNo,
        DateTimeOffset barrierCapturedAt,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        SqlServerCommitLsn = sqlServerCommitLsn;
        SqlServerChangeLsn = sqlServerChangeLsn;
        SqlServerEventSerialNo = sqlServerEventSerialNo;
        BarrierCapturedAt = barrierCapturedAt;
        Diagnostics = diagnostics;
    }

    public string? SqlServerCommitLsn { get; }

    public string? SqlServerChangeLsn { get; }

    public long? SqlServerEventSerialNo { get; }

    public DateTimeOffset BarrierCapturedAt { get; }

    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

    public bool Succeeded =>
        SqlServerCommitLsn is not null
        && SqlServerChangeLsn is not null
        && SqlServerEventSerialNo is not null
        && Diagnostics.Count == 0;

    public static MssqlCdcProviderBarrierCaptureResult Success(
        string sqlServerCommitLsn,
        string sqlServerChangeLsn,
        long sqlServerEventSerialNo,
        DateTimeOffset barrierCapturedAt
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlServerCommitLsn);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlServerChangeLsn);

        return new(sqlServerCommitLsn, sqlServerChangeLsn, sqlServerEventSerialNo, barrierCapturedAt, []);
    }

    public static MssqlCdcProviderBarrierCaptureResult Failure(
        DateTimeOffset barrierCapturedAt,
        IReadOnlyList<CdcDiagnostic> diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new(null, null, null, barrierCapturedAt, diagnostics);
    }
}

internal sealed record MssqlCdcProviderBarrierObservationRequest(
    string OperationId,
    CdcBinding Binding,
    DateTimeOffset ProjectionCaughtUpObservedAt,
    MssqlCdcProviderBarrierCaptureResult CapturedBarrier,
    CdcConnectorOffsetObservation ConnectorOffset,
    string? ExpectedConnectSourcePartitionHash = null
);

internal sealed record MssqlCdcSourceHistoryObservationRequest(
    string ConnectionString,
    string OperationId,
    CdcBinding Binding,
    CdcProviderSetupObservation? ProviderSetup,
    CdcConnectorOffsetObservation ConnectorOffset,
    CdcSqlServerSchemaHistoryEvidence? SchemaHistory
)
{
    public CdcIncident? LatchedIncident { get; init; }

    public string? ExpectedConnectSourcePartitionHash { get; init; }

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

internal sealed class MssqlCdcSourcePositionAdapter(
    IDocumentCacheProviderCommandTimeoutClassifier timeoutClassifier,
    TimeProvider timeProvider,
    ILogger<MssqlCdcSourcePositionAdapter> logger
)
{
    private const string HeartbeatSequencePath = "$.sqlServerHeartbeatSequence";
    private const string ProviderHistoryPath = "$.providerHistory";
    private const string SqlServerCaptureInstancesPath = "$.providerHistory.sqlServerCaptureInstances";
    private const string SqlServerJobsPath = "$.providerHistory.sqlServerJobs";
    private const string SqlServerRetainedRangeStartPath = "$.providerHistory.retainedRangeStart";
    private const string SqlServerRetainedRangeEndPath = "$.providerHistory.retainedRangeEnd";
    private const long HeartbeatAfterImageEventSerialNo = 2;

    private static readonly IReadOnlyList<string> _documentColumns =
    [
        "DocumentId",
        "DocumentUuid",
        "ResourceKeyId",
        "CreatedByOwnershipTokenId",
        "ContentVersion",
        "IdentityVersion",
        "ContentLastModifiedAt",
        "IdentityLastModifiedAt",
        "CreatedAt",
    ];

    private static readonly IReadOnlyList<string> _documentCacheColumns =
    [
        "DocumentId",
        "DocumentUuid",
        "ProjectName",
        "ResourceName",
        "ResourceVersion",
        "ContentVersion",
        "StreamEtag",
        "LastModifiedAt",
        "DocumentJson",
        "ComputedAt",
    ];

    private static readonly IReadOnlyList<string> _heartbeatColumns =
    [
        "HeartbeatId",
        "HeartbeatSequence",
        "HeartbeatAt",
    ];

    private readonly IDocumentCacheProviderCommandTimeoutClassifier _timeoutClassifier =
        timeoutClassifier ?? throw new ArgumentNullException(nameof(timeoutClassifier));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<MssqlCdcSourcePositionAdapter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<MssqlCdcProviderBarrierCaptureResult> CaptureBarrierAsync(
        MssqlCdcProviderBarrierCaptureRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Binding);

        DateTimeOffset capturedAt = UtcNow();
        CdcDiagnosticCollector diagnostics = new();
        ValidateSqlServerBinding(request.Binding, "$.binding.provider", diagnostics);

        string? heartbeatCaptureInstanceName = ResolveHeartbeatCaptureInstanceName(
            request.Binding,
            diagnostics
        );
        if (diagnostics.HasDiagnostics || heartbeatCaptureInstanceName is null)
        {
            return MssqlCdcProviderBarrierCaptureResult.Failure(capturedAt, diagnostics.Diagnostics);
        }

        try
        {
            await using SqlConnection connection = new(request.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            long heartbeatSequence = await ReadHeartbeatSequenceAsync(
                    connection,
                    request.CommandTimeout,
                    cancellationToken
                )
                .ConfigureAwait(false);
            DateTimeOffset deadline = UtcNow().Add(request.CaptureWaitTimeout);

            while (UtcNow() <= deadline)
            {
                SqlServerHeartbeatAfterImage? afterImage = await ReadHeartbeatAfterImageAsync(
                        connection,
                        heartbeatCaptureInstanceName,
                        heartbeatSequence,
                        request.CommandTimeout,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                if (afterImage is not null)
                {
                    CdcSqlServerLsnResult commitLsn = CdcSqlServerProviderPositionParser.NormalizeTenByteLsn(
                        afterImage.StartLsn,
                        "$.sqlServerCommitLsn"
                    );
                    CdcSqlServerLsnResult changeLsn = CdcSqlServerProviderPositionParser.NormalizeTenByteLsn(
                        afterImage.SeqVal,
                        "$.sqlServerChangeLsn"
                    );
                    AddDiagnostics(diagnostics, commitLsn.Diagnostics);
                    AddDiagnostics(diagnostics, changeLsn.Diagnostics);

                    if (commitLsn.Lsn is not null && changeLsn.Lsn is not null && !diagnostics.HasDiagnostics)
                    {
                        return MssqlCdcProviderBarrierCaptureResult.Success(
                            commitLsn.Lsn.Value.ToString(),
                            changeLsn.Lsn.Value.ToString(),
                            HeartbeatAfterImageEventSerialNo,
                            UtcNow()
                        );
                    }

                    return MssqlCdcProviderBarrierCaptureResult.Failure(UtcNow(), diagnostics.Diagnostics);
                }

                await Task.Delay(request.PollInterval, cancellationToken).ConfigureAwait(false);
            }

            diagnostics.LocalStateUnavailable(
                HeartbeatSequencePath,
                "CDC SQL Server provider barrier capture timed out waiting for heartbeat after-image."
            );
            return MssqlCdcProviderBarrierCaptureResult.Failure(UtcNow(), diagnostics.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            diagnostics.LocalStateUnavailable(
                HeartbeatSequencePath,
                "CDC SQL Server provider barrier capture was cancelled."
            );
            return MssqlCdcProviderBarrierCaptureResult.Failure(UtcNow(), diagnostics.Diagnostics);
        }
        catch (Exception exception) when (_timeoutClassifier.IsProviderCommandTimeout(exception))
        {
            LogProviderObservationFailure(exception, "provider-barrier-timeout");
            diagnostics.LocalStateUnavailable(
                HeartbeatSequencePath,
                "CDC SQL Server provider barrier capture timed out."
            );
            return MssqlCdcProviderBarrierCaptureResult.Failure(UtcNow(), diagnostics.Diagnostics);
        }
        catch (Exception exception)
        {
            LogProviderObservationFailure(exception, "provider-barrier-failed");
            diagnostics.LocalStateUnavailable(
                HeartbeatSequencePath,
                "CDC SQL Server provider barrier capture failed."
            );
            return MssqlCdcProviderBarrierCaptureResult.Failure(UtcNow(), diagnostics.Diagnostics);
        }
    }

    public CdcProviderBarrierObservation ObserveProviderBarrier(
        MssqlCdcProviderBarrierObservationRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Binding);
        ArgumentNullException.ThrowIfNull(request.CapturedBarrier);
        ArgumentNullException.ThrowIfNull(request.ConnectorOffset);

        CdcDiagnosticCollector diagnostics = new();
        ValidateSqlServerBinding(request.Binding, "$.binding.provider", diagnostics);
        AddDiagnostics(diagnostics, request.CapturedBarrier.Diagnostics);

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
                request.ExpectedConnectSourcePartitionHash
            );
        AddDiagnostics(diagnostics, connectorOffsetValidation.Diagnostics);

        CdcProviderBarrierState barrierState = request.CapturedBarrier.Succeeded
            ? CdcProviderBarrierState.NotReached
            : CdcProviderBarrierState.Unknown;
        string? committedPosition = null;

        if (
            request.CapturedBarrier.SqlServerCommitLsn is not null
            && request.CapturedBarrier.SqlServerChangeLsn is not null
        )
        {
            CdcSqlServerLsnResult commitLsn = CdcSqlServerProviderPositionParser.ParseLsn(
                request.CapturedBarrier.SqlServerCommitLsn,
                "$.sqlServerCommitLsn"
            );
            CdcSqlServerLsnResult changeLsn = CdcSqlServerProviderPositionParser.ParseLsn(
                request.CapturedBarrier.SqlServerChangeLsn,
                "$.sqlServerChangeLsn"
            );
            AddDiagnostics(diagnostics, commitLsn.Diagnostics);
            AddDiagnostics(diagnostics, changeLsn.Diagnostics);

            if (commitLsn.Lsn is not null && changeLsn.Lsn is not null && connectorOffsetValidation.Succeeded)
            {
                CdcProviderPositionComparisonResult comparison =
                    CdcSqlServerProviderPositionParser.CompareCommittedOffsetToBarrier(
                        CdcSqlServerProviderPosition.HeartbeatAfterImage(
                            commitLsn.Lsn.Value,
                            changeLsn.Lsn.Value
                        ),
                        new(
                            request.ConnectorOffset.SourcePartitionMatchResult,
                            request.ConnectorOffset.IsSnapshot,
                            request.ConnectorOffset.IsNull,
                            request.ConnectorOffset.CommitLsn,
                            request.ConnectorOffset.ChangeLsn,
                            request.ConnectorOffset.EventSerialNo
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
            CdcProvider.SqlServer,
            request.Binding.PhysicalSourceFingerprint,
            request.ProjectionCaughtUpObservedAt.ToUniversalTime(),
            request.CapturedBarrier.BarrierCapturedAt.ToUniversalTime(),
            request.ConnectorOffset.ObservedAt.ToUniversalTime(),
            barrierState,
            null,
            request.CapturedBarrier.SqlServerCommitLsn,
            request.CapturedBarrier.SqlServerChangeLsn,
            request.CapturedBarrier.SqlServerEventSerialNo,
            committedPosition,
            [.. diagnostics.Diagnostics]
        );
    }

    public async Task<CdcSourceHistoryClassificationResult> ObserveSourceHistoryAsync(
        MssqlCdcSourceHistoryObservationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Binding);
        ArgumentNullException.ThrowIfNull(request.ConnectorOffset);

        CdcDiagnosticCollector diagnostics = new();
        ValidateSqlServerBinding(request.Binding, "$.binding.provider", diagnostics);

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
                SqlServerSchemaHistory = request.SchemaHistory,
                LatchedIncident = request.LatchedIncident,
                ExpectedConnectSourcePartitionHash = request.ExpectedConnectSourcePartitionHash,
                Diagnostics = [.. diagnostics.Diagnostics],
            }
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
        string heartbeatCaptureName =
            inventory.SqlServerCaptureInstanceCdcHeartbeatName
            ?? throw new InvalidOperationException(
                "SQL Server heartbeat capture instance name was not rendered."
            );

        try
        {
            await using SqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            SqlServerProviderHistoryMetadata metadata = await ReadProviderHistoryMetadataAsync(
                    connection,
                    inventory,
                    commandTimeout,
                    cancellationToken
                )
                .ConfigureAwait(false);

            return CreateProviderHistoryEvidence(metadata, inventory);
        }
        catch (OperationCanceledException)
        {
            return UnknownProviderHistory(
                heartbeatCaptureName,
                [
                    new(
                        CdcDiagnosticCategory.LocalStateUnavailable,
                        ProviderHistoryPath,
                        "CDC SQL Server provider source-history observation was cancelled."
                    ),
                ]
            );
        }
        catch (Exception exception) when (_timeoutClassifier.IsProviderCommandTimeout(exception))
        {
            LogProviderObservationFailure(exception, "provider-history-timeout");
            return UnknownProviderHistory(
                heartbeatCaptureName,
                [
                    new(
                        CdcDiagnosticCategory.LocalStateUnavailable,
                        ProviderHistoryPath,
                        "CDC SQL Server provider source-history observation timed out."
                    ),
                ]
            );
        }
        catch (Exception exception)
        {
            LogProviderObservationFailure(exception, "provider-history-failed");
            return UnknownProviderHistory(
                heartbeatCaptureName,
                [
                    new(
                        CdcDiagnosticCategory.LocalStateUnavailable,
                        ProviderHistoryPath,
                        "CDC SQL Server provider source-history observation failed."
                    ),
                ]
            );
        }
    }

    private static CdcProviderSourceHistoryEvidence CreateProviderHistoryEvidence(
        SqlServerProviderHistoryMetadata metadata,
        CdcArtifactInventory inventory
    )
    {
        CdcDiagnosticCollector diagnostics = new();
        AddDiagnostics(diagnostics, metadata.Diagnostics);

        string heartbeatCaptureName =
            inventory.SqlServerCaptureInstanceCdcHeartbeatName
            ?? throw new InvalidOperationException(
                "SQL Server heartbeat capture instance name was not rendered."
            );

        if (!metadata.DatabaseCdcEnabled)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                ProviderHistoryPath,
                "CDC SQL Server database CDC is disabled."
            );
            return new(
                CdcProviderArtifactContinuityState.Missing,
                CdcProviderRetainedRangeState.Unknown,
                heartbeatCaptureName,
                null,
                null,
                [CdcIncidentUnavailableFact.ProviderArtifact]
            )
            {
                Diagnostics = [.. diagnostics.Diagnostics],
            };
        }

        SqlServerCaptureInstanceMetadata? missingCapture = metadata.ExpectedCaptures.FirstOrDefault(capture =>
            !capture.Exists
        );
        if (missingCapture is not null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                SqlServerCaptureInstancesPath,
                "CDC SQL Server binding-derived capture instance is missing."
            );
            return new(
                CdcProviderArtifactContinuityState.Missing,
                CdcProviderRetainedRangeState.Unknown,
                missingCapture.CaptureInstanceName,
                null,
                null,
                [CdcIncidentUnavailableFact.ProviderArtifact]
            )
            {
                Diagnostics = [.. diagnostics.Diagnostics],
            };
        }

        SqlServerCaptureInstanceMetadata? mismatchedCapture = metadata.ExpectedCaptures.FirstOrDefault(
            capture => !capture.IsExactMatch
        );
        if (mismatchedCapture is not null)
        {
            return new(
                CdcProviderArtifactContinuityState.Recreated,
                CdcProviderRetainedRangeState.Unknown,
                mismatchedCapture.CaptureInstanceName,
                mismatchedCapture.RetainedMinLsn,
                metadata.RetainedMaxLsn,
                [CdcIncidentUnavailableFact.ProviderArtifact]
            )
            {
                Diagnostics = [.. diagnostics.Diagnostics],
            };
        }

        CdcProviderRetainedRangeState retainedRangeState = ValidateRetainedRange(metadata, diagnostics);
        if (!metadata.CdcJobsExactMatch)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                SqlServerJobsPath,
                "CDC SQL Server capture and cleanup job metadata must both be available."
            );
            retainedRangeState = CdcProviderRetainedRangeState.Unknown;
        }

        return new(
            CdcProviderArtifactContinuityState.ExactMatch,
            retainedRangeState,
            heartbeatCaptureName,
            metadata.RetainedRangeStart,
            metadata.RetainedMaxLsn,
            retainedRangeState == CdcProviderRetainedRangeState.Unknown
                ? [CdcIncidentUnavailableFact.ProviderRetainedRange]
                : []
        )
        {
            Diagnostics = [.. diagnostics.Diagnostics],
        };
    }

    private static CdcProviderRetainedRangeState ValidateRetainedRange(
        SqlServerProviderHistoryMetadata metadata,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (string.IsNullOrWhiteSpace(metadata.RetainedRangeStart))
        {
            diagnostics.MissingRequiredField(SqlServerRetainedRangeStartPath, "retainedRangeStart");
        }

        if (string.IsNullOrWhiteSpace(metadata.RetainedMaxLsn))
        {
            diagnostics.MissingRequiredField(SqlServerRetainedRangeEndPath, "retainedRangeEnd");
        }

        CdcSqlServerLsnResult start = CdcSqlServerProviderPositionParser.ParseLsn(
            metadata.RetainedRangeStart,
            SqlServerRetainedRangeStartPath
        );
        CdcSqlServerLsnResult end = CdcSqlServerProviderPositionParser.ParseLsn(
            metadata.RetainedMaxLsn,
            SqlServerRetainedRangeEndPath
        );
        AddDiagnostics(diagnostics, start.Diagnostics);
        AddDiagnostics(diagnostics, end.Diagnostics);

        if (start.Lsn is null || end.Lsn is null)
        {
            return CdcProviderRetainedRangeState.Unknown;
        }

        if (start.Lsn.Value.CompareTo(end.Lsn.Value) > 0)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidOrdering,
                SqlServerRetainedRangeStartPath,
                "CDC SQL Server retained range start must not be after retained range end."
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

    private static async Task<SqlServerProviderHistoryMetadata> ReadProviderHistoryMetadataAsync(
        SqlConnection connection,
        CdcArtifactInventory inventory,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        bool databaseCdcEnabled = await ReadDatabaseCdcEnabledAsync(
                connection,
                commandTimeout,
                cancellationToken
            )
            .ConfigureAwait(false);
        bool cdcJobsExactMatch =
            databaseCdcEnabled
            && await ReadCdcJobsExactMatchAsync(connection, commandTimeout, cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyList<SqlServerCaptureInstanceMetadata> expectedCaptures = databaseCdcEnabled
            ? await ReadExpectedCaptureInstancesAsync(
                    connection,
                    inventory,
                    commandTimeout,
                    cancellationToken
                )
                .ConfigureAwait(false)
            : ExpectedMissingCaptures(inventory);

        return CreateProviderHistoryMetadata(databaseCdcEnabled, cdcJobsExactMatch, expectedCaptures);
    }

    private static SqlServerProviderHistoryMetadata CreateProviderHistoryMetadata(
        bool databaseCdcEnabled,
        bool cdcJobsExactMatch,
        IReadOnlyList<SqlServerCaptureInstanceMetadata> expectedCaptures
    )
    {
        CdcDiagnosticCollector diagnostics = new();
        foreach (
            SqlServerCaptureInstanceMetadata capture in expectedCaptures.Where(capture =>
                capture.Exists && !capture.IsExactMatch
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidObservation,
                SqlServerCaptureInstancesPath,
                "CDC SQL Server capture instance metadata must exactly match the binding-derived artifact."
            );
        }

        string? retainedRangeStart = null;
        CdcSqlServerLsn? parsedRetainedRangeStart = null;
        string? retainedMaxLsn = expectedCaptures
            .Select(capture => capture.RetainedMaxLsn)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        foreach (
            SqlServerCaptureInstanceMetadata capture in expectedCaptures.Where(capture =>
                !string.IsNullOrWhiteSpace(capture.RetainedMinLsn)
            )
        )
        {
            CdcSqlServerLsnResult minLsn = CdcSqlServerProviderPositionParser.ParseLsn(
                capture.RetainedMinLsn,
                SqlServerRetainedRangeStartPath
            );
            AddDiagnostics(diagnostics, minLsn.Diagnostics);
            if (
                minLsn.Lsn is not null
                && (
                    parsedRetainedRangeStart is null
                    || minLsn.Lsn.Value.CompareTo(parsedRetainedRangeStart.Value) > 0
                )
            )
            {
                parsedRetainedRangeStart = minLsn.Lsn.Value;
                retainedRangeStart = minLsn.Lsn.Value.ToString();
            }
        }

        if (!string.IsNullOrWhiteSpace(retainedMaxLsn))
        {
            CdcSqlServerLsnResult maxLsn = CdcSqlServerProviderPositionParser.ParseLsn(
                retainedMaxLsn,
                SqlServerRetainedRangeEndPath
            );
            AddDiagnostics(diagnostics, maxLsn.Diagnostics);
            retainedMaxLsn = maxLsn.Lsn?.ToString() ?? retainedMaxLsn;
        }

        return new(
            databaseCdcEnabled,
            cdcJobsExactMatch,
            expectedCaptures,
            retainedRangeStart,
            retainedMaxLsn,
            [.. diagnostics.Diagnostics]
        );
    }

    private static async Task<bool> ReadDatabaseCdcEnabledAsync(
        SqlConnection connection,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONVERT(bit, [is_cdc_enabled])
            FROM sys.databases
            WHERE [name] = DB_NAME();
            """;
        command.CommandTimeout = GetCommandTimeoutSeconds(commandTimeout);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is not null && value != DBNull.Value && Convert.ToBoolean(value);
    }

    private static async Task<bool> ReadCdcJobsExactMatchAsync(
        SqlConnection connection,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            EXEC sys.sp_cdc_help_jobs;
            """;
        command.CommandTimeout = GetCommandTimeoutSeconds(commandTimeout);

        HashSet<string> jobTypes = new(StringComparer.OrdinalIgnoreCase);
        await using SqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        int jobTypeOrdinal = reader.GetOrdinal("job_type");
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.IsDBNullAsync(jobTypeOrdinal, cancellationToken).ConfigureAwait(false))
            {
                jobTypes.Add(reader.GetString(jobTypeOrdinal));
            }
        }

        return jobTypes.SetEquals(["capture", "cleanup"]);
    }

    private static async Task<
        IReadOnlyList<SqlServerCaptureInstanceMetadata>
    > ReadExpectedCaptureInstancesAsync(
        SqlConnection connection,
        CdcArtifactInventory inventory,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        string documentCaptureName =
            inventory.SqlServerCaptureInstanceDocumentName
            ?? throw new InvalidOperationException(
                "SQL Server document capture instance name was not rendered."
            );
        string documentCacheCaptureName =
            inventory.SqlServerCaptureInstanceDocumentCacheName
            ?? throw new InvalidOperationException(
                "SQL Server document cache capture instance name was not rendered."
            );
        string heartbeatCaptureName =
            inventory.SqlServerCaptureInstanceCdcHeartbeatName
            ?? throw new InvalidOperationException(
                "SQL Server heartbeat capture instance name was not rendered."
            );

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                capture_info.capture_instance,
                source_schema.[name] AS source_schema,
                source_table.[name] AS source_name,
                COALESCE(capture_info.role_name, N'') AS role_name,
                CONVERT(bit, capture_info.supports_net_changes) AS supports_net_changes,
                CONVERT(bit, CASE WHEN capture_info.has_drop_pending = 1 THEN 1 ELSE 0 END) AS has_drop_pending,
                COALESCE(capture_info.filegroup_name, N'') AS filegroup_name,
                CONVERT(bit, capture_info.partition_switch) AS partition_switch,
                CONVERT(bit, CASE WHEN EXISTS (
                    SELECT 1
                    FROM sys.indexes source_index
                    INNER JOIN sys.partition_schemes partition_scheme
                        ON partition_scheme.data_space_id = source_index.data_space_id
                    WHERE source_index.object_id = source_table.object_id
                    AND source_index.index_id IN (0, 1)
                ) THEN 1 ELSE 0 END) AS source_is_partitioned,
                COALESCE(sys.fn_varbintohexstr(sys.fn_cdc_get_min_lsn(capture_info.capture_instance)), N'') AS retained_min_lsn,
                COALESCE(sys.fn_varbintohexstr(sys.fn_cdc_get_max_lsn()), N'') AS retained_max_lsn,
                CONVERT(bit, CASE
                    WHEN OBJECT_ID(N'cdc.fn_cdc_get_all_changes_' + capture_info.capture_instance) IS NOT NULL
                        THEN 1
                    ELSE 0
                END) AS all_changes_function_present,
                captured_column.column_name,
                captured_column.column_ordinal
            FROM cdc.change_tables capture_info
            INNER JOIN sys.tables source_table
                ON source_table.[object_id] = capture_info.source_object_id
            INNER JOIN sys.schemas source_schema
                ON source_schema.[schema_id] = source_table.[schema_id]
            INNER JOIN cdc.captured_columns captured_column
                ON captured_column.[object_id] = capture_info.[object_id]
            WHERE capture_info.capture_instance IN (
                @documentCaptureName,
                @documentCacheCaptureName,
                @heartbeatCaptureName
            )
            ORDER BY capture_info.capture_instance, captured_column.column_ordinal;
            """;
        command.CommandTimeout = GetCommandTimeoutSeconds(commandTimeout);
        command.Parameters.AddWithValue("@documentCaptureName", documentCaptureName);
        command.Parameters.AddWithValue("@documentCacheCaptureName", documentCacheCaptureName);
        command.Parameters.AddWithValue("@heartbeatCaptureName", heartbeatCaptureName);

        Dictionary<string, List<SqlServerCaptureInstanceRow>> rowsByCapture = new(StringComparer.Ordinal);
        await using SqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string captureInstance = ReadRequiredString(reader, "capture_instance");
            if (!rowsByCapture.TryGetValue(captureInstance, out List<SqlServerCaptureInstanceRow>? rows))
            {
                rows = [];
                rowsByCapture[captureInstance] = rows;
            }

            rows.Add(
                new(
                    captureInstance,
                    ReadRequiredString(reader, "source_schema"),
                    ReadRequiredString(reader, "source_name"),
                    ReadRequiredString(reader, "role_name"),
                    ReadRequiredBoolean(reader, "supports_net_changes"),
                    ReadRequiredBoolean(reader, "has_drop_pending"),
                    ReadRequiredString(reader, "filegroup_name"),
                    ReadRequiredBoolean(reader, "partition_switch"),
                    ReadRequiredBoolean(reader, "source_is_partitioned"),
                    ReadRequiredString(reader, "retained_min_lsn"),
                    ReadRequiredString(reader, "retained_max_lsn"),
                    ReadRequiredBoolean(reader, "all_changes_function_present"),
                    ReadRequiredString(reader, "column_name"),
                    ReadRequiredInt32(reader, "column_ordinal")
                )
            );
        }

        return
        [
            CreateCaptureMetadata(
                documentCaptureName,
                "dms",
                "Document",
                inventory.SqlServerCdcGatingRoleName!,
                _documentColumns,
                rowsByCapture.GetValueOrDefault(documentCaptureName),
                requireAllChangesFunction: false
            ),
            CreateCaptureMetadata(
                documentCacheCaptureName,
                "dms",
                "DocumentCache",
                inventory.SqlServerCdcGatingRoleName!,
                _documentCacheColumns,
                rowsByCapture.GetValueOrDefault(documentCacheCaptureName),
                requireAllChangesFunction: false
            ),
            CreateCaptureMetadata(
                heartbeatCaptureName,
                "dms",
                "CdcHeartbeat",
                inventory.SqlServerCdcGatingRoleName!,
                _heartbeatColumns,
                rowsByCapture.GetValueOrDefault(heartbeatCaptureName),
                requireAllChangesFunction: true
            ),
        ];
    }

    private static IReadOnlyList<SqlServerCaptureInstanceMetadata> ExpectedMissingCaptures(
        CdcArtifactInventory inventory
    ) =>
        [
            SqlServerCaptureInstanceMetadata.Missing(inventory.SqlServerCaptureInstanceDocumentName!),
            SqlServerCaptureInstanceMetadata.Missing(inventory.SqlServerCaptureInstanceDocumentCacheName!),
            SqlServerCaptureInstanceMetadata.Missing(inventory.SqlServerCaptureInstanceCdcHeartbeatName!),
        ];

    private static SqlServerCaptureInstanceMetadata CreateCaptureMetadata(
        string captureInstanceName,
        string expectedSourceSchema,
        string expectedSourceName,
        string expectedRoleName,
        IReadOnlyList<string> expectedColumns,
        IReadOnlyList<SqlServerCaptureInstanceRow>? rows,
        bool requireAllChangesFunction
    )
    {
        if (rows is null || rows.Count == 0)
        {
            return SqlServerCaptureInstanceMetadata.Missing(captureInstanceName);
        }

        SqlServerCaptureInstanceRow first = rows[0];
        string[] observedColumns = rows.OrderBy(row => row.ColumnOrdinal)
            .Select(row => row.ColumnName)
            .ToArray();
        bool exact =
            string.Equals(first.SourceSchema, expectedSourceSchema, StringComparison.Ordinal)
            && string.Equals(first.SourceName, expectedSourceName, StringComparison.Ordinal)
            && string.Equals(first.RoleName, expectedRoleName, StringComparison.Ordinal)
            && !first.SupportsNetChanges
            && !first.HasDropPending
            && string.IsNullOrWhiteSpace(first.FilegroupName)
            && (!first.PartitionSwitch || !first.SourceIsPartitioned)
            && (!requireAllChangesFunction || first.AllChangesFunctionPresent)
            && observedColumns.SequenceEqual(expectedColumns, StringComparer.Ordinal);

        return new(
            captureInstanceName,
            true,
            exact,
            string.IsNullOrWhiteSpace(first.RetainedMinLsn) ? null : first.RetainedMinLsn,
            string.IsNullOrWhiteSpace(first.RetainedMaxLsn) ? null : first.RetainedMaxLsn
        );
    }

    private static async Task<long> ReadHeartbeatSequenceAsync(
        SqlConnection connection,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONVERT(bigint, [HeartbeatSequence])
            FROM [dms].[CdcHeartbeat]
            WHERE [HeartbeatId] = 1;
            """;
        command.CommandTimeout = GetCommandTimeoutSeconds(commandTimeout);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null || value == DBNull.Value)
        {
            throw new InvalidOperationException("SQL Server CDC heartbeat sequence was not returned.");
        }

        return Convert.ToInt64(value);
    }

    private static async Task<SqlServerHeartbeatAfterImage?> ReadHeartbeatAfterImageAsync(
        SqlConnection connection,
        string captureInstanceName,
        long heartbeatSequence,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"""
            DECLARE @from_lsn binary(10) = sys.fn_cdc_get_min_lsn(@captureInstanceName);
            DECLARE @to_lsn binary(10) = sys.fn_cdc_get_max_lsn();

            IF @from_lsn IS NOT NULL
                AND @to_lsn IS NOT NULL
                AND @from_lsn <> 0x00000000000000000000
                AND @to_lsn >= @from_lsn
            BEGIN
                SELECT TOP (1)
                    [__$start_lsn],
                    [__$seqval],
                    CONVERT(bigint, [HeartbeatSequence]) AS [HeartbeatSequence]
                FROM cdc.fn_cdc_get_all_changes_{captureInstanceName}(@from_lsn, @to_lsn, N'all')
                WHERE CONVERT(int, [__$operation]) = 4
                  AND CONVERT(bigint, [HeartbeatSequence]) > @heartbeatSequence
                ORDER BY [__$start_lsn], [__$seqval];
            END;
            """;
        command.CommandTimeout = GetCommandTimeoutSeconds(commandTimeout);
        command.Parameters.AddWithValue("@captureInstanceName", captureInstanceName);
        command.Parameters.AddWithValue("@heartbeatSequence", heartbeatSequence);

        await using SqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new((byte[])reader["__$start_lsn"], (byte[])reader["__$seqval"], reader.GetInt64(2));
    }

    private static string? ResolveHeartbeatCaptureInstanceName(
        CdcBinding binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        AddDiagnostics(diagnostics, artifactNameResult.Diagnostics);

        return artifactNameResult.Inventory?.SqlServerCaptureInstanceCdcHeartbeatName;
    }

    private static void ValidateSqlServerBinding(
        CdcBinding binding,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (binding.Provider != CdcProvider.SqlServer)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ProviderMismatch,
                path,
                "CDC SQL Server source-position adapter requires a SQL Server binding."
            );
        }
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
            "SQL Server CDC source-position observation failed with outcome {Outcome}; exception type {ExceptionType}",
            outcome,
            exception.GetType().Name
        );
    }

    private static string ReadRequiredString(SqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.GetString(ordinal);
    }

    private static bool ReadRequiredBoolean(SqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.GetBoolean(ordinal);
    }

    private static int ReadRequiredInt32(SqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.GetInt32(ordinal);
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

    private sealed record SqlServerHeartbeatAfterImage(
        byte[] StartLsn,
        byte[] SeqVal,
        long HeartbeatSequence
    );

    private sealed record SqlServerProviderHistoryMetadata(
        bool DatabaseCdcEnabled,
        bool CdcJobsExactMatch,
        IReadOnlyList<SqlServerCaptureInstanceMetadata> ExpectedCaptures,
        string? RetainedRangeStart,
        string? RetainedMaxLsn,
        IReadOnlyList<CdcDiagnostic> Diagnostics
    );

    private sealed record SqlServerCaptureInstanceMetadata(
        string CaptureInstanceName,
        bool Exists,
        bool IsExactMatch,
        string? RetainedMinLsn,
        string? RetainedMaxLsn
    )
    {
        public static SqlServerCaptureInstanceMetadata Missing(string captureInstanceName) =>
            new(captureInstanceName, false, false, null, null);
    }

    private sealed record SqlServerCaptureInstanceRow(
        string CaptureInstanceName,
        string SourceSchema,
        string SourceName,
        string RoleName,
        bool SupportsNetChanges,
        bool HasDropPending,
        string FilegroupName,
        bool PartitionSwitch,
        bool SourceIsPartitioned,
        string RetainedMinLsn,
        string RetainedMaxLsn,
        bool AllChangesFunctionPresent,
        string ColumnName,
        int ColumnOrdinal
    );
}
