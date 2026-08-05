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
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlDocumentCacheWriter(
    NpgsqlDataSourceCache dataSourceCache,
    IDocumentCacheWriterRetryAdapter retryAdapter,
    ILogger<PostgresqlDocumentCacheWriter> logger,
    ITransactionFaultInjectionObserver? faultInjectionObserver = null,
    IDocumentCacheWriterTelemetry? telemetry = null
) : IDocumentCacheWriter, IDocumentCacheSessionBoundWriter
{
    private const string QueryCanceledSqlState = "57014";

    private static readonly DocumentCacheLifecycleReaderQuery LifecycleReaderQuery =
        DocumentCacheLifecycleReaderSupport.GetQuery(SqlDialect.Pgsql);
    private static readonly PostgresqlRelationalWriteExceptionClassifier LifecycleReadExceptionClassifier =
        new();

    private readonly NpgsqlDataSourceCache _dataSourceCache =
        dataSourceCache ?? throw new ArgumentNullException(nameof(dataSourceCache));
    private readonly IDocumentCacheWriterRetryAdapter _retryAdapter =
        retryAdapter ?? throw new ArgumentNullException(nameof(retryAdapter));
    private readonly ILogger<PostgresqlDocumentCacheWriter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ITransactionFaultInjectionObserver _faultInjectionObserver =
        faultInjectionObserver ?? NoOpTransactionFaultInjectionObserver.Instance;
    private readonly IDocumentCacheWriterTelemetry _telemetry =
        telemetry ?? NoOpDocumentCacheWriterTelemetry.Instance;

    public async Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string connectionString = RequireTargetConnectionString(request);

        _logger.LogDebug(
            "Executing PostgreSQL DocumentCache writer for target {TargetKey} with purpose {Purpose}",
            LoggingSanitizer.SanitizeForLogging(request.TargetContext.TargetKey.ToString()),
            request.Purpose
        );

        DocumentCacheWriterResult result = await _retryAdapter
            .ExecuteAsync(
                new DocumentCacheWriterRetryRequest(
                    RelationalProviderToken.Postgresql,
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
                RelationalProviderToken.Postgresql,
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

        if (request.MutexLease.ProviderToken != RelationalProviderToken.Postgresql)
        {
            throw new InvalidOperationException(
                $"PostgreSQL session-bound DocumentCache writer requires a PostgreSQL mutex lease, but received '{request.MutexLease.ProviderToken.Value}'."
            );
        }

        try
        {
            DocumentCacheWriterResult result = await _retryAdapter
                .ExecuteAsync(
                    new DocumentCacheWriterRetryRequest(
                        RelationalProviderToken.Postgresql,
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
                            cancellationToken,
                            request.MarkMutationBeforeCommit
                        )
                )
                .ConfigureAwait(false);

            _telemetry.RecordOutcome(
                DocumentCacheWriterMetricContext.ForCacheWriter(
                    RelationalProviderToken.Postgresql,
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
                "PostgreSQL session-bound DocumentCache writer lost the administrative mutex session for target {TargetKey}.",
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
                "PostgreSQL session-bound DocumentCache writer observed a provider command timeout for target {TargetKey}.",
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
                "PostgreSQL session-bound DocumentCache writer observed a closed administrative mutex session for target {TargetKey}.",
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
                "PostgreSQL session-bound DocumentCache writer observed a lost administrative mutex session for target {TargetKey}.",
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
        Func<CancellationToken, Task<PostgresqlDocumentCacheWriterTransaction>> beginTransactionAsync,
        CancellationToken cancellationToken,
        Action? markMutationBeforeCommit = null
    )
    {
        await using PostgresqlDocumentCacheWriterTransaction transactionScope = await beginTransactionAsync(
                cancellationToken
            )
            .ConfigureAwait(false);
        NpgsqlConnection connection = transactionScope.Connection;
        NpgsqlTransaction transaction = transactionScope.Transaction;
        CancellationToken activeTransactionCancellationToken = CancellationToken.None;

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
                    activeTransactionCancellationToken
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

            // PostgreSQL lock order: hold the shared lifecycle row lock, observe current
            // Document/ResourceKey/DocumentCache/DocumentProjectionWork without deliberately
            // row-locking work, perform cache DML against DocumentCache/source rows, then delete
            // matching work as the final commit gate. Duplicate writers and enqueue/acknowledge
            // races therefore meet on the cache row or final work delete, not on a pre-held work lock.
            DocumentCacheWriterCurrentObservation currentObservation = await ReadCurrentObservationAsync(
                    connection,
                    transaction,
                    request.DocumentId,
                    activeTransactionCancellationToken
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
                await transactionScope.CommitAsync(activeTransactionCancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                return selection.TerminalResult!;
            }

            if (selection.RequestsCacheAheadLatchFlow)
            {
                telemetryOutcome = selection.Outcome;
                await transactionScope.CommitAsync(activeTransactionCancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                DocumentCacheWriterSupport.RecordTransactionDuration(
                    _telemetry,
                    RelationalProviderToken.Postgresql,
                    request,
                    telemetryLifecycleState,
                    telemetryOutcome.Value,
                    transactionStartTimestamp
                );
                transactionTelemetryRecorded = true;
                return await DocumentCacheWriterCacheAheadIncidentFlow
                    .ExecuteAsync(
                        new DocumentCacheWriterCacheAheadIncidentRequest(
                            RelationalProviderToken.Postgresql,
                            request.TargetContext.TargetKey,
                            request.Purpose,
                            DocumentCacheWriterCacheAheadIncidentFlow.DefaultIncidentTimeout
                        ),
                        incidentCancellationToken =>
                            ConfirmCacheAheadAsync(
                                request,
                                beginTransactionAsync,
                                incidentCancellationToken,
                                markMutationBeforeCommit
                            ),
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
                        activeTransactionCancellationToken,
                        markMutationBeforeCommit
                    )
                    .ConfigureAwait(false)
                : await AcknowledgeAlreadyCurrentAsync(
                        connection,
                        transaction,
                        request,
                        lifecycleReadResult,
                        request.DocumentId,
                        selection.ExpectedContentVersion!.Value,
                        activeTransactionCancellationToken,
                        markMutationBeforeCommit
                    )
                    .ConfigureAwait(false);

            telemetryOutcome = result.Outcome;
            if (result is DocumentCacheWriterResult.RacingWriterLost)
            {
                await transactionScope.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                transactionCompleted = true;
                return result;
            }

            await transactionScope.CommitAsync(activeTransactionCancellationToken).ConfigureAwait(false);
            transactionCompleted = true;
            return result;
        }
        catch (PostgresException exception) when (IsRetryableDeleteRace(exception))
        {
            await DocumentCacheWriterSupport
                .RollbackIfNeededAsync(
                    transactionScope.RollbackAsync,
                    transactionCompleted,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            throw new DocumentCacheWriterRetryableDeleteRaceException();
        }
        catch
        {
            await DocumentCacheWriterSupport
                .RollbackIfNeededAsync(
                    transactionScope.RollbackAsync,
                    transactionCompleted,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!transactionTelemetryRecorded && telemetryOutcome is not null)
            {
                DocumentCacheWriterSupport.RecordTransactionDuration(
                    _telemetry,
                    RelationalProviderToken.Postgresql,
                    request,
                    telemetryLifecycleState,
                    telemetryOutcome.Value,
                    transactionStartTimestamp
                );
            }
        }
    }

    private async Task<DocumentCacheWriterResult> WriteCandidateAndAcknowledgeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DocumentCacheWriterRequest request,
        DocumentCacheLifecycleReadResult lifecycleReadResult,
        DocumentCacheWriterClassificationSelection selection,
        CancellationToken cancellationToken,
        Action? markMutationBeforeCommit
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
            RelationalProviderToken.Postgresql,
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
            RelationalProviderToken.Postgresql,
            request,
            lifecycleReadResult.Lifecycle?.State,
            acknowledgedRows == 1 ? selection.Outcome : DocumentCacheWriterOutcome.RacingWriterLost,
            acknowledgementStartTimestamp
        );

        if (acknowledgedRows == 1)
        {
            markMutationBeforeCommit?.Invoke();

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
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DocumentCacheWriterRequest request,
        DocumentCacheLifecycleReadResult lifecycleReadResult,
        long documentId,
        long expectedContentVersion,
        CancellationToken cancellationToken,
        Action? markMutationBeforeCommit
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
            RelationalProviderToken.Postgresql,
            request,
            lifecycleReadResult.Lifecycle?.State,
            acknowledgedRows == 1
                ? DocumentCacheWriterOutcome.AlreadyCurrentAcknowledged
                : DocumentCacheWriterOutcome.RacingWriterLost,
            acknowledgementStartTimestamp
        );

        if (acknowledgedRows == 1)
        {
            markMutationBeforeCommit?.Invoke();

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
        Func<CancellationToken, Task<PostgresqlDocumentCacheWriterTransaction>> beginTransactionAsync,
        CancellationToken cancellationToken,
        Action? markMutationBeforeCommit = null
    )
    {
        await using PostgresqlDocumentCacheWriterTransaction transactionScope = await beginTransactionAsync(
                cancellationToken
            )
            .ConfigureAwait(false);
        NpgsqlConnection connection = transactionScope.Connection;
        NpgsqlTransaction transaction = transactionScope.Transaction;
        CancellationToken activeTransactionCancellationToken = CancellationToken.None;

        var transactionCompleted = false;
        long transactionStartTimestamp = Stopwatch.GetTimestamp();
        DocumentCacheLifecycleState? telemetryLifecycleState = null;
        DocumentCacheWriterOutcome? telemetryOutcome = null;

        try
        {
            DocumentCacheLifecycleReadResult lifecycleReadResult = await ReadLifecycleForUpdateAsync(
                    connection,
                    transaction,
                    activeTransactionCancellationToken
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
                    activeTransactionCancellationToken
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
                await transactionScope.CommitAsync(activeTransactionCancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                return recheckDecision.TerminalResult;
            }

            DocumentCacheWriterCacheAheadLatchUpdateResult latchUpdateResult = await SetCacheAheadLatchAsync(
                    connection,
                    transaction,
                    request.DocumentId,
                    activeTransactionCancellationToken
                )
                .ConfigureAwait(false);

            if (latchUpdateResult.Outcome == DocumentCacheWriterCacheAheadLatchUpdateOutcome.LatchSet)
            {
                markMutationBeforeCommit?.Invoke();

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
                        cancellationToken: activeTransactionCancellationToken
                    )
                    .ConfigureAwait(false);
            }

            await transactionScope.CommitAsync(activeTransactionCancellationToken).ConfigureAwait(false);
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
            await DocumentCacheWriterSupport
                .RollbackIfNeededAsync(
                    transactionScope.RollbackAsync,
                    transactionCompleted,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (telemetryOutcome is not null)
            {
                DocumentCacheWriterSupport.RecordTransactionDuration(
                    _telemetry,
                    RelationalProviderToken.Postgresql,
                    request,
                    telemetryLifecycleState,
                    telemetryOutcome.Value,
                    transactionStartTimestamp
                );
            }
        }
    }

    private static async Task<DocumentCacheLifecycleReadResult> ReadLifecycleForShareAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken
    ) =>
        await ReadLifecycleAsync(
                connection,
                transaction,
                """
                SELECT "ProjectionLifecycleState", "CacheAheadRecoveryRequired"
                FROM "dms"."DocumentCacheState"
                WHERE "StateId" = 1
                FOR SHARE;
                """,
                cancellationToken
            )
            .ConfigureAwait(false);

    private static async Task<DocumentCacheLifecycleReadResult> ReadLifecycleForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken
    ) =>
        await ReadLifecycleAsync(
                connection,
                transaction,
                """
                SELECT "ProjectionLifecycleState", "CacheAheadRecoveryRequired"
                FROM "dms"."DocumentCacheState"
                WHERE "StateId" = 1
                FOR UPDATE;
                """,
                cancellationToken
            )
            .ConfigureAwait(false);

    private static async Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;

            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            return await DocumentCacheLifecycleReaderSupport
                .ReadLifecycleAsync(reader, LifecycleReaderQuery, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Missing,
                "DocumentCache lifecycle state table is missing."
            );
        }
        catch (DbException exception) when (!LifecycleReadExceptionClassifier.IsTransientFailure(exception))
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Unreadable,
                "DocumentCache lifecycle state is unreadable."
            );
        }
    }

    private static async Task<DocumentCacheWriterCurrentObservation> ReadCurrentObservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long documentId,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                document."ContentVersion" AS "SourceContentVersion",
                cache."ContentVersion" AS "CacheContentVersion",
                work."RequiredContentVersion" AS "WorkRequiredContentVersion",
                document."DocumentUuid" AS "SourceDocumentUuid",
                document."ResourceKeyId" AS "SourceResourceKeyId",
                resourceKey."ProjectName" AS "SourceProjectName",
                resourceKey."ResourceName" AS "SourceResourceName",
                resourceKey."ResourceVersion" AS "SourceResourceVersion"
            FROM (SELECT CAST(@documentId AS bigint) AS "DocumentId") requested
            LEFT JOIN "dms"."Document" document
                ON document."DocumentId" = requested."DocumentId"
            LEFT JOIN "dms"."ResourceKey" resourceKey
                ON resourceKey."ResourceKeyId" = document."ResourceKeyId"
            LEFT JOIN "dms"."DocumentCache" cache
                ON cache."DocumentId" = requested."DocumentId"
            LEFT JOIN "dms"."DocumentProjectionWork" work
                ON work."DocumentId" = requested."DocumentId";
            """;
        command.Parameters.Add(new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId });

        await using NpgsqlDataReader reader = await command
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
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DocumentCacheMaterializationCandidate candidate,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "dms"."DocumentCache" AS cache (
                "DocumentId",
                "DocumentUuid",
                "ProjectName",
                "ResourceName",
                "ResourceVersion",
                "ContentVersion",
                "StreamEtag",
                "LastModifiedAt",
                "DocumentJson",
                "ComputedAt"
            )
            SELECT
                document."DocumentId",
                document."DocumentUuid",
                resourceKey."ProjectName",
                resourceKey."ResourceName",
                resourceKey."ResourceVersion",
                document."ContentVersion",
                @streamEtag,
                @lastModifiedAt,
                @documentJson,
                statement_timestamp()
            FROM "dms"."Document" document
            INNER JOIN "dms"."ResourceKey" resourceKey
                ON resourceKey."ResourceKeyId" = document."ResourceKeyId"
            INNER JOIN "dms"."DocumentProjectionWork" work
                ON work."DocumentId" = document."DocumentId"
            WHERE document."DocumentId" = @documentId
              AND document."DocumentUuid" = @documentUuid
              AND document."ContentVersion" = @contentVersion
              AND resourceKey."ProjectName" = @projectName
              AND resourceKey."ResourceName" = @resourceName
              AND resourceKey."ResourceVersion" = @resourceVersion
              AND work."RequiredContentVersion" = @contentVersion
            ON CONFLICT ("DocumentId") DO UPDATE
            SET
                "DocumentUuid" = EXCLUDED."DocumentUuid",
                "ProjectName" = EXCLUDED."ProjectName",
                "ResourceName" = EXCLUDED."ResourceName",
                "ResourceVersion" = EXCLUDED."ResourceVersion",
                "ContentVersion" = EXCLUDED."ContentVersion",
                "StreamEtag" = EXCLUDED."StreamEtag",
                "LastModifiedAt" = EXCLUDED."LastModifiedAt",
                "DocumentJson" = EXCLUDED."DocumentJson",
                "ComputedAt" = statement_timestamp()
            WHERE cache."ContentVersion" < EXCLUDED."ContentVersion";
            """;

        AddCandidateParameters(command, candidate);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteAcknowledgementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long documentId,
        long expectedContentVersion,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM "dms"."DocumentProjectionWork" work
            USING "dms"."Document" document, "dms"."DocumentCache" cache
            WHERE work."DocumentId" = @documentId
              AND work."RequiredContentVersion" = @expectedContentVersion
              AND document."DocumentId" = work."DocumentId"
              AND document."ContentVersion" = @expectedContentVersion
              AND cache."DocumentId" = work."DocumentId"
              AND cache."ContentVersion" = @expectedContentVersion;
            """;
        command.Parameters.Add(new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId });
        command.Parameters.Add(
            new NpgsqlParameter("expectedContentVersion", NpgsqlDbType.Bigint)
            {
                Value = expectedContentVersion,
            }
        );

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DocumentCacheWriterCacheAheadLatchUpdateResult> SetCacheAheadLatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long documentId,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH current_cache_ahead AS (
                SELECT 1
                FROM "dms"."Document" document
                INNER JOIN "dms"."DocumentCache" cache
                    ON cache."DocumentId" = document."DocumentId"
                WHERE document."DocumentId" = @documentId
                  AND cache."ContentVersion" > document."ContentVersion"
                FOR SHARE OF document, cache
            ),
            latch_update AS (
                UPDATE "dms"."DocumentCacheState"
                SET "CacheAheadRecoveryRequired" = true
                WHERE "StateId" = 1
                  AND "ProjectionLifecycleState" IN ('Tracking', 'Rebuilding')
                  AND "CacheAheadRecoveryRequired" = false
                  AND EXISTS (SELECT 1 FROM current_cache_ahead)
                RETURNING 1
            )
            SELECT CASE
                WHEN EXISTS (SELECT 1 FROM latch_update) THEN @latchSet
                WHEN EXISTS (
                    SELECT 1
                    FROM "dms"."DocumentCacheState"
                    WHERE "StateId" = 1
                      AND "ProjectionLifecycleState" IN ('Tracking', 'Rebuilding')
                      AND "CacheAheadRecoveryRequired" = false
                ) THEN @cacheAheadDisappeared
                ELSE @lifecycleOrLatchFenced
            END;
            """;
        command.Parameters.Add(new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = documentId });
        command.Parameters.Add(
            new NpgsqlParameter("latchSet", NpgsqlDbType.Integer)
            {
                Value = (int)DocumentCacheWriterCacheAheadLatchUpdateOutcome.LatchSet,
            }
        );
        command.Parameters.Add(
            new NpgsqlParameter("cacheAheadDisappeared", NpgsqlDbType.Integer)
            {
                Value = (int)DocumentCacheWriterCacheAheadLatchUpdateOutcome.CacheAheadDisappeared,
            }
        );
        command.Parameters.Add(
            new NpgsqlParameter("lifecycleOrLatchFenced", NpgsqlDbType.Integer)
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
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
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
                    RelationalProviderToken.Postgresql,
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
        NpgsqlCommand command,
        DocumentCacheMaterializationCandidate candidate
    )
    {
        command.Parameters.Add(
            new NpgsqlParameter("documentId", NpgsqlDbType.Bigint) { Value = candidate.DocumentId }
        );
        command.Parameters.Add(
            new NpgsqlParameter("documentUuid", NpgsqlDbType.Uuid) { Value = candidate.DocumentUuid.Value }
        );
        command.Parameters.Add(
            new NpgsqlParameter("contentVersion", NpgsqlDbType.Bigint) { Value = candidate.ContentVersion }
        );
        command.Parameters.Add(
            new NpgsqlParameter("projectName", NpgsqlDbType.Varchar) { Value = candidate.ProjectName }
        );
        command.Parameters.Add(
            new NpgsqlParameter("resourceName", NpgsqlDbType.Varchar) { Value = candidate.ResourceName }
        );
        command.Parameters.Add(
            new NpgsqlParameter("resourceVersion", NpgsqlDbType.Varchar) { Value = candidate.ResourceVersion }
        );
        command.Parameters.Add(
            new NpgsqlParameter("streamEtag", NpgsqlDbType.Varchar) { Value = candidate.StreamEtag }
        );
        command.Parameters.Add(
            new NpgsqlParameter("lastModifiedAt", NpgsqlDbType.TimestampTz)
            {
                Value = candidate.LastModifiedAt,
            }
        );
        command.Parameters.Add(
            new NpgsqlParameter("documentJson", NpgsqlDbType.Jsonb)
            {
                Value = candidate.DocumentJson.ToJsonString(JsonSerializerOptions.Default),
            }
        );
    }

    private async Task<PostgresqlDocumentCacheWriterTransaction> BeginOrdinaryTransactionAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        NpgsqlDataSource dataSource = _dataSourceCache.GetOrCreate(connectionString);
        NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            NpgsqlTransaction transaction = await connection
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);

            return PostgresqlDocumentCacheWriterTransaction.Ordinary(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<PostgresqlDocumentCacheWriterTransaction> BeginSessionBoundTransactionAsync(
        IDocumentCacheAdministrativeMutexLease mutexLease,
        CancellationToken cancellationToken
    )
    {
        if (!mutexLease.IsSessionOpen)
        {
            throw new DocumentCacheAdministrativeMutexSessionLostException(
                RelationalProviderToken.Postgresql
            );
        }

        IRelationalWriteSession session = await mutexLease
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        if (
            session.Connection is not NpgsqlConnection connection
            || session.Transaction is not NpgsqlTransaction transaction
        )
        {
            await DisposeInvalidSessionAsync(session).ConfigureAwait(false);
            throw new InvalidOperationException(
                "PostgreSQL session-bound DocumentCache writer requires a PostgreSQL administrative mutex session."
            );
        }

        return PostgresqlDocumentCacheWriterTransaction.SessionBound(connection, transaction, session);
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
        exception is PostgresException { SqlState: QueryCanceledSqlState };

    private static bool IsRetryableDeleteRace(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.ForeignKeyViolation
        || (
            exception.SqlState == PostgresErrorCodes.RaiseException
            && exception.MessageText.StartsWith(
                DocumentCacheInventoryDefinition
                    .DocumentCacheTriggers
                    .ValidateDocumentUuidFailureMessagePrefix,
                StringComparison.Ordinal
            )
        );

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

        if (request.TargetContext.MappingSet.Key.Dialect != SqlDialect.Pgsql)
        {
            throw new InvalidOperationException(
                "PostgreSQL DocumentCache writer requires a PostgreSQL mapping set."
            );
        }

        if (request.TargetContext.TargetDataStore is null)
        {
            throw new InvalidOperationException(
                "PostgreSQL DocumentCache writer requires a target-bound data-store connection string."
            );
        }

        return request.TargetContext.TargetDataStore.ConnectionString;
    }

    private sealed class PostgresqlDocumentCacheWriterTransaction : IAsyncDisposable
    {
        private readonly IRelationalWriteSession? _session;
        private readonly bool _ownsConnection;

        private PostgresqlDocumentCacheWriterTransaction(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IRelationalWriteSession? session,
            bool ownsConnection
        )
        {
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            Transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
            _session = session;
            _ownsConnection = ownsConnection;
        }

        public NpgsqlConnection Connection { get; }

        public NpgsqlTransaction Transaction { get; }

        public static PostgresqlDocumentCacheWriterTransaction Ordinary(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction
        ) => new(connection, transaction, session: null, ownsConnection: true);

        public static PostgresqlDocumentCacheWriterTransaction SessionBound(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
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
