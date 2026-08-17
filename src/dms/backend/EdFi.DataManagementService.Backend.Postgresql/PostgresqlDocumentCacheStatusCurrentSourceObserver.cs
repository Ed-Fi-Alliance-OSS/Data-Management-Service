// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DataManagementService.Backend.Postgresql;

internal sealed class PostgresqlDocumentCacheStatusCurrentSourceObserver(
    NpgsqlDataSourceCache dataSourceCache,
    IDocumentCacheProviderCommandTimeoutClassifier timeoutClassifier,
    ILogger<PostgresqlDocumentCacheStatusCurrentSourceObserver> logger
) : IDocumentCacheStatusCurrentSourceObserver
{
    private const string StateAlias = "state";
    private const string WorkAlias = "work";

    private static readonly string _stateTable = SqlIdentifierQuoter.QuoteTableName(
        SqlDialect.Pgsql,
        DocumentCacheInventoryDefinition.DocumentCacheState
    );
    private static readonly string _workTable = SqlIdentifierQuoter.QuoteTableName(
        SqlDialect.Pgsql,
        DocumentCacheInventoryDefinition.DocumentProjectionWork
    );
    private static readonly DbColumnName _stateIdColumn = DocumentCacheInventoryDefinition
        .DocumentCacheStateColumns
        .StateId;
    private static readonly DbColumnName _lifecycleColumn = DocumentCacheInventoryDefinition
        .DocumentCacheStateColumns
        .ProjectionLifecycleState;
    private static readonly DbColumnName _cacheAheadColumn = DocumentCacheInventoryDefinition
        .DocumentCacheStateColumns
        .CacheAheadRecoveryRequired;
    private static readonly DbColumnName _documentIdColumn = DocumentCacheInventoryDefinition
        .DocumentProjectionWorkColumns
        .DocumentId;
    private static readonly DbColumnName _firstEnqueuedAtColumn = DocumentCacheInventoryDefinition
        .DocumentProjectionWorkColumns
        .FirstEnqueuedAt;

    private static readonly string _stateId = Quote(_stateIdColumn);
    private static readonly string _lifecycle = Quote(_lifecycleColumn);
    private static readonly string _cacheAhead = Quote(_cacheAheadColumn);
    private static readonly string _documentId = Quote(_documentIdColumn);
    private static readonly string _firstEnqueuedAt = Quote(_firstEnqueuedAtColumn);

    internal const string DurableObservedAtColumnName = "DurableObservedAt";
    internal const string LifecycleColumnName = "ProjectionLifecycleState";
    internal const string CacheAheadRecoveryRequiredColumnName = "CacheAheadRecoveryRequired";
    internal const string HasWorkColumnName = "HasWork";
    internal const string OldestWorkFirstEnqueuedAtColumnName = "OldestWorkFirstEnqueuedAt";
    internal const string OldestWorkAgeSecondsColumnName = "OldestWorkAgeSeconds";

    internal static readonly string StatusObservationSql = $"""
        WITH durable_clock AS (
            SELECT statement_timestamp() AS "{DurableObservedAtColumnName}"
        ),
        state_row AS (
            SELECT {StateAlias}.{_lifecycle}, {StateAlias}.{_cacheAhead}
            FROM {_stateTable} AS {StateAlias}
            WHERE {StateAlias}.{_stateId} = 1
        ),
        oldest_work AS (
            SELECT {WorkAlias}.{_documentId}, {WorkAlias}.{_firstEnqueuedAt}
            FROM {_workTable} AS {WorkAlias}
            ORDER BY {WorkAlias}.{_firstEnqueuedAt}, {WorkAlias}.{_documentId}
            LIMIT 1
        )
        SELECT
            durable_clock."{DurableObservedAtColumnName}",
            state_row.{_lifecycle} AS "{LifecycleColumnName}",
            state_row.{_cacheAhead} AS "{CacheAheadRecoveryRequiredColumnName}",
            oldest_work.{_documentId} IS NOT NULL AS "{HasWorkColumnName}",
            oldest_work.{_firstEnqueuedAt} AS "{OldestWorkFirstEnqueuedAtColumnName}",
            CASE
                WHEN oldest_work.{_firstEnqueuedAt} IS NULL THEN NULL
                ELSE GREATEST(
                    EXTRACT(EPOCH FROM (durable_clock."{DurableObservedAtColumnName}" - oldest_work.{_firstEnqueuedAt})),
                    0
                )::double precision
            END AS "{OldestWorkAgeSecondsColumnName}"
        FROM durable_clock
        LEFT JOIN state_row ON TRUE
        LEFT JOIN oldest_work ON TRUE;
        """;

    private readonly NpgsqlDataSourceCache _dataSourceCache =
        dataSourceCache ?? throw new ArgumentNullException(nameof(dataSourceCache));
    private readonly IDocumentCacheProviderCommandTimeoutClassifier _timeoutClassifier =
        timeoutClassifier ?? throw new ArgumentNullException(nameof(timeoutClassifier));
    private readonly ILogger<PostgresqlDocumentCacheStatusCurrentSourceObserver> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public RelationalProviderToken ProviderToken => RelationalProviderToken.Postgresql;

    public async Task<DocumentCacheStatusCurrentSourceObservationResult> ObserveAsync(
        DocumentCacheStatusCurrentSourceObservationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        string connectionString = DocumentCacheStatusCurrentSourceObservationGuard.RequireConnectionString(
            request,
            ProviderToken
        );

        try
        {
            NpgsqlDataSource dataSource = _dataSourceCache.GetOrCreate(connectionString);
            await using NpgsqlConnection connection = await dataSource
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlCommand command = connection.CreateCommand();

            command.CommandText = StatusObservationSql;
            command.CommandTimeout = GetCommandTimeoutSeconds(
                request.TargetExecutionContext.EffectiveSettings.StatusObservationTimeout
            );

            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            return await ReadResultAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return DocumentCacheStatusCurrentSourceObservationResult.Cancelled(
                "PostgreSQL DocumentCache status current-source observation was cancelled."
            );
        }
        catch (Exception exception) when (_timeoutClassifier.IsProviderCommandTimeout(exception))
        {
            LogObservationFailure(
                exception,
                DocumentCacheStatusCurrentSourceObservationOutcome.ProviderTimeout
            );
            return DocumentCacheStatusCurrentSourceObservationResult.ProviderTimeout(
                "PostgreSQL DocumentCache status current-source observation timed out."
            );
        }
        catch (Exception exception)
        {
            LogObservationFailure(exception, DocumentCacheStatusCurrentSourceObservationOutcome.Failed);
            return DocumentCacheStatusCurrentSourceObservationResult.Failed(
                "PostgreSQL DocumentCache status current-source observation failed."
            );
        }
    }

    private static async Task<DocumentCacheStatusCurrentSourceObservationResult> ReadResultAsync(
        NpgsqlDataReader reader,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DocumentCacheStatusCurrentSourceObservationResult.Failed(
                "PostgreSQL DocumentCache status current-source observation returned no rows."
            );
        }

        DateTimeOffset durableObservedAt = ReadRequiredTimestamp(reader, DurableObservedAtColumnName);
        string? lifecycleText = ReadOptionalString(reader, LifecycleColumnName);
        bool? cacheAheadRecoveryRequired = ReadOptionalBoolean(reader, CacheAheadRecoveryRequiredColumnName);

        if (
            lifecycleText is null
            || cacheAheadRecoveryRequired is null
            || !DocumentCacheLifecycleTokenParser.TryParse(
                lifecycleText,
                out DocumentCacheLifecycleState lifecycleState
            )
        )
        {
            return DocumentCacheStatusCurrentSourceObservationResult.StateMissingOrInvalid(
                durableObservedAt,
                "dms.DocumentCacheState singleton row is missing or invalid."
            );
        }

        bool hasWork = ReadRequiredBoolean(reader, HasWorkColumnName);
        DateTimeOffset? oldestWorkFirstEnqueuedAt = ReadOptionalTimestamp(
            reader,
            OldestWorkFirstEnqueuedAtColumnName
        );
        double? oldestWorkAgeSeconds = ReadOptionalDouble(reader, OldestWorkAgeSecondsColumnName);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DocumentCacheStatusCurrentSourceObservationResult.Failed(
                "PostgreSQL DocumentCache status current-source observation returned multiple rows."
            );
        }

        return DocumentCacheStatusCurrentSourceObservationResult.Success(
            lifecycleState,
            cacheAheadRecoveryRequired.Value,
            hasWork
                ? DocumentCacheStatusDurableQueuePresence.NotEmpty
                : DocumentCacheStatusDurableQueuePresence.Empty,
            oldestWorkFirstEnqueuedAt,
            oldestWorkAgeSeconds,
            durableObservedAt
        );
    }

    private static int GetCommandTimeoutSeconds(TimeSpan timeout)
    {
        double timeoutSeconds = Math.Ceiling(timeout.TotalSeconds);
        if (timeoutSeconds < 1)
        {
            return 1;
        }

        return timeoutSeconds > int.MaxValue ? int.MaxValue : (int)timeoutSeconds;
    }

    private void LogObservationFailure(
        Exception exception,
        DocumentCacheStatusCurrentSourceObservationOutcome outcome
    )
    {
        _logger.LogDebug(
            "PostgreSQL DocumentCache status current-source observation failed with outcome {Outcome}; exception type {ExceptionType}",
            outcome,
            exception.GetType().Name
        );
    }

    private static string? ReadOptionalString(NpgsqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static bool ReadRequiredBoolean(NpgsqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.GetBoolean(ordinal);
    }

    private static bool? ReadOptionalBoolean(NpgsqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
    }

    private static DateTimeOffset ReadRequiredTimestamp(NpgsqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return DocumentCacheStatusCurrentSourceObservationGuard.NormalizeUtcTimestamp(
            reader.GetValue(ordinal)
        );
    }

    private static DateTimeOffset? ReadOptionalTimestamp(NpgsqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal)
            ? null
            : DocumentCacheStatusCurrentSourceObservationGuard.NormalizeUtcTimestamp(
                reader.GetValue(ordinal)
            );
    }

    private static double? ReadOptionalDouble(NpgsqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    private static string Quote(DbColumnName column) =>
        SqlIdentifierQuoter.QuoteIdentifier(SqlDialect.Pgsql, column);
}
