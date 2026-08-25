// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed class MssqlCdcSourcePositionAdapter(
    IDocumentCacheProviderCommandTimeoutClassifier timeoutClassifier,
    TimeProvider timeProvider,
    ILogger<MssqlCdcSourcePositionAdapter> logger
) : ICdcProviderSourcePositionAdapter
{
    private const string HeartbeatSequencePath = "$.sqlServerHeartbeatSequence";
    private const long HeartbeatAfterImageEventSerialNo = 2;

    private readonly IDocumentCacheProviderCommandTimeoutClassifier _timeoutClassifier =
        timeoutClassifier ?? throw new ArgumentNullException(nameof(timeoutClassifier));
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<MssqlCdcSourcePositionAdapter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    CdcProvider ICdcProviderSourcePositionAdapter.Provider => CdcProvider.SqlServer;

    public async Task<CdcProviderBarrierCaptureResult> CaptureBarrierAsync(
        CdcProviderBarrierCaptureRequest request,
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
            return CdcProviderBarrierCaptureResult.Failure(
                CdcProvider.SqlServer,
                capturedAt,
                diagnostics.Diagnostics
            );
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
                        return CdcProviderBarrierCaptureResult.SqlServerSuccess(
                            commitLsn.Lsn.Value.ToString(),
                            changeLsn.Lsn.Value.ToString(),
                            HeartbeatAfterImageEventSerialNo,
                            UtcNow()
                        );
                    }

                    return CdcProviderBarrierCaptureResult.Failure(
                        CdcProvider.SqlServer,
                        UtcNow(),
                        diagnostics.Diagnostics
                    );
                }

                await Task.Delay(request.PollInterval, cancellationToken).ConfigureAwait(false);
            }

            diagnostics.LocalStateUnavailable(
                HeartbeatSequencePath,
                "CDC SQL Server provider barrier capture timed out waiting for heartbeat after-image."
            );
            return CdcProviderBarrierCaptureResult.Failure(
                CdcProvider.SqlServer,
                UtcNow(),
                diagnostics.Diagnostics
            );
        }
        catch (OperationCanceledException)
        {
            diagnostics.LocalStateUnavailable(
                HeartbeatSequencePath,
                "CDC SQL Server provider barrier capture was cancelled."
            );
            return CdcProviderBarrierCaptureResult.Failure(
                CdcProvider.SqlServer,
                UtcNow(),
                diagnostics.Diagnostics
            );
        }
        catch (Exception exception) when (_timeoutClassifier.IsProviderCommandTimeout(exception))
        {
            LogProviderObservationFailure(exception, "provider-barrier-timeout");
            diagnostics.LocalStateUnavailable(
                HeartbeatSequencePath,
                "CDC SQL Server provider barrier capture timed out."
            );
            return CdcProviderBarrierCaptureResult.Failure(
                CdcProvider.SqlServer,
                UtcNow(),
                diagnostics.Diagnostics
            );
        }
        catch (Exception exception)
        {
            LogProviderObservationFailure(exception, "provider-barrier-failed");
            diagnostics.LocalStateUnavailable(
                HeartbeatSequencePath,
                "CDC SQL Server provider barrier capture failed."
            );
            return CdcProviderBarrierCaptureResult.Failure(
                CdcProvider.SqlServer,
                UtcNow(),
                diagnostics.Diagnostics
            );
        }
    }

    public CdcProviderBarrierObservation ObserveProviderBarrier(CdcProviderBarrierObservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Binding);
        ArgumentNullException.ThrowIfNull(request.CapturedBarrier);
        ArgumentNullException.ThrowIfNull(request.ConnectorOffset);

        CdcDiagnosticCollector diagnostics = new();
        ValidateSqlServerBinding(request.Binding, "$.binding.provider", diagnostics);
        AddDiagnostics(diagnostics, request.CapturedBarrier.Diagnostics);
        ValidateSqlServerCaptureResult(request.CapturedBarrier, "$.capturedBarrier.provider", diagnostics);

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

        CdcProviderBarrierState barrierState = CdcProviderBarrierState.Unknown;
        string? committedPosition = null;

        if (
            !diagnostics.HasDiagnostics
            && request.CapturedBarrier.Succeeded
            && request.CapturedBarrier.SqlServerCommitLsn is not null
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

            if (commitLsn.Lsn is not null && changeLsn.Lsn is not null && !diagnostics.HasDiagnostics)
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
                else if (IsKnownNotReached(comparison))
                {
                    barrierState = CdcProviderBarrierState.NotReached;
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

    public Task<CdcSourceHistoryClassificationResult> ObserveSourceHistoryAsync(
        CdcSourceHistoryObservationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Binding);
        ArgumentNullException.ThrowIfNull(request.ConnectorOffset);
        cancellationToken.ThrowIfCancellationRequested();

        CdcDiagnosticCollector diagnostics = new();
        ValidateSqlServerBinding(request.Binding, "$.binding.provider", diagnostics);

        return Task.FromResult(
            CdcSourceHistoryContinuityClassifier.Evaluate(
                new(request.OperationId, UtcNow(), UtcNow(), request.Binding)
                {
                    ProviderSetup = request.ProviderSetup,
                    ConnectorOffset = request.ConnectorOffset,
                    ProviderHistory = request.ProviderHistory,
                    SqlServerSchemaHistory = request.SqlServerSchemaHistory,
                    LatchedIncident = request.LatchedIncident,
                    ExpectedConnectSourcePartitionHash = request.ExpectedConnectSourcePartitionHash,
                    Diagnostics = [.. diagnostics.Diagnostics],
                }
            )
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

    private static void ValidateSqlServerCaptureResult(
        CdcProviderBarrierCaptureResult captureResult,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (captureResult.Provider != CdcProvider.SqlServer)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ProviderMismatch,
                path,
                "CDC provider barrier capture result provider did not match SQL Server."
            );
        }
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static bool IsKnownNotReached(CdcProviderPositionComparisonResult comparison) =>
        comparison.Diagnostics.Count > 0
        && comparison.Diagnostics.All(diagnostic =>
            diagnostic.Category == CdcDiagnosticCategory.InvalidOrdering
        );

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
}
