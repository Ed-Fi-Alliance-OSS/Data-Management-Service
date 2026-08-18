// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend.Mssql;

internal sealed class MssqlDocumentCacheStatusCurrentSourceObserver(
    IDocumentCacheProviderCommandTimeoutClassifier timeoutClassifier,
    ILogger<MssqlDocumentCacheStatusCurrentSourceObserver> logger
) : IDocumentCacheStatusCurrentSourceObserver
{
    private static readonly string _stateAlias = SqlIdentifierQuoter.QuoteIdentifier(
        SqlDialect.Mssql,
        "state"
    );
    private static readonly string _workAlias = SqlIdentifierQuoter.QuoteIdentifier(SqlDialect.Mssql, "work");
    private static readonly string _stateTable = SqlIdentifierQuoter.QuoteTableName(
        SqlDialect.Mssql,
        DocumentCacheInventoryDefinition.DocumentCacheState
    );
    private static readonly string _workTable = SqlIdentifierQuoter.QuoteTableName(
        SqlDialect.Mssql,
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
            SELECT SYSUTCDATETIME() AS [{DurableObservedAtColumnName}]
        ),
        state_row AS (
            SELECT {_stateAlias}.{_lifecycle}, {_stateAlias}.{_cacheAhead}
            FROM {_stateTable} AS {_stateAlias}
            WHERE {_stateAlias}.{_stateId} = 1
        ),
        oldest_work AS (
            SELECT TOP (1) {_workAlias}.{_documentId}, {_workAlias}.{_firstEnqueuedAt}
            FROM {_workTable} AS {_workAlias}
            ORDER BY {_workAlias}.{_firstEnqueuedAt}, {_workAlias}.{_documentId}
        )
        SELECT
            durable_clock.[{DurableObservedAtColumnName}],
            state_row.{_lifecycle} AS [{LifecycleColumnName}],
            state_row.{_cacheAhead} AS [{CacheAheadRecoveryRequiredColumnName}],
            CAST(CASE WHEN oldest_work.{_documentId} IS NOT NULL THEN 1 ELSE 0 END AS bit) AS [{HasWorkColumnName}],
            oldest_work.{_firstEnqueuedAt} AS [{OldestWorkFirstEnqueuedAtColumnName}],
            CASE
                WHEN oldest_work.{_firstEnqueuedAt} IS NULL THEN NULL
                WHEN DATEDIFF_BIG(NANOSECOND, oldest_work.{_firstEnqueuedAt}, durable_clock.[{DurableObservedAtColumnName}]) < 0
                    THEN CONVERT(float, 0)
                ELSE CONVERT(float, DATEDIFF_BIG(NANOSECOND, oldest_work.{_firstEnqueuedAt}, durable_clock.[{DurableObservedAtColumnName}])) / 1000000000.0
            END AS [{OldestWorkAgeSecondsColumnName}]
        FROM durable_clock
        LEFT JOIN state_row ON 1 = 1
        LEFT JOIN oldest_work ON 1 = 1;
        """;

    private readonly IDocumentCacheProviderCommandTimeoutClassifier _timeoutClassifier =
        timeoutClassifier ?? throw new ArgumentNullException(nameof(timeoutClassifier));
    private readonly ILogger<MssqlDocumentCacheStatusCurrentSourceObserver> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public RelationalProviderToken ProviderToken => RelationalProviderToken.SqlServer;

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
            await using SqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqlCommand command = connection.CreateCommand();

            command.CommandText = StatusObservationSql;
            command.CommandTimeout = GetCommandTimeoutSeconds(
                request.TargetExecutionContext.EffectiveSettings.StatusObservationTimeout
            );

            await using SqlDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            return await ReadResultAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return DocumentCacheStatusCurrentSourceObservationResult.Cancelled(
                "SQL Server DocumentCache status current-source observation was cancelled."
            );
        }
        catch (Exception exception) when (_timeoutClassifier.IsProviderCommandTimeout(exception))
        {
            LogObservationFailure(
                exception,
                DocumentCacheStatusCurrentSourceObservationOutcome.ProviderTimeout
            );
            return DocumentCacheStatusCurrentSourceObservationResult.ProviderTimeout(
                "SQL Server DocumentCache status current-source observation timed out."
            );
        }
        catch (Exception exception)
        {
            LogObservationFailure(exception, DocumentCacheStatusCurrentSourceObservationOutcome.Failed);
            return DocumentCacheStatusCurrentSourceObservationResult.Failed(
                "SQL Server DocumentCache status current-source observation failed."
            );
        }
    }

    private static async Task<DocumentCacheStatusCurrentSourceObservationResult> ReadResultAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DocumentCacheStatusCurrentSourceObservationResult.Failed(
                "SQL Server DocumentCache status current-source observation returned no rows."
            );
        }

        DateTimeOffset durableObservedAt = ReadRequiredTimestamp(reader, DurableObservedAtColumnName);
        string? lifecycleText = ReadOptionalString(reader, LifecycleColumnName);
        bool? cacheAheadRecoveryRequired = ReadOptionalBoolean(reader, CacheAheadRecoveryRequiredColumnName);
        bool hasWork = ReadRequiredBoolean(reader, HasWorkColumnName);
        DateTimeOffset? oldestWorkFirstEnqueuedAt = ReadOptionalTimestamp(
            reader,
            OldestWorkFirstEnqueuedAtColumnName
        );
        double? oldestWorkAgeSeconds = ReadOptionalDouble(reader, OldestWorkAgeSecondsColumnName);
        DocumentCacheStatusDurableQueuePresence queuePresence = hasWork
            ? DocumentCacheStatusDurableQueuePresence.NotEmpty
            : DocumentCacheStatusDurableQueuePresence.Empty;

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DocumentCacheStatusCurrentSourceObservationResult.Failed(
                "SQL Server DocumentCache status current-source observation returned multiple rows."
            );
        }

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
                queuePresence,
                oldestWorkFirstEnqueuedAt,
                oldestWorkAgeSeconds,
                "dms.DocumentCacheState singleton row is missing or invalid."
            );
        }

        return DocumentCacheStatusCurrentSourceObservationResult.Success(
            lifecycleState,
            cacheAheadRecoveryRequired.Value,
            queuePresence,
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
            "SQL Server DocumentCache status current-source observation failed with outcome {Outcome}; exception type {ExceptionType}",
            outcome,
            exception.GetType().Name
        );
    }

    private static string? ReadOptionalString(SqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static bool ReadRequiredBoolean(SqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.GetBoolean(ordinal);
    }

    private static bool? ReadOptionalBoolean(SqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
    }

    private static DateTimeOffset ReadRequiredTimestamp(SqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return DocumentCacheStatusCurrentSourceObservationGuard.NormalizeUtcTimestamp(
            reader.GetValue(ordinal)
        );
    }

    private static DateTimeOffset? ReadOptionalTimestamp(SqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal)
            ? null
            : DocumentCacheStatusCurrentSourceObservationGuard.NormalizeUtcTimestamp(
                reader.GetValue(ordinal)
            );
    }

    private static double? ReadOptionalDouble(SqlDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    private static string Quote(DbColumnName column) =>
        SqlIdentifierQuoter.QuoteIdentifier(SqlDialect.Mssql, column);
}
