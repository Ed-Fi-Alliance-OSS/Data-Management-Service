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
    IDocumentCacheWriterTelemetry? telemetry = null
) : IDocumentCacheWriter
{
    private const int ForeignKeyConstraintViolationNumber = 547;
    private const int ThrowStatementNumber = 50000;

    private readonly IDocumentCacheWriterRetryAdapter _retryAdapter =
        retryAdapter ?? throw new ArgumentNullException(nameof(retryAdapter));
    private readonly ILogger<MssqlDocumentCacheWriter> _logger =
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
                (_, cancellationToken) => ExecuteAttemptAsync(request, connectionString, cancellationToken)
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

    private async Task<DocumentCacheWriterResult> ExecuteAttemptAsync(
        DocumentCacheWriterRequest request,
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlTransaction transaction = (SqlTransaction)
            await connection
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);

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

            DocumentCacheWriterResult? lifecycleFence = SelectLifecycleFence(
                request.Purpose,
                lifecycleReadResult
            );
            if (lifecycleFence is not null)
            {
                telemetryOutcome = lifecycleFence.Outcome;
                await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                return lifecycleFence;
            }

            // SQL Server lock order: hold the shared lifecycle row lock, observe current
            // Document/ResourceKey/DocumentCache/DocumentProjectionWork without deliberately
            // row-locking work, perform cache DML against DocumentCache/source rows, then delete
            // matching work as the final commit gate. Duplicate absent-row writers serialize on
            // the exact-key UPDLOCK,HOLDLOCK cache probe before insert.
            MssqlDocumentCacheWriterCurrentObservation currentObservation = await ReadCurrentObservationAsync(
                    connection,
                    transaction,
                    request.DocumentId,
                    cancellationToken
                )
                .ConfigureAwait(false);

            DocumentCacheWriterClassificationSelection selection =
                DocumentCacheWriterClassificationSelector.Select(
                    new DocumentCacheWriterClassificationRequest(
                        request.Purpose,
                        lifecycleReadResult,
                        currentObservation.ToCurrentState(),
                        BuildCandidateObservation(request, currentObservation)
                    )
                );

            if (!selection.RequiresProviderCompletion)
            {
                telemetryOutcome = selection.TerminalResult!.Outcome;
                await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                return selection.TerminalResult!;
            }

            if (selection.RequestsCacheAheadLatchFlow)
            {
                telemetryOutcome = selection.Outcome;
                await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                RecordTransactionDuration(
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
                            ConfirmCacheAheadAsync(request, connectionString, incidentCancellationToken),
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
                await RollbackAsync(transaction, cancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                return result;
            }

            await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
            transactionCompleted = true;
            return result;
        }
        catch (SqlException exception) when (IsRetryableDeleteRace(exception))
        {
            await RollbackIfNeededAsync(transaction, transactionCompleted, cancellationToken)
                .ConfigureAwait(false);
            throw new DocumentCacheWriterRetryableDeleteRaceException();
        }
        catch
        {
            await RollbackIfNeededAsync(transaction, transactionCompleted, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!transactionTelemetryRecorded && telemetryOutcome is not null)
            {
                RecordTransactionDuration(
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
        RecordCacheDmlDuration(
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
        RecordAcknowledgementDuration(
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
        RecordAcknowledgementDuration(
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
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlTransaction transaction = (SqlTransaction)
            await connection
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);

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
            DocumentCacheWriterResult? lifecycleFence = SelectLifecycleFence(
                request.Purpose,
                lifecycleReadResult
            );
            if (lifecycleFence is not null)
            {
                telemetryOutcome = lifecycleFence.Outcome;
                await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                return lifecycleFence;
            }

            MssqlDocumentCacheWriterCurrentObservation currentObservation = await ReadCurrentObservationAsync(
                    connection,
                    transaction,
                    request.DocumentId,
                    cancellationToken
                )
                .ConfigureAwait(false);
            DocumentCacheWriterCacheAheadIncidentDecision recheckDecision =
                DocumentCacheWriterCacheAheadIncidentFlow.SelectRecheckDecision(
                    request.Purpose,
                    lifecycleReadResult,
                    currentObservation.ToCurrentState(),
                    BuildCandidateObservation(request, currentObservation)
                );

            if (recheckDecision.TerminalResult is not null)
            {
                telemetryOutcome = recheckDecision.TerminalResult.Outcome;
                await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                return recheckDecision.TerminalResult;
            }

            int latchRows = await SetCacheAheadLatchAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);

            await ObserveFaultInjectionAsync(
                    DocumentCacheWriterFaultInjectionHook.AfterCacheAheadLatchUpdateBeforeIncidentCommit,
                    request,
                    lifecycleReadResult,
                    DocumentCacheWriterOutcome.CacheAheadLatchSet,
                    connection,
                    transaction,
                    cacheDmlRowCount: null,
                    acknowledgementRowCount: null,
                    cacheAheadLatchRowCount: latchRows,
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);

            await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
            transactionCompleted = true;

            DocumentCacheWriterResult result = DocumentCacheWriterCacheAheadIncidentFlow.CompleteLatchUpdate(
                recheckDecision,
                latchRows
            );
            telemetryOutcome = result.Outcome;
            return result;
        }
        catch
        {
            await RollbackIfNeededAsync(transaction, transactionCompleted, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (telemetryOutcome is not null)
            {
                RecordTransactionDuration(
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

    private static async Task<DocumentCacheLifecycleReadResult> ReadLifecycleAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        await using SqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Missing,
                "DocumentCache lifecycle state row is missing."
            );
        }

        string lifecycleText = reader.GetString(0);
        bool cacheAheadRecoveryRequired = reader.GetBoolean(1);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Invalid,
                "DocumentCache lifecycle state returned multiple rows."
            );
        }

        return Enum.TryParse(lifecycleText, ignoreCase: false, out DocumentCacheLifecycleState lifecycleState)
            ? DocumentCacheLifecycleReadResult.Success(
                new DocumentCacheLifecycleObservation(lifecycleState, cacheAheadRecoveryRequired)
            )
            : DocumentCacheLifecycleReadResult.Failure(
                DocumentCacheLifecycleReadStatus.Invalid,
                "DocumentCache lifecycle state is unsupported."
            );
    }

    private static async Task<MssqlDocumentCacheWriterCurrentObservation> ReadCurrentObservationAsync(
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

        var observation = new MssqlDocumentCacheWriterCurrentObservation(
            GetNullableInt64(reader, "SourceContentVersion"),
            GetNullableInt64(reader, "CacheContentVersion"),
            GetNullableInt64(reader, "WorkRequiredContentVersion"),
            GetNullableGuid(reader, "SourceDocumentUuid"),
            GetNullableInt16(reader, "SourceResourceKeyId"),
            GetNullableString(reader, "SourceProjectName"),
            GetNullableString(reader, "SourceResourceName"),
            GetNullableString(reader, "SourceResourceVersion")
        );

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "DocumentCache writer current-state observation returned multiple rows."
            );
        }

        return observation;
    }

    private static DocumentCacheWriterCandidateObservation BuildCandidateObservation(
        DocumentCacheWriterRequest request,
        MssqlDocumentCacheWriterCurrentObservation currentObservation
    )
    {
        DocumentCacheMaterializationCandidate? candidate = request.Candidate;
        if (candidate is null)
        {
            return DocumentCacheWriterCandidateObservation.Absent;
        }

        return new DocumentCacheWriterCandidateObservation(
            candidate,
            CompareCandidateMetadata(request.TargetContext.MappingSet, candidate, currentObservation)
        );
    }

    private static DocumentCacheWriterCandidateMetadataComparison CompareCandidateMetadata(
        MappingSet mappingSet,
        DocumentCacheMaterializationCandidate candidate,
        MssqlDocumentCacheWriterCurrentObservation currentObservation
    )
    {
        if (currentObservation.SourceContentVersion is null)
        {
            return DocumentCacheWriterCandidateMetadataComparison.MatchesCurrentSource;
        }

        if (currentObservation.SourceDocumentUuid != candidate.DocumentUuid.Value)
        {
            return DocumentCacheWriterCandidateMetadataComparison.DocumentUuidMismatch;
        }

        if (
            !string.Equals(
                currentObservation.SourceProjectName,
                candidate.ProjectName,
                StringComparison.Ordinal
            )
            || !string.Equals(
                currentObservation.SourceResourceName,
                candidate.ResourceName,
                StringComparison.Ordinal
            )
            || !string.Equals(
                currentObservation.SourceResourceVersion,
                candidate.ResourceVersion,
                StringComparison.Ordinal
            )
        )
        {
            return DocumentCacheWriterCandidateMetadataComparison.ResourceMetadataMismatch;
        }

        if (
            currentObservation.SourceResourceKeyId is null
            || !mappingSet.ResourceKeyById.TryGetValue(
                currentObservation.SourceResourceKeyId.Value,
                out ResourceKeyEntry? resourceKey
            )
            || resourceKey.ResourceKeyId != currentObservation.SourceResourceKeyId.Value
            || !string.Equals(
                resourceKey.Resource.ProjectName,
                candidate.ProjectName,
                StringComparison.Ordinal
            )
            || !string.Equals(
                resourceKey.Resource.ResourceName,
                candidate.ResourceName,
                StringComparison.Ordinal
            )
            || !string.Equals(
                resourceKey.ResourceVersion,
                candidate.ResourceVersion,
                StringComparison.Ordinal
            )
        )
        {
            return DocumentCacheWriterCandidateMetadataComparison.TargetMappingMismatch;
        }

        return DocumentCacheWriterCandidateMetadataComparison.MatchesCurrentSource;
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

    private static async Task<int> SetCacheAheadLatchAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE [dms].[DocumentCacheState]
            SET [CacheAheadRecoveryRequired] = CAST(1 AS bit)
            WHERE [StateId] = 1
              AND [ProjectionLifecycleState] IN ('Tracking', 'Rebuilding')
              AND [CacheAheadRecoveryRequired] = CAST(0 AS bit);
            """;

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private void RecordTransactionDuration(
        DocumentCacheWriterRequest request,
        DocumentCacheLifecycleState? lifecycleState,
        DocumentCacheWriterOutcome outcome,
        long startTimestamp
    )
    {
        _telemetry.RecordTransactionDuration(
            CreateMetricContext(request, lifecycleState, outcome),
            DocumentCacheWriterTelemetry.GetElapsedTime(startTimestamp)
        );
    }

    private void RecordCacheDmlDuration(
        DocumentCacheWriterRequest request,
        DocumentCacheLifecycleState? lifecycleState,
        DocumentCacheWriterOutcome outcome,
        long startTimestamp
    )
    {
        TimeSpan duration = DocumentCacheWriterTelemetry.GetElapsedTime(startTimestamp);
        DocumentCacheWriterMetricContext context = CreateMetricContext(request, lifecycleState, outcome);
        _telemetry.RecordCacheDmlDuration(context, duration);
        _telemetry.RecordSameDocumentWait(
            context,
            DocumentCacheWriterContentionParticipant.CacheWriter,
            DocumentCacheWriterContentionPhase.CacheDml,
            duration
        );
    }

    private void RecordAcknowledgementDuration(
        DocumentCacheWriterRequest request,
        DocumentCacheLifecycleState? lifecycleState,
        DocumentCacheWriterOutcome outcome,
        long startTimestamp
    )
    {
        TimeSpan duration = DocumentCacheWriterTelemetry.GetElapsedTime(startTimestamp);
        DocumentCacheWriterMetricContext context = CreateMetricContext(request, lifecycleState, outcome);
        _telemetry.RecordAcknowledgementDuration(context, duration);
        _telemetry.RecordSameDocumentWait(
            context,
            DocumentCacheWriterContentionParticipant.CacheWriter,
            DocumentCacheWriterContentionPhase.Acknowledgement,
            duration
        );
    }

    private static DocumentCacheWriterMetricContext CreateMetricContext(
        DocumentCacheWriterRequest request,
        DocumentCacheLifecycleState? lifecycleState,
        DocumentCacheWriterOutcome outcome
    ) =>
        DocumentCacheWriterMetricContext.ForCacheWriter(
            RelationalProviderToken.SqlServer,
            request.TargetContext.TargetKey,
            request.Purpose,
            lifecycleState,
            outcome
        );

    private static DocumentCacheWriterResult? SelectLifecycleFence(
        DocumentCacheWriterPurpose purpose,
        DocumentCacheLifecycleReadResult lifecycleReadResult
    )
    {
        DocumentCacheWriterClassificationSelection selection =
            DocumentCacheWriterClassificationSelector.Select(
                new DocumentCacheWriterClassificationRequest(
                    purpose,
                    lifecycleReadResult,
                    new DocumentCacheWriterCurrentStateObservation(
                        sourceContentVersion: null,
                        cacheContentVersion: null,
                        workRequiredContentVersion: null
                    ),
                    DocumentCacheWriterCandidateObservation.Absent
                )
            );

        return selection.Outcome == DocumentCacheWriterOutcome.LifecycleOrLatchFenced
            ? selection.TerminalResult
            : null;
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
            new SqlParameter("@projectName", SqlDbType.VarChar, 256) { Value = candidate.ProjectName }
        );
        command.Parameters.Add(
            new SqlParameter("@resourceName", SqlDbType.VarChar, 256) { Value = candidate.ResourceName }
        );
        command.Parameters.Add(
            new SqlParameter("@resourceVersion", SqlDbType.VarChar, 32) { Value = candidate.ResourceVersion }
        );
        command.Parameters.Add(
            new SqlParameter("@streamEtag", SqlDbType.VarChar, 128) { Value = candidate.StreamEtag }
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

    private static async Task CommitAsync(SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RollbackAsync(SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RollbackIfNeededAsync(
        SqlTransaction transaction,
        bool transactionCompleted,
        CancellationToken cancellationToken
    )
    {
        if (transactionCompleted)
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
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

    private static bool IsRetryableDeleteRace(SqlException exception) =>
        exception.Number == ForeignKeyConstraintViolationNumber
        || (
            exception.Number == ThrowStatementNumber
            && exception.Message.StartsWith(
                "dms.DocumentCache.DocumentUuid diverges from the owning dms.Document row",
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
}
