// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
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
    ILogger<PostgresqlDocumentCacheWriter> logger
) : IDocumentCacheWriter
{
    private readonly NpgsqlDataSourceCache _dataSourceCache =
        dataSourceCache ?? throw new ArgumentNullException(nameof(dataSourceCache));
    private readonly IDocumentCacheWriterRetryAdapter _retryAdapter =
        retryAdapter ?? throw new ArgumentNullException(nameof(retryAdapter));
    private readonly ILogger<PostgresqlDocumentCacheWriter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public Task<DocumentCacheWriterResult> WriteAsync(DocumentCacheWriterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string connectionString = RequireTargetConnectionString(request);

        _logger.LogDebug(
            "Executing PostgreSQL DocumentCache writer for target {TargetKey} with purpose {Purpose}",
            LoggingSanitizer.SanitizeForLogging(request.TargetContext.TargetKey.ToString()),
            request.Purpose
        );

        return _retryAdapter.ExecuteAsync(
            new DocumentCacheWriterRetryRequest(
                RelationalProviderToken.Postgresql,
                request.TargetContext.TargetKey,
                request.Purpose,
                request.CancellationToken
            ),
            (_, cancellationToken) => ExecuteAttemptAsync(request, connectionString, cancellationToken)
        );
    }

    private async Task<DocumentCacheWriterResult> ExecuteAttemptAsync(
        DocumentCacheWriterRequest request,
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        NpgsqlDataSource dataSource = _dataSourceCache.GetOrCreate(connectionString);
        await using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        var transactionCompleted = false;

        try
        {
            DocumentCacheLifecycleReadResult lifecycleReadResult = await ReadLifecycleForShareAsync(
                    connection,
                    transaction,
                    cancellationToken
                )
                .ConfigureAwait(false);

            DocumentCacheWriterResult? lifecycleFence = SelectLifecycleFence(
                request.Purpose,
                lifecycleReadResult
            );
            if (lifecycleFence is not null)
            {
                await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                return lifecycleFence;
            }

            // PostgreSQL lock order: hold the shared lifecycle row lock, observe current
            // Document/ResourceKey/DocumentCache/DocumentProjectionWork without deliberately
            // row-locking work, perform cache DML against DocumentCache/source rows, then delete
            // matching work as the final commit gate. Duplicate writers and enqueue/acknowledge
            // races therefore meet on the cache row or final work delete, not on a pre-held work lock.
            PostgresqlDocumentCacheWriterCurrentObservation currentObservation =
                await ReadCurrentObservationAsync(
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
                await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                return selection.TerminalResult!;
            }

            if (selection.RequestsCacheAheadLatchFlow)
            {
                await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                return await ConfirmCacheAheadAsync(request, connectionString, cancellationToken)
                    .ConfigureAwait(false);
            }

            DocumentCacheWriterResult result = selection.WritesCache
                ? await WriteCandidateAndAcknowledgeAsync(
                        connection,
                        transaction,
                        selection,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
                : await AcknowledgeAlreadyCurrentAsync(
                        connection,
                        transaction,
                        request.DocumentId,
                        selection.ExpectedContentVersion!.Value,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

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
        catch (PostgresException exception) when (IsRetryableDeleteRace(exception))
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
    }

    private static async Task<DocumentCacheWriterResult> WriteCandidateAndAcknowledgeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DocumentCacheWriterClassificationSelection selection,
        CancellationToken cancellationToken
    )
    {
        DocumentCacheMaterializationCandidate candidate = selection.Candidate!;
        long expectedContentVersion = selection.ExpectedContentVersion!.Value;

        int cacheRows = await ExecuteCacheWriteAsync(connection, transaction, candidate, cancellationToken)
            .ConfigureAwait(false);
        int acknowledgedRows = await ExecuteAcknowledgementAsync(
                connection,
                transaction,
                candidate.DocumentId,
                expectedContentVersion,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (acknowledgedRows == 1)
        {
            return cacheRows > 0
                ? new DocumentCacheWriterResult.CandidateWrittenAcknowledged(
                    candidate,
                    expectedContentVersion
                )
                : new DocumentCacheWriterResult.AlreadyCurrentAcknowledged(expectedContentVersion);
        }

        return DocumentCacheWriterResult.RacingWriterLost.Instance;
    }

    private static async Task<DocumentCacheWriterResult> AcknowledgeAlreadyCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long documentId,
        long expectedContentVersion,
        CancellationToken cancellationToken
    )
    {
        int acknowledgedRows = await ExecuteAcknowledgementAsync(
                connection,
                transaction,
                documentId,
                expectedContentVersion,
                cancellationToken
            )
            .ConfigureAwait(false);

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
        NpgsqlDataSource dataSource = _dataSourceCache.GetOrCreate(connectionString);
        await using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        var transactionCompleted = false;

        try
        {
            DocumentCacheLifecycleReadResult lifecycleReadResult = await ReadLifecycleForUpdateAsync(
                    connection,
                    transaction,
                    cancellationToken
                )
                .ConfigureAwait(false);
            DocumentCacheWriterResult? lifecycleFence = SelectLifecycleFence(
                request.Purpose,
                lifecycleReadResult
            );
            if (lifecycleFence is not null)
            {
                await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                return lifecycleFence;
            }

            PostgresqlDocumentCacheWriterCurrentObservation currentObservation =
                await ReadCurrentObservationAsync(
                        connection,
                        transaction,
                        request.DocumentId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            DocumentCacheWriterClassificationSelection recheckSelection =
                DocumentCacheWriterClassificationSelector.Select(
                    new DocumentCacheWriterClassificationRequest(
                        request.Purpose,
                        lifecycleReadResult,
                        currentObservation.ToCurrentState(),
                        BuildCandidateObservation(request, currentObservation)
                    )
                );

            if (!recheckSelection.RequestsCacheAheadLatchFlow)
            {
                await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                return DocumentCacheWriterResult.CacheAheadDisappeared.Instance;
            }

            int latchRows = await SetCacheAheadLatchAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (latchRows != 1)
            {
                await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                transactionCompleted = true;
                return new DocumentCacheWriterResult.LifecycleOrLatchFenced(
                    DocumentCacheWriterFenceReason.CacheAheadRecoveryRequired,
                    lifecycleReadResult.Lifecycle!.State,
                    cacheAheadRecoveryRequired: true
                );
            }

            await CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
            transactionCompleted = true;

            return new DocumentCacheWriterResult.CacheAheadLatchSet(
                currentObservation.SourceContentVersion!.Value,
                currentObservation.CacheContentVersion!.Value
            );
        }
        catch
        {
            await RollbackIfNeededAsync(transaction, transactionCompleted, cancellationToken)
                .ConfigureAwait(false);
            throw;
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
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        await using NpgsqlDataReader reader = await command
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

    private static async Task<PostgresqlDocumentCacheWriterCurrentObservation> ReadCurrentObservationAsync(
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

        var observation = new PostgresqlDocumentCacheWriterCurrentObservation(
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
        PostgresqlDocumentCacheWriterCurrentObservation currentObservation
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
        PostgresqlDocumentCacheWriterCurrentObservation currentObservation
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

    private static async Task<int> SetCacheAheadLatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE "dms"."DocumentCacheState"
            SET "CacheAheadRecoveryRequired" = true
            WHERE "StateId" = 1
              AND "ProjectionLifecycleState" IN ('Tracking', 'Rebuilding')
              AND "CacheAheadRecoveryRequired" = false;
            """;

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

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

    private static async Task CommitAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RollbackAsync(
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RollbackIfNeededAsync(
        NpgsqlTransaction transaction,
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

    private static bool IsRetryableDeleteRace(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.ForeignKeyViolation
        || (
            exception.SqlState == PostgresErrorCodes.RaiseException
            && exception.MessageText.StartsWith(
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

    private static long? GetNullableInt64(NpgsqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static short? GetNullableInt16(NpgsqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt16(ordinal);
    }

    private static Guid? GetNullableGuid(NpgsqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    private static string? GetNullableString(NpgsqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private sealed record PostgresqlDocumentCacheWriterCurrentObservation(
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
