// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed class MssqlDocumentCacheWriter(
    IDocumentCacheWriterRetryAdapter retryAdapter,
    ILogger<MssqlDocumentCacheWriter> logger,
    ITransactionFaultInjectionObserver? faultInjectionObserver = null,
    IDocumentCacheWriterTelemetry? telemetry = null,
    string? sessionInitializationCommandText = null
) : IDocumentCacheWriter, IDocumentCacheSessionBoundWriter
{
    private const int InvalidObjectNameNumber = 208;
    private const int CommandTimeoutNumber = -2;

    private static readonly DocumentCacheLifecycleReaderQuery LifecycleReaderQuery =
        DocumentCacheLifecycleReaderSupport.GetQuery(SqlDialect.Mssql);
    private static readonly MssqlRelationalWriteExceptionClassifier WriteExceptionClassifier = new();

    private readonly IDocumentCacheWriterRetryAdapter _retryAdapter =
        retryAdapter ?? throw new ArgumentNullException(nameof(retryAdapter));
    private readonly ILogger<MssqlDocumentCacheWriter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ITransactionFaultInjectionObserver _faultInjectionObserver =
        faultInjectionObserver ?? NoOpTransactionFaultInjectionObserver.Instance;
    private readonly IDocumentCacheWriterTelemetry _telemetry =
        telemetry ?? NoOpDocumentCacheWriterTelemetry.Instance;
    private readonly string? _sessionInitializationCommandText = string.IsNullOrWhiteSpace(
        sessionInitializationCommandText
    )
        ? null
        : sessionInitializationCommandText;

    public async Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string connectionString = RequireTargetConnectionString(request);

        _logger.LogDebug(
            "Executing SQL Server DocumentCache writer for target {TargetKey} with purpose {Purpose}",
            LoggingSanitizer.SanitizeForLogging(request.TargetContext.TargetKey.ToString()),
            request.Purpose
        );

        DocumentCacheWriterResult result = await _retryAdapter
            .ExecuteAsync(
                new DocumentCacheWriterRetryRequest(
                    RelationalProviderToken.SqlServer,
                    request.TargetContext.TargetKey,
                    request.Purpose,
                    request.CancellationToken
                ),
                (_, cancellationToken) =>
                    ExecuteAttemptAsync(
                        request,
                        beginTransactionAsync: attemptCancellationToken =>
                            BeginOrdinaryTransactionAsync(connectionString, attemptCancellationToken),
                        cancellationToken
                    )
            )
            .ConfigureAwait(false);

        _telemetry.RecordOutcome(
            DocumentCacheWriterMetricContext.ForCacheWriter(
                RelationalProviderToken.SqlServer,
                request.TargetContext.TargetKey,
                request.Purpose,
                DocumentCacheWriterTelemetry.TryGetLifecycle(result),
                result.Outcome
            )
        );

        return result;
    }

    public async Task<DocumentCacheSessionBoundWriterResult> WriteAsync(
        DocumentCacheSessionBoundWriterRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        DocumentCacheWriterRequest writerRequest = request.WriterRequest;

        if (request.MutexLease.ProviderToken != RelationalProviderToken.SqlServer)
        {
            throw new InvalidOperationException(
                $"SQL Server session-bound DocumentCache writer requires a SQL Server mutex lease, but received '{request.MutexLease.ProviderToken.Value}'."
            );
        }

        try
        {
            DocumentCacheWriterResult result = await _retryAdapter
                .ExecuteAsync(
                    new DocumentCacheWriterRetryRequest(
                        RelationalProviderToken.SqlServer,
                        writerRequest.TargetContext.TargetKey,
                        writerRequest.Purpose,
                        writerRequest.CancellationToken
                    ),
                    (_, cancellationToken) =>
                        ExecuteAttemptAsync(
                            writerRequest,
                            beginTransactionAsync: attemptCancellationToken =>
                                BeginSessionBoundTransactionAsync(
                                    request.MutexLease,
                                    attemptCancellationToken
                                ),
                            cancellationToken
                        )
                )
                .ConfigureAwait(false);

            _telemetry.RecordOutcome(
                DocumentCacheWriterMetricContext.ForCacheWriter(
                    RelationalProviderToken.SqlServer,
                    writerRequest.TargetContext.TargetKey,
                    writerRequest.Purpose,
                    DocumentCacheWriterTelemetry.TryGetLifecycle(result),
                    result.Outcome
                )
            );

            return DocumentCacheSessionBoundWriterResult.FromWriterResult(
                result,
                request.CommandExecutionMutated
            );
        }
        catch (DocumentCacheAdministrativeMutexSessionLostException exception)
        {
            _logger.LogWarning(
                exception,
                "SQL Server session-bound DocumentCache writer lost the administrative mutex session for target {TargetKey}.",
                LoggingSanitizer.SanitizeForLogging(writerRequest.TargetContext.TargetKey.ToString())
            );

            return DocumentCacheSessionBoundWriterResult.SessionLoss(
                request.CommandExecutionMutated,
                "Administrative mutex session was lost during the session-bound DocumentCache writer."
            );
        }
        catch (DbException exception)
            when (request.MutexLease.IsSessionOpen && IsProviderCommandTimeout(exception))
        {
            _logger.LogWarning(
                exception,
                "SQL Server session-bound DocumentCache writer observed a provider command timeout for target {TargetKey}.",
                LoggingSanitizer.SanitizeForLogging(writerRequest.TargetContext.TargetKey.ToString())
            );

            return DocumentCacheSessionBoundWriterResult.ProviderCommandTimeout(
                request.CommandExecutionMutated,
                "Provider command timeout interrupted the session-bound DocumentCache writer."
            );
        }
        catch (DbException exception) when (!request.MutexLease.IsSessionOpen)
        {
            _logger.LogWarning(
                exception,
                "SQL Server session-bound DocumentCache writer observed a closed administrative mutex session for target {TargetKey}.",
                LoggingSanitizer.SanitizeForLogging(writerRequest.TargetContext.TargetKey.ToString())
            );

            return DocumentCacheSessionBoundWriterResult.SessionLoss(
                request.CommandExecutionMutated,
                "Administrative mutex session closed during the session-bound DocumentCache writer."
            );
        }
        catch (InvalidOperationException exception) when (!request.MutexLease.IsSessionOpen)
        {
            _logger.LogWarning(
                exception,
                "SQL Server session-bound DocumentCache writer observed a lost administrative mutex session for target {TargetKey}.",
                LoggingSanitizer.SanitizeForLogging(writerRequest.TargetContext.TargetKey.ToString())
            );

            return DocumentCacheSessionBoundWriterResult.SessionLoss(
                request.CommandExecutionMutated,
                "Administrative mutex session was lost during the session-bound DocumentCache writer."
            );
        }
    }

    private async Task<DocumentCacheWriterResult> ExecuteAttemptAsync(
        DocumentCacheWriterRequest request,
        Func<CancellationToken, Task<MssqlDocumentCacheWriterTransaction>> beginTransactionAsync,
        CancellationToken cancellationToken
    )
    {
        await using MssqlDocumentCacheWriterTransaction transactionScope = await beginTransactionAsync(
                cancellationToken
            )
            .ConfigureAwait(false);
        SqlConnection connection = transactionScope.Connection;
        SqlTransaction transaction = transactionScope.Transaction;

        var transactionCompleted = false;
        var transactionTelemetryRecorded = false;
        long transactionStartTimestamp = Stopwatch.GetTimestamp();
        DocumentCacheLifecycleState? telemetryLifecycleState = null;
        DocumentCacheWriterOutcome? telemetryOutcome = null;

        try
        {
            DocumentCacheLifecycleReadResult lifecycleReadResult = await ReadLifecycleForShareAsync(
                    connection,
                    transaction,
                    cancellationToken
                )
                .ConfigureAwait(false);
            telemetryLifecycleState = lifecycleReadResult.Lifecycle?.State;

            DocumentCacheWriterResult? lifecycleFence = DocumentCacheWriterSupport.SelectLifecycleFence(
                lifecycleReadResult
            );
            if (lifecycleFence is not null)
            {
                telemetryOutcome = lifecycleFence.Outcome;
                await transactionScope.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                transactionCompleted = true;
                return lifecycleFence;
            }

            // SQL Server lock order: hold the shared lifecycle row lock, observe current
            // Document/ResourceKey/DocumentCache/DocumentProjectionWork without deliberately
            // row-locking work, perform cache DML against DocumentCache/source rows, then delete
            // matching work as the final commit gate. Duplicate absent-row writers serialize on
            // the exact-key UPDLOCK,HOLDLOCK cache probe before insert.
            DocumentCacheWriterCurrentObservation currentObservation = await ReadCurrentObservationAsync(
                    connection,
                    transaction,
                    request.DocumentId,
                    cancellationToken
                )
                .ConfigureAwait(false);

            DocumentCacheWriterClassificationSelection selection =
                DocumentCacheWriterClassificationSelector.Select(
                    new DocumentCacheWriterClassificationRequest(
                        lifecycleReadResult,
                        currentObservation.ToCurrentState(),
                        DocumentCacheWriterSupport.BuildCandidateObservation(request, currentObservation)
                    )
                );

            if (!selection.RequiresProviderCompletion)
            {
                telemetryOutcome = selection.TerminalResult!.Outcome;
                await transactionScope.CommitAsync(cancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                return selection.TerminalResult!;
            }

            if (selection.RequestsCacheAheadLatchFlow)
            {
                telemetryOutcome = selection.Outcome;
                await transactionScope.CommitAsync(cancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                DocumentCacheWriterSupport.RecordTransactionDuration(
                    _telemetry,
                    RelationalProviderToken.SqlServer,
                    request,
                    telemetryLifecycleState,
                    telemetryOutcome.Value,
                    transactionStartTimestamp
                );
                transactionTelemetryRecorded = true;
                return await DocumentCacheWriterCacheAheadIncidentFlow
                    .ExecuteAsync(
                        new DocumentCacheWriterCacheAheadIncidentRequest(
                            RelationalProviderToken.SqlServer,
                            request.TargetContext.TargetKey,
                            request.Purpose,
                            DocumentCacheWriterCacheAheadIncidentFlow.DefaultIncidentTimeout
                        ),
                        incidentCancellationToken =>
                            ConfirmCacheAheadAsync(request, beginTransactionAsync, incidentCancellationToken),
                        _logger
                    )
                    .ConfigureAwait(false);
            }

            DocumentCacheWriterResult result = selection.WritesCache
                ? await WriteCandidateAndAcknowledgeAsync(
                        connection,
                        transaction,
                        request,
                        lifecycleReadResult,
                        selection,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
                : await AcknowledgeAlreadyCurrentAsync(
                        connection,
                        transaction,
                        request,
                        lifecycleReadResult,
                        request.DocumentId,
                        selection.ExpectedContentVersion!.Value,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

            telemetryOutcome = result.Outcome;
            if (result is DocumentCacheWriterResult.RacingWriterLost)
            {
                await transactionScope.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                transactionCompleted = true;
                return result;
            }

            await transactionScope.CommitAsync(cancellationToken).ConfigureAwait(false);
            transactionCompleted = true;
            return result;
        }
        catch (SqlException exception)
            when (MssqlDocumentCacheWriterDeleteRaceClassifier.IsRetryableDeleteRace(exception))
        {
            await RollbackIfNeededAsync(transactionScope, transactionCompleted).ConfigureAwait(false);
            throw new DocumentCacheWriterRetryableDeleteRaceException();
        }
        catch
        {
            await RollbackIfNeededAsync(transactionScope, transactionCompleted).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!transactionTelemetryRecorded && telemetryOutcome is not null)
            {
                DocumentCacheWriterSupport.RecordTransactionDuration(
                    _telemetry,
                    RelationalProviderToken.SqlServer,
                    request,
                    telemetryLifecycleState,
                    telemetryOutcome.Value,
                    transactionStartTimestamp
                );
            }
        }
    }

    private async Task<DocumentCacheWriterResult> WriteCandidateAndAcknowledgeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DocumentCacheWriterRequest request,
        DocumentCacheLifecycleReadResult lifecycleReadResult,
        DocumentCacheWriterClassificationSelection selection,
        CancellationToken cancellationToken
    )
    {
        DocumentCacheMaterializationCandidate candidate = selection.Candidate!;
        long expectedContentVersion = selection.ExpectedContentVersion!.Value;

        await ObserveFaultInjectionAsync(
                DocumentCacheWriterFaultInjectionHook.AfterMainStateLockAndClassificationBeforeCacheDml,
                request,
                lifecycleReadResult,
                selection.Outcome,
                connection,
                transaction,
                cacheDmlRowCount: null,
                acknowledgementRowCount: null,
                cacheAheadLatchRowCount: null,
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        long cacheDmlStartTimestamp = Stopwatch.GetTimestamp();
        int cacheRows = await ExecuteCacheWriteAsync(connection, transaction, candidate, cancellationToken)
            .ConfigureAwait(false);
        DocumentCacheWriterSupport.RecordCacheDmlDuration(
            _telemetry,
            RelationalProviderToken.SqlServer,
            request,
            lifecycleReadResult.Lifecycle?.State,
            selection.Outcome,
            cacheDmlStartTimestamp
        );

        await ObserveFaultInjectionAsync(
                DocumentCacheWriterFaultInjectionHook.AfterCacheDmlBeforeAcknowledgement,
                request,
                lifecycleReadResult,
                selection.Outcome,
                connection,
                transaction,
                cacheRows,
                acknowledgementRowCount: null,
                cacheAheadLatchRowCount: null,
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        long acknowledgementStartTimestamp = Stopwatch.GetTimestamp();
        int acknowledgedRows = await ExecuteAcknowledgementAsync(
                connection,
                transaction,
                candidate.DocumentId,
                expectedContentVersion,
                cancellationToken
            )
            .ConfigureAwait(false);
        DocumentCacheWriterSupport.RecordAcknowledgementDuration(
            _telemetry,
            RelationalProviderToken.SqlServer,
            request,
            lifecycleReadResult.Lifecycle?.State,
            acknowledgedRows == 1 ? selection.Outcome : DocumentCacheWriterOutcome.RacingWriterLost,
            acknowledgementStartTimestamp
        );

        if (acknowledgedRows == 1)
        {
            await ObserveFaultInjectionAsync(
                    DocumentCacheWriterFaultInjectionHook.AfterAcknowledgementBeforeCommit,
                    request,
                    lifecycleReadResult,
                    selection.Outcome,
                    connection,
                    transaction,
                    cacheRows,
                    acknowledgedRows,
                    cacheAheadLatchRowCount: null,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);

            return cacheRows > 0
                ? new DocumentCacheWriterResult.CandidateWrittenAcknowledged(
                    candidate,
                    expectedContentVersion
                )
                : new DocumentCacheWriterResult.AlreadyCurrentAcknowledged(expectedContentVersion);
        }

        return DocumentCacheWriterResult.RacingWriterLost.Instance;
    }

    private async Task<DocumentCacheWriterResult> AcknowledgeAlreadyCurrentAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DocumentCacheWriterRequest request,
        DocumentCacheLifecycleReadResult lifecycleReadResult,
        long documentId,
        long expectedContentVersion,
        CancellationToken cancellationToken
    )
    {
        long acknowledgementStartTimestamp = Stopwatch.GetTimestamp();
        int acknowledgedRows = await ExecuteAcknowledgementAsync(
                connection,
                transaction,
                documentId,
                expectedContentVersion,
                cancellationToken
            )
            .ConfigureAwait(false);
        DocumentCacheWriterSupport.RecordAcknowledgementDuration(
            _telemetry,
            RelationalProviderToken.SqlServer,
            request,
            lifecycleReadResult.Lifecycle?.State,
            acknowledgedRows == 1
                ? DocumentCacheWriterOutcome.AlreadyCurrentAcknowledged
                : DocumentCacheWriterOutcome.RacingWriterLost,
            acknowledgementStartTimestamp
        );

        if (acknowledgedRows == 1)
        {
            await ObserveFaultInjectionAsync(
                    DocumentCacheWriterFaultInjectionHook.AfterAcknowledgementBeforeCommit,
                    request,
                    lifecycleReadResult,
                    DocumentCacheWriterOutcome.AlreadyCurrentAcknowledged,
                    connection,
                    transaction,
                    cacheDmlRowCount: null,
                    acknowledgementRowCount: acknowledgedRows,
                    cacheAheadLatchRowCount: null,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);
        }

        return acknowledgedRows == 1
            ? new DocumentCacheWriterResult.AlreadyCurrentAcknowledged(expectedContentVersion)
            : DocumentCacheWriterResult.RacingWriterLost.Instance;
    }

    private async Task<DocumentCacheWriterResult> ConfirmCacheAheadAsync(
        DocumentCacheWriterRequest request,
        Func<CancellationToken, Task<MssqlDocumentCacheWriterTransaction>> beginTransactionAsync,
        CancellationToken cancellationToken
    )
    {
        await using MssqlDocumentCacheWriterTransaction transactionScope = await beginTransactionAsync(
                cancellationToken
            )
            .ConfigureAwait(false);
        SqlConnection connection = transactionScope.Connection;
        SqlTransaction transaction = transactionScope.Transaction;

        var transactionCompleted = false;
        long transactionStartTimestamp = Stopwatch.GetTimestamp();
        DocumentCacheLifecycleState? telemetryLifecycleState = null;
        DocumentCacheWriterOutcome? telemetryOutcome = null;

        try
        {
            DocumentCacheLifecycleReadResult lifecycleReadResult = await ReadLifecycleForUpdateAsync(
                    connection,
                    transaction,
                    cancellationToken
                )
                .ConfigureAwait(false);
            telemetryLifecycleState = lifecycleReadResult.Lifecycle?.State;
            DocumentCacheWriterResult? lifecycleFence = DocumentCacheWriterSupport.SelectLifecycleFence(
                lifecycleReadResult
            );
            if (lifecycleFence is not null)
            {
                telemetryOutcome = lifecycleFence.Outcome;
                await transactionScope.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                transactionCompleted = true;
                return lifecycleFence;
            }

            DocumentCacheWriterCurrentObservation currentObservation = await ReadCurrentObservationAsync(
                    connection,
                    transaction,
                    request.DocumentId,
                    cancellationToken
                )
                .ConfigureAwait(false);
            DocumentCacheWriterCacheAheadIncidentDecision recheckDecision =
                DocumentCacheWriterCacheAheadIncidentFlow.SelectRecheckDecision(
                    lifecycleReadResult,
                    currentObservation.ToCurrentState(),
                    DocumentCacheWriterSupport.BuildCandidateObservation(request, currentObservation)
                );

            if (recheckDecision.TerminalResult is not null)
            {
                telemetryOutcome = recheckDecision.TerminalResult.Outcome;
                await transactionScope.CommitAsync(cancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                return recheckDecision.TerminalResult;
            }

            DocumentCacheWriterCacheAheadLatchUpdateResult latchUpdateResult = await SetCacheAheadLatchAsync(
                    connection,
                    transaction,
                    request.DocumentId,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (latchUpdateResult.Outcome == DocumentCacheWriterCacheAheadLatchUpdateOutcome.LatchSet)
            {
                await ObserveFaultInjectionAsync(
                        DocumentCacheWriterFaultInjectionHook.AfterCacheAheadLatchUpdateBeforeIncidentCommit,
                        request,
                        lifecycleReadResult,
                        DocumentCacheWriterOutcome.CacheAheadLatchSet,
                        connection,
                        transaction,
                        cacheDmlRowCount: null,
                        acknowledgementRowCount: null,
                        cacheAheadLatchRowCount: latchUpdateResult.AffectedRows,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            await transactionScope.CommitAsync(cancellationToken).ConfigureAwait(false);
            transactionCompleted = true;

            DocumentCacheWriterResult result = DocumentCacheWriterCacheAheadIncidentFlow.CompleteLatchUpdate(
                recheckDecision,
                latchUpdateResult
            );
            telemetryOutcome = result.Outcome;
            return result;
        }
        catch
        {
            await RollbackIfNeededAsync(transactionScope, transactionCompleted).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (telemetryOutcome is not null)
            {
                DocumentCacheWriterSupport.RecordTransactionDuration(
                    _telemetry,
                    RelationalProviderToken.SqlServer,
                    request,
                    telemetryLifecycleState,
                    telemetryOutcome.Value,
                    transactionStartTimestamp
                );
            }
        }
    }

    private static async Task<DocumentCacheLifecycleReadResult> ReadLifecycleForShareAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken
    ) =>
        await ReadLifecycleAsync(
                connection,
                transaction,
                """
                SELECT [ProjectionLifecycleState], [CacheAheadRecoveryRequired]
                FROM [dms].[DocumentCacheState] WITH (HOLDLOCK)
                WHERE [StateId] = 1;
                """,
                cancellationToken
            )
            .ConfigureAwait(false);

    private static async Task<DocumentCacheLifecycleReadResult> ReadLifecycleForUpdateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken
    ) =>
        await ReadLifecycleAsync(
                connection,
                transaction,
                """
                SELECT [ProjectionLifecycleState], [CacheAheadRecoveryRequired]
                FROM [dms].[DocumentCacheState] WITH (XLOCK, HOLDLOCK)
                WHERE [StateId] = 1;
                """,
                cancellationToken
            )
            .ConfigureAwait(false);

    private async Task InitializeSessionAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (_sessionInitializationCommandText is null)
        {
            return;
        }

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = _sessionInitializationCommandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using SqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;

            await using SqlDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            return await DocumentCacheLifecycleReaderSupport
                .ReadLifecycleAsync(reader, LifecycleReaderQuery, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqlException exception) when (exception.Number == InvalidObjectNameNumber)
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Missing,
                "DocumentCache lifecycle state table is missing."
            );
        }
        catch (DbException exception) when (!WriteExceptionClassifier.IsTransientFailure(exception))
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Unreadable,
                "DocumentCache lifecycle state is unreadable."
            );
        }
    }

    private static async Task<DocumentCacheWriterCurrentObservation> ReadCurrentObservationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long documentId,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                [document].[ContentVersion] AS [SourceContentVersion],
                [cache].[ContentVersion] AS [CacheContentVersion],
                [work].[RequiredContentVersion] AS [WorkRequiredContentVersion],
                [document].[DocumentUuid] AS [SourceDocumentUuid],
                [document].[ResourceKeyId] AS [SourceResourceKeyId],
                [resourceKey].[ProjectName] AS [SourceProjectName],
                [resourceKey].[ResourceName] AS [SourceResourceName],
                [resourceKey].[ResourceVersion] AS [SourceResourceVersion]
            FROM (VALUES (CAST(@documentId AS bigint))) AS [requested]([DocumentId])
            LEFT JOIN [dms].[Document] AS [document]
                ON [document].[DocumentId] = [requested].[DocumentId]
            LEFT JOIN [dms].[ResourceKey] AS [resourceKey]
                ON [resourceKey].[ResourceKeyId] = [document].[ResourceKeyId]
            LEFT JOIN [dms].[DocumentCache] AS [cache]
                ON [cache].[DocumentId] = [requested].[DocumentId]
            LEFT JOIN [dms].[DocumentProjectionWork] AS [work]
                ON [work].[DocumentId] = [requested].[DocumentId];
            """;
        command.Parameters.Add(new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId });

        await using SqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "DocumentCache writer current-state observation returned no row."
            );
        }

        var observation = new DocumentCacheWriterCurrentObservation(
            DocumentCacheWriterSupport.GetNullableInt64(reader, "SourceContentVersion"),
            DocumentCacheWriterSupport.GetNullableInt64(reader, "CacheContentVersion"),
            DocumentCacheWriterSupport.GetNullableInt64(reader, "WorkRequiredContentVersion"),
            DocumentCacheWriterSupport.GetNullableGuid(reader, "SourceDocumentUuid"),
            DocumentCacheWriterSupport.GetNullableInt16(reader, "SourceResourceKeyId"),
            DocumentCacheWriterSupport.GetNullableString(reader, "SourceProjectName"),
            DocumentCacheWriterSupport.GetNullableString(reader, "SourceResourceName"),
            DocumentCacheWriterSupport.GetNullableString(reader, "SourceResourceVersion")
        );

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "DocumentCache writer current-state observation returned multiple rows."
            );
        }

        return observation;
    }

    private static async Task<int> ExecuteCacheWriteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DocumentCacheMaterializationCandidate candidate,
        CancellationToken cancellationToken
    )
    {
        int updatedRows = await ExecuteCacheUpdateAsync(connection, transaction, candidate, cancellationToken)
            .ConfigureAwait(false);

        if (updatedRows != 0)
        {
            return updatedRows;
        }

        return await ExecuteCacheInsertAsync(connection, transaction, candidate, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<int> ExecuteCacheUpdateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DocumentCacheMaterializationCandidate candidate,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE [cache]
            SET
                [DocumentUuid] = [document].[DocumentUuid],
                [ProjectName] = [resourceKey].[ProjectName],
                [ResourceName] = [resourceKey].[ResourceName],
                [ResourceVersion] = [resourceKey].[ResourceVersion],
                [ContentVersion] = [document].[ContentVersion],
                [StreamEtag] = @streamEtag,
                [LastModifiedAt] = @lastModifiedAt,
                [DocumentJson] = @documentJson,
                [ComputedAt] = sysutcdatetime()
            FROM [dms].[DocumentCache] AS [cache]
            INNER JOIN [dms].[Document] AS [document]
                ON [document].[DocumentId] = [cache].[DocumentId]
            INNER JOIN [dms].[ResourceKey] AS [resourceKey]
                ON [resourceKey].[ResourceKeyId] = [document].[ResourceKeyId]
            INNER JOIN [dms].[DocumentProjectionWork] AS [work]
                ON [work].[DocumentId] = [document].[DocumentId]
            WHERE [cache].[DocumentId] = @documentId
              AND [cache].[ContentVersion] < @contentVersion
              AND [document].[DocumentId] = @documentId
              AND [document].[DocumentUuid] = @documentUuid
              AND [document].[ContentVersion] = @contentVersion
              AND [resourceKey].[ProjectName] = @projectName
              AND [resourceKey].[ResourceName] = @resourceName
              AND [resourceKey].[ResourceVersion] = @resourceVersion
              AND [work].[RequiredContentVersion] = @contentVersion;
            """;

        AddCandidateParameters(command, candidate);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteCacheInsertAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DocumentCacheMaterializationCandidate candidate,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO [dms].[DocumentCache] (
                [DocumentId],
                [DocumentUuid],
                [ProjectName],
                [ResourceName],
                [ResourceVersion],
                [ContentVersion],
                [StreamEtag],
                [LastModifiedAt],
                [DocumentJson],
                [ComputedAt]
            )
            SELECT
                [document].[DocumentId],
                [document].[DocumentUuid],
                [resourceKey].[ProjectName],
                [resourceKey].[ResourceName],
                [resourceKey].[ResourceVersion],
                [document].[ContentVersion],
                @streamEtag,
                @lastModifiedAt,
                @documentJson,
                sysutcdatetime()
            FROM [dms].[Document] AS [document]
            INNER JOIN [dms].[ResourceKey] AS [resourceKey]
                ON [resourceKey].[ResourceKeyId] = [document].[ResourceKeyId]
            INNER JOIN [dms].[DocumentProjectionWork] AS [work]
                ON [work].[DocumentId] = [document].[DocumentId]
            WHERE [document].[DocumentId] = @documentId
              AND [document].[DocumentUuid] = @documentUuid
              AND [document].[ContentVersion] = @contentVersion
              AND [resourceKey].[ProjectName] = @projectName
              AND [resourceKey].[ResourceName] = @resourceName
              AND [resourceKey].[ResourceVersion] = @resourceVersion
              AND [work].[RequiredContentVersion] = @contentVersion
              AND NOT EXISTS (
                  SELECT 1
                  FROM [dms].[DocumentCache] AS [cache] WITH (UPDLOCK, HOLDLOCK)
                  WHERE [cache].[DocumentId] = @documentId
              );
            """;

        AddCandidateParameters(command, candidate);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteAcknowledgementAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long documentId,
        long expectedContentVersion,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE [work]
            FROM [dms].[DocumentProjectionWork] AS [work]
            INNER JOIN [dms].[Document] AS [document]
                ON [document].[DocumentId] = [work].[DocumentId]
            INNER JOIN [dms].[DocumentCache] AS [cache]
                ON [cache].[DocumentId] = [work].[DocumentId]
            WHERE [work].[DocumentId] = @documentId
              AND [work].[RequiredContentVersion] = @expectedContentVersion
              AND [document].[ContentVersion] = @expectedContentVersion
              AND [cache].[ContentVersion] = @expectedContentVersion;
            """;
        command.Parameters.Add(new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId });
        command.Parameters.Add(
            new SqlParameter("@expectedContentVersion", SqlDbType.BigInt) { Value = expectedContentVersion }
        );

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DocumentCacheWriterCacheAheadLatchUpdateResult> SetCacheAheadLatchAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long documentId,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @latchUpdate TABLE ([AffectedRows] int NOT NULL);

            UPDATE [state]
            SET [CacheAheadRecoveryRequired] = CAST(1 AS bit)
            OUTPUT 1 INTO @latchUpdate
            FROM [dms].[DocumentCacheState] AS [state]
            WHERE [state].[StateId] = 1
              AND [state].[ProjectionLifecycleState] IN ('Tracking', 'Rebuilding')
              AND [state].[CacheAheadRecoveryRequired] = CAST(0 AS bit)
              AND EXISTS (
                  SELECT 1
                  FROM [dms].[Document] AS [document] WITH (HOLDLOCK)
                  INNER JOIN [dms].[DocumentCache] AS [cache] WITH (HOLDLOCK)
                      ON [cache].[DocumentId] = [document].[DocumentId]
                  WHERE [document].[DocumentId] = @documentId
                    AND [cache].[ContentVersion] > [document].[ContentVersion]
              );

            SELECT CASE
                WHEN EXISTS (SELECT 1 FROM @latchUpdate) THEN @latchSet
                WHEN EXISTS (
                    SELECT 1
                    FROM [dms].[DocumentCacheState] WITH (HOLDLOCK)
                    WHERE [StateId] = 1
                      AND [ProjectionLifecycleState] IN ('Tracking', 'Rebuilding')
                      AND [CacheAheadRecoveryRequired] = CAST(0 AS bit)
                ) THEN @cacheAheadDisappeared
                ELSE @lifecycleOrLatchFenced
            END;
            """;
        command.Parameters.Add(new SqlParameter("@documentId", SqlDbType.BigInt) { Value = documentId });
        command.Parameters.Add(
            new SqlParameter("@latchSet", SqlDbType.Int)
            {
                Value = (int)DocumentCacheWriterCacheAheadLatchUpdateOutcome.LatchSet,
            }
        );
        command.Parameters.Add(
            new SqlParameter("@cacheAheadDisappeared", SqlDbType.Int)
            {
                Value = (int)DocumentCacheWriterCacheAheadLatchUpdateOutcome.CacheAheadDisappeared,
            }
        );
        command.Parameters.Add(
            new SqlParameter("@lifecycleOrLatchFenced", SqlDbType.Int)
            {
                Value = (int)DocumentCacheWriterCacheAheadLatchUpdateOutcome.LifecycleOrLatchFenced,
            }
        );

        object? outcomeValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var outcome = (DocumentCacheWriterCacheAheadLatchUpdateOutcome)Convert.ToInt32(outcomeValue);

        return outcome switch
        {
            DocumentCacheWriterCacheAheadLatchUpdateOutcome.LatchSet =>
                DocumentCacheWriterCacheAheadLatchUpdateResult.LatchSet(),
            DocumentCacheWriterCacheAheadLatchUpdateOutcome.CacheAheadDisappeared =>
                DocumentCacheWriterCacheAheadLatchUpdateResult.CacheAheadDisappeared(),
            DocumentCacheWriterCacheAheadLatchUpdateOutcome.LifecycleOrLatchFenced =>
                DocumentCacheWriterCacheAheadLatchUpdateResult.LifecycleOrLatchFenced(),
            _ => throw new InvalidOperationException("Unsupported cache-ahead latch update outcome."),
        };
    }

    private async ValueTask ObserveFaultInjectionAsync(
        DocumentCacheWriterFaultInjectionHook hook,
        DocumentCacheWriterRequest request,
        DocumentCacheLifecycleReadResult lifecycleReadResult,
        DocumentCacheWriterOutcome outcome,
        SqlConnection connection,
        SqlTransaction transaction,
        int? cacheDmlRowCount,
        int? acknowledgementRowCount,
        int? cacheAheadLatchRowCount,
        CancellationToken cancellationToken
    )
    {
        await _faultInjectionObserver
            .ObserveAsync(
                new DocumentCacheWriterFaultInjectionContext(
                    hook,
                    RelationalProviderToken.SqlServer,
                    request.TargetContext.TargetKey,
                    request.Purpose,
                    lifecycleReadResult.Lifecycle?.State,
                    lifecycleReadResult.Lifecycle?.CacheAheadRecoveryRequired,
                    outcome,
                    cacheDmlRowCount,
                    acknowledgementRowCount,
                    cacheAheadLatchRowCount
                ),
                new DocumentCacheWriterFaultInjectionControl(connection, transaction),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static void AddCandidateParameters(
        SqlCommand command,
        DocumentCacheMaterializationCandidate candidate
    )
    {
        command.Parameters.Add(
            new SqlParameter("@documentId", SqlDbType.BigInt) { Value = candidate.DocumentId }
        );
        command.Parameters.Add(
            new SqlParameter("@documentUuid", SqlDbType.UniqueIdentifier)
            {
                Value = candidate.DocumentUuid.Value,
            }
        );
        command.Parameters.Add(
            new SqlParameter("@contentVersion", SqlDbType.BigInt) { Value = candidate.ContentVersion }
        );
        command.Parameters.Add(
            new SqlParameter("@projectName", SqlDbType.NVarChar, 256) { Value = candidate.ProjectName }
        );
        command.Parameters.Add(
            new SqlParameter("@resourceName", SqlDbType.NVarChar, 256) { Value = candidate.ResourceName }
        );
        command.Parameters.Add(
            new SqlParameter("@resourceVersion", SqlDbType.NVarChar, 32) { Value = candidate.ResourceVersion }
        );
        command.Parameters.Add(
            new SqlParameter("@streamEtag", SqlDbType.VarChar, 64) { Value = candidate.StreamEtag }
        );
        command.Parameters.Add(
            new SqlParameter("@lastModifiedAt", SqlDbType.DateTime2)
            {
                Value = candidate.LastModifiedAt.UtcDateTime,
            }
        );
        command.Parameters.Add(
            new SqlParameter("@documentJson", SqlDbType.NVarChar, -1)
            {
                Value = candidate.DocumentJson.ToJsonString(JsonSerializerOptions.Default),
            }
        );
    }

    private async Task<MssqlDocumentCacheWriterTransaction> BeginOrdinaryTransactionAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        SqlConnection connection = new(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await InitializeSessionAsync(connection, cancellationToken).ConfigureAwait(false);
            SqlTransaction transaction = (SqlTransaction)
                await connection
                    .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                    .ConfigureAwait(false);

            return MssqlDocumentCacheWriterTransaction.Ordinary(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<MssqlDocumentCacheWriterTransaction> BeginSessionBoundTransactionAsync(
        IDocumentCacheAdministrativeMutexLease mutexLease,
        CancellationToken cancellationToken
    )
    {
        if (!mutexLease.IsSessionOpen)
        {
            throw new DocumentCacheAdministrativeMutexSessionLostException(RelationalProviderToken.SqlServer);
        }

        IRelationalWriteSession session = await mutexLease
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        if (
            session.Connection is not SqlConnection connection
            || session.Transaction is not SqlTransaction transaction
        )
        {
            await DisposeInvalidSessionAsync(session).ConfigureAwait(false);
            throw new InvalidOperationException(
                "SQL Server session-bound DocumentCache writer requires a SQL Server administrative mutex session."
            );
        }

        return MssqlDocumentCacheWriterTransaction.SessionBound(connection, transaction, session);
    }

    private static async Task RollbackIfNeededAsync(
        MssqlDocumentCacheWriterTransaction transactionScope,
        bool transactionCompleted
    )
    {
        if (transactionCompleted)
        {
            return;
        }

        try
        {
            await transactionScope.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            _ = exception;
        }
        catch (DbException exception)
        {
            _ = exception;
        }
    }

    private static async Task DisposeInvalidSessionAsync(IRelationalWriteSession session)
    {
        try
        {
            await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbException)
        {
            _ = exception;
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool IsProviderCommandTimeout(DbException exception) =>
        exception is SqlException { Number: CommandTimeoutNumber };

    private static string RequireTargetConnectionString(DocumentCacheWriterRequest request)
    {
        if (
            request.TargetContext.TargetValidation
            != DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
        )
        {
            throw new InvalidOperationException(
                "DocumentCache writer requires a target context selected after EffectiveSchema and ResourceKey seed validation."
            );
        }

        if (request.TargetContext.MappingSet.Key.Dialect != SqlDialect.Mssql)
        {
            throw new InvalidOperationException(
                "SQL Server DocumentCache writer requires a SQL Server mapping set."
            );
        }

        if (request.TargetContext.TargetDataStore is null)
        {
            throw new InvalidOperationException(
                "SQL Server DocumentCache writer requires a target-bound data-store connection string."
            );
        }

        return request.TargetContext.TargetDataStore.ConnectionString;
    }

    private static long? GetNullableInt64(SqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static short? GetNullableInt16(SqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt16(ordinal);
    }

    private static Guid? GetNullableGuid(SqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    private static string? GetNullableString(SqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private sealed record MssqlDocumentCacheWriterCurrentObservation(
        long? SourceContentVersion,
        long? CacheContentVersion,
        long? WorkRequiredContentVersion,
        Guid? SourceDocumentUuid,
        short? SourceResourceKeyId,
        string? SourceProjectName,
        string? SourceResourceName,
        string? SourceResourceVersion
    )
    {
        public DocumentCacheWriterCurrentStateObservation ToCurrentState() =>
            new(SourceContentVersion, CacheContentVersion, WorkRequiredContentVersion);
    }

    private sealed class MssqlDocumentCacheWriterTransaction : IAsyncDisposable
    {
        private readonly IRelationalWriteSession? _session;
        private readonly bool _ownsConnection;

        private MssqlDocumentCacheWriterTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            IRelationalWriteSession? session,
            bool ownsConnection
        )
        {
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            Transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
            _session = session;
            _ownsConnection = ownsConnection;
        }

        public SqlConnection Connection { get; }

        public SqlTransaction Transaction { get; }

        public static MssqlDocumentCacheWriterTransaction Ordinary(
            SqlConnection connection,
            SqlTransaction transaction
        ) => new(connection, transaction, session: null, ownsConnection: true);

        public static MssqlDocumentCacheWriterTransaction SessionBound(
            SqlConnection connection,
            SqlTransaction transaction,
            IRelationalWriteSession session
        ) => new(connection, transaction, session, ownsConnection: false);

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (_session is null)
            {
                await Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await _session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            if (_session is null)
            {
                await Transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await _session.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_session is not null)
            {
                await _session.DisposeAsync().ConfigureAwait(false);
                return;
            }

            await Transaction.DisposeAsync().ConfigureAwait(false);
            if (_ownsConnection)
            {
                await Connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
