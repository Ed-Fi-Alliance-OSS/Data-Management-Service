// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Npgsql;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Control;

/// <summary>
/// Outcome of one pre-binding eligibility read. Every outcome other than <see cref="Succeeded"/> is
/// absent evidence: the observation composed from it reports an unknown lifecycle and blocking row
/// presence, so admission stays closed rather than passing on a read that did not happen.
/// </summary>
public enum CdcEligibilityReadOutcome
{
    Succeeded,

    /// <summary>A DocumentCache table the eligibility gate depends on is not present.</summary>
    SchemaIncomplete,

    /// <summary>The database could not be reached, or answered something the gate cannot read.</summary>
    Unreadable,
}

/// <summary>
/// The durable facts one eligibility transaction observed. Every field comes from the same read, so
/// the lifecycle, the cache-ahead latch, and the three row-presence facts describe one state of the
/// database rather than three states observed in sequence.
/// </summary>
public sealed record CdcEligibilityReading(
    DateTimeOffset DurableObservedAt,
    string ProviderConsistencyToken,
    string? LifecycleStateToken,
    bool? CacheAheadRecoveryRequired,
    bool CanonicalRowsPresent,
    bool CacheRowsPresent,
    bool WorkRowsPresent,
    string? SourceIdentity
);

/// <summary>
/// Result of one eligibility read. <see cref="Summary"/> is composed by the probe from what it
/// attempted; a provider error message never reaches it, because those quote connection settings.
/// </summary>
public sealed record CdcEligibilityReadResult(
    CdcEligibilityReadOutcome Outcome,
    CdcEligibilityReading? Reading,
    string? Summary
)
{
    public bool Succeeded => Outcome == CdcEligibilityReadOutcome.Succeeded && Reading is not null;
}

/// <summary>Inputs one eligibility probe runs against.</summary>
/// <remarks>
/// The physical-source fingerprint is discovered by the probe rather than supplied, so
/// <see cref="CdcObservationContext.PhysicalSourceFingerprint"/> is normally null here: this runs
/// before a binding exists, and the fingerprint the probe reads is what the binding will be created
/// against.
/// </remarks>
public sealed record CdcEligibilityProbeRequest(
    CdcObservationContext Context,
    InitialCdcProvisioningProof Proof,
    string ConnectionString
)
{
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Reads the pre-binding eligibility facts from the instance database in one read-only, provider
/// consistent transaction.
/// </summary>
/// <remarks>
/// This is the gate that runs <em>before</em> a binding is created, so it holds no administrative
/// mutex and mutates nothing. The equivalent emptiness check inside the guarded activation command
/// runs under the mutex, after the point where enablement must already have rejected an ineligible
/// database.
/// </remarks>
public interface ICdcEligibilityProbe
{
    CdcProvider Provider { get; }

    Task<InitialCdcEligibilityObservation> ProbeAsync(
        CdcEligibilityProbeRequest request,
        CancellationToken cancellationToken = default
    );
}

internal sealed class CdcEligibilityProbe(
    CdcProvider provider,
    TimeProvider timeProvider,
    ILogger<CdcEligibilityProbe> logger
) : ICdcEligibilityProbe
{
    public CdcProvider Provider => provider;

    public async Task<InitialCdcEligibilityObservation> ProbeAsync(
        CdcEligibilityProbeRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);
        ArgumentNullException.ThrowIfNull(request.Proof);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConnectionString);

        CdcEligibilityReadResult read = await ReadAsync(request, cancellationToken).ConfigureAwait(false);

        return CdcEligibilityObservationMapper.Map(
            request.Context,
            request.Proof,
            read,
            timeProvider.GetUtcNow()
        );
    }

    internal async Task<CdcEligibilityReadResult> ReadAsync(
        CdcEligibilityProbeRequest request,
        CancellationToken cancellationToken
    )
    {
        SqlDialect dialect = Dialect();

        try
        {
            await using DbConnection connection = CreateConnection(request.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Read committed is the right isolation for a probe that must not block a live writer: the
            // evidence is read by a single statement, which each provider answers from one consistent
            // point in time, and the transaction scopes that statement so the reported consistency
            // token identifies the read. On SQL Server that point-in-time answer comes from
            // READ_COMMITTED_SNAPSHOT, which the DMS provisioner requires of every instance database.
            await using DbTransaction transaction = await connection
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);

            if (CdcEligibilitySql.RenderReadOnlyTransactionCommandText(dialect) is { } readOnlyCommandText)
            {
                await ExecuteNonQueryAsync(
                        connection,
                        transaction,
                        readOnlyCommandText,
                        request.CommandTimeout,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            IReadOnlySet<string> presentTables = await ReadPresentTablesAsync(
                    connection,
                    transaction,
                    dialect,
                    request.CommandTimeout,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (CdcEligibilitySql.MissingRequiredTable(presentTables) is { } missingTable)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Failed(
                    CdcEligibilityReadOutcome.SchemaIncomplete,
                    $"The instance database has no {missingTable} table."
                );
            }

            CdcEligibilityReadResult result = await ReadEvidenceAsync(
                    connection,
                    transaction,
                    dialect,
                    presentTables.Contains(DataStoreIdentityTableDefinition.Table.Name),
                    request.CommandTimeout,
                    cancellationToken
                )
                .ConfigureAwait(false);

            // Nothing was written, so the transaction is rolled back rather than committed: the probe
            // has no state of its own to make durable.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            // Only the exception type is reported. A provider failure message quotes the connection.
            LogEligibilityReadFailure(exception);

            return Failed(
                CdcEligibilityReadOutcome.Unreadable,
                "The instance database did not answer the CDC eligibility read."
            );
        }
    }

    /// <summary>
    /// Reports only the exception type. A provider failure message quotes the connection it failed
    /// on, so the exception itself never reaches the log.
    /// </summary>
    private void LogEligibilityReadFailure(Exception exception) =>
        logger.LogDebug(
            "CDC eligibility probe could not read instance state; exception type {ExceptionType}.",
            exception.GetType().Name
        );

    private static async Task<IReadOnlySet<string>> ReadPresentTablesAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqlDialect dialect,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = CreateCommand(
            connection,
            transaction,
            CdcEligibilitySql.RenderTableExistenceCommandText(dialect),
            commandTimeout
        );
        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        HashSet<string> presentTables = new(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            presentTables.Add(reader.GetString(0));
        }

        return presentTables;
    }

    private static async Task<CdcEligibilityReadResult> ReadEvidenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        SqlDialect dialect,
        bool includeDataStoreIdentity,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = CreateCommand(
            connection,
            transaction,
            CdcEligibilitySql.RenderEvidenceCommandText(dialect, includeDataStoreIdentity),
            commandTimeout
        );
        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Failed(
                CdcEligibilityReadOutcome.Unreadable,
                "The CDC eligibility read returned no evidence row."
            );
        }

        CdcEligibilityReading reading = new(
            DocumentCacheStatusCurrentSourceObservationGuard.NormalizeUtcTimestamp(
                reader.GetValue(reader.GetOrdinal(CdcEligibilitySql.DurableObservedAtColumnName))
            ),
            ReadOptionalString(reader, CdcEligibilitySql.ProviderConsistencyTokenColumnName) ?? string.Empty,
            ReadOptionalString(reader, CdcEligibilitySql.LifecycleStateColumnName),
            ReadOptionalBoolean(reader, CdcEligibilitySql.CacheAheadRecoveryRequiredColumnName),
            ReadRequiredBoolean(reader, CdcEligibilitySql.CanonicalRowsPresentColumnName),
            ReadRequiredBoolean(reader, CdcEligibilitySql.CacheRowsPresentColumnName),
            ReadRequiredBoolean(reader, CdcEligibilitySql.WorkRowsPresentColumnName),
            ReadOptionalString(reader, CdcEligibilitySql.SourceIdentityColumnName)
        );

        return new(CdcEligibilityReadOutcome.Succeeded, reading, null);
    }

    private DbConnection CreateConnection(string connectionString) =>
        provider == CdcProvider.Postgresql
            ? new NpgsqlConnection(connectionString)
            : new SqlConnection(connectionString);

    private SqlDialect Dialect() => provider == CdcProvider.Postgresql ? SqlDialect.Pgsql : SqlDialect.Mssql;

    private static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction transaction,
        string commandText,
        TimeSpan commandTimeout
    )
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.CommandTimeout = CommandTimeoutSeconds(commandTimeout);

        return command;
    }

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        DbTransaction transaction,
        string commandText,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = CreateCommand(connection, transaction, commandText, commandTimeout);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int CommandTimeoutSeconds(TimeSpan commandTimeout) =>
        commandTimeout <= TimeSpan.Zero ? 30 : (int)Math.Ceiling(commandTimeout.TotalSeconds);

    private static string? ReadOptionalString(DbDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static bool? ReadOptionalBoolean(DbDataReader reader, string columnName)
    {
        int ordinal = reader.GetOrdinal(columnName);

        return reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
    }

    private static bool ReadRequiredBoolean(DbDataReader reader, string columnName) =>
        reader.GetBoolean(reader.GetOrdinal(columnName));

    private static CdcEligibilityReadResult Failed(CdcEligibilityReadOutcome outcome, string summary) =>
        new(outcome, null, summary);
}

/// <summary>
/// The read-only statements the eligibility probe issues. They are rendered here so the guarantee the
/// gate depends on — that it observes state without taking the administrative mutex and without
/// mutating anything — is stated in one place and can be asserted directly.
/// </summary>
internal static class CdcEligibilitySql
{
    internal const string LifecycleStateColumnName = "LifecycleState";
    internal const string CacheAheadRecoveryRequiredColumnName = "CacheAheadRecoveryRequired";
    internal const string CanonicalRowsPresentColumnName = "CanonicalRowsPresent";
    internal const string CacheRowsPresentColumnName = "CacheRowsPresent";
    internal const string WorkRowsPresentColumnName = "WorkRowsPresent";
    internal const string SourceIdentityColumnName = "SourceIdentity";
    internal const string DurableObservedAtColumnName = "DurableObservedAt";
    internal const string ProviderConsistencyTokenColumnName = "ProviderConsistencyToken";

    /// <summary>
    /// The DocumentCache tables the gate reads. <c>dms.DataStoreIdentity</c> is deliberately absent:
    /// its absence leaves the physical source unidentified, which the observation reports rather than
    /// treating as an unreadable database.
    /// </summary>
    private static readonly DbTableName[] RequiredTables =
    [
        DocumentCacheInventoryDefinition.DocumentCacheState,
        DocumentCacheInventoryDefinition.Document,
        DocumentCacheInventoryDefinition.DocumentCache,
        DocumentCacheInventoryDefinition.DocumentProjectionWork,
    ];

    /// <summary>
    /// PostgreSQL can enforce the read-only guarantee rather than leaving it to inspection. SQL Server
    /// has no equivalent transaction mode, so there the guarantee rests on the statements themselves.
    /// </summary>
    internal static string? RenderReadOnlyTransactionCommandText(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => "SET TRANSACTION READ ONLY;",
            SqlDialect.Mssql => null,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

    internal static string RenderTableExistenceCommandText(SqlDialect dialect)
    {
        string schemaLiteral = Literal(DocumentCacheInventoryDefinition.DmsSchema.Value);
        string tableLiterals = string.Join(
            ", ",
            RequiredTables
                .Select(table => table.Name)
                .Append(DataStoreIdentityTableDefinition.Table.Name)
                .Select(Literal)
        );

        return dialect switch
        {
            SqlDialect.Pgsql => "SELECT table_name\n"
                + "FROM information_schema.tables\n"
                + $"WHERE table_schema = {schemaLiteral} AND table_name IN ({tableLiterals})",
            SqlDialect.Mssql => "SELECT TABLE_NAME\n"
                + "FROM INFORMATION_SCHEMA.TABLES\n"
                + $"WHERE TABLE_SCHEMA = {schemaLiteral} AND TABLE_NAME IN ({tableLiterals})",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    internal static string? MissingRequiredTable(IReadOnlySet<string> presentTables)
    {
        ArgumentNullException.ThrowIfNull(presentTables);

        foreach (DbTableName table in RequiredTables)
        {
            if (!presentTables.Contains(table.Name))
            {
                return QualifiedName(table);
            }
        }

        return null;
    }

    internal static string QualifiedName(DbTableName table) => $"{table.Schema.Value}.{table.Name}";

    /// <summary>
    /// Every fact the gate needs, read by one statement so the lifecycle, the latch, and the three
    /// row-presence facts describe a single point in time on either provider. The statement also
    /// reports the provider's own clock and a token identifying the read, which is the evidence that
    /// the facts came from one transaction.
    /// </summary>
    internal static string RenderEvidenceCommandText(SqlDialect dialect, bool includeDataStoreIdentity)
    {
        string stateTable = Quote(dialect, DocumentCacheInventoryDefinition.DocumentCacheState);
        string stateIdColumn = Quote(
            dialect,
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.StateId
        );
        string lifecycleColumn = Quote(
            dialect,
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.ProjectionLifecycleState
        );
        string cacheAheadColumn = Quote(
            dialect,
            DocumentCacheInventoryDefinition.DocumentCacheStateColumns.CacheAheadRecoveryRequired
        );
        string documentTable = Quote(dialect, DocumentCacheInventoryDefinition.Document);
        string cacheTable = Quote(dialect, DocumentCacheInventoryDefinition.DocumentCache);
        string workTable = Quote(dialect, DocumentCacheInventoryDefinition.DocumentProjectionWork);

        return dialect switch
        {
            SqlDialect.Pgsql => "SELECT\n"
                + $"    (SELECT {lifecycleColumn} FROM {stateTable} WHERE {stateIdColumn} = 1) AS {Alias(dialect, LifecycleStateColumnName)},\n"
                + $"    (SELECT {cacheAheadColumn} FROM {stateTable} WHERE {stateIdColumn} = 1) AS {Alias(dialect, CacheAheadRecoveryRequiredColumnName)},\n"
                + $"    EXISTS (SELECT 1 FROM {documentTable}) AS {Alias(dialect, CanonicalRowsPresentColumnName)},\n"
                + $"    EXISTS (SELECT 1 FROM {cacheTable}) AS {Alias(dialect, CacheRowsPresentColumnName)},\n"
                + $"    EXISTS (SELECT 1 FROM {workTable}) AS {Alias(dialect, WorkRowsPresentColumnName)},\n"
                + $"    {SourceIdentityProjection(dialect, includeDataStoreIdentity)} AS {Alias(dialect, SourceIdentityColumnName)},\n"
                + $"    statement_timestamp() AS {Alias(dialect, DurableObservedAtColumnName)},\n"
                + $"    pg_catalog.pg_current_snapshot()::text AS {Alias(dialect, ProviderConsistencyTokenColumnName)}",
            SqlDialect.Mssql => "SELECT\n"
                + $"    (SELECT {lifecycleColumn} FROM {stateTable} WHERE {stateIdColumn} = 1) AS {Alias(dialect, LifecycleStateColumnName)},\n"
                + $"    (SELECT {cacheAheadColumn} FROM {stateTable} WHERE {stateIdColumn} = 1) AS {Alias(dialect, CacheAheadRecoveryRequiredColumnName)},\n"
                + $"    CAST(CASE WHEN EXISTS (SELECT TOP (1) 1 FROM {documentTable}) THEN 1 ELSE 0 END AS bit) AS {Alias(dialect, CanonicalRowsPresentColumnName)},\n"
                + $"    CAST(CASE WHEN EXISTS (SELECT TOP (1) 1 FROM {cacheTable}) THEN 1 ELSE 0 END AS bit) AS {Alias(dialect, CacheRowsPresentColumnName)},\n"
                + $"    CAST(CASE WHEN EXISTS (SELECT TOP (1) 1 FROM {workTable}) THEN 1 ELSE 0 END AS bit) AS {Alias(dialect, WorkRowsPresentColumnName)},\n"
                + $"    {SourceIdentityProjection(dialect, includeDataStoreIdentity)} AS {Alias(dialect, SourceIdentityColumnName)},\n"
                + $"    SYSUTCDATETIME() AS {Alias(dialect, DurableObservedAtColumnName)},\n"
                + $"    CONVERT(varchar(64), CURRENT_TRANSACTION_ID()) AS {Alias(dialect, ProviderConsistencyTokenColumnName)}",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string SourceIdentityProjection(SqlDialect dialect, bool includeDataStoreIdentity)
    {
        if (!includeDataStoreIdentity)
        {
            // A database with no identity table has no physical source to name, and the statement must
            // not reference a table that is not there.
            return dialect == SqlDialect.Pgsql ? "CAST(NULL AS text)" : "CAST(NULL AS varchar(64))";
        }

        string identityTable = Quote(dialect, DataStoreIdentityTableDefinition.Table);
        string singletonColumn = Quote(
            dialect,
            DataStoreIdentityTableDefinition.DataStoreIdentitySingletonId
        );
        string sourceIdentityColumn = Quote(dialect, DataStoreIdentityTableDefinition.SourceIdentity);

        return dialect == SqlDialect.Pgsql
            ? $"(SELECT {sourceIdentityColumn}::text FROM {identityTable} WHERE {singletonColumn} = 1)"
            : $"(SELECT CONVERT(varchar(64), {sourceIdentityColumn}) FROM {identityTable} WHERE {singletonColumn} = 1)";
    }

    private static string Quote(SqlDialect dialect, DbTableName table) =>
        SqlIdentifierQuoter.QuoteTableName(dialect, table);

    private static string Quote(SqlDialect dialect, DbColumnName column) =>
        SqlIdentifierQuoter.QuoteIdentifier(dialect, column);

    private static string Alias(SqlDialect dialect, string columnName) =>
        dialect == SqlDialect.Pgsql ? $"\"{columnName}\"" : $"[{columnName}]";

    private static string Literal(string value) => $"'{value.Replace("'", "''")}'";
}

/// <summary>
/// Composes the shared eligibility observation from one read. The observation reports what was
/// observed rather than a verdict: the retry classifier decides what an occupied or non-disabled
/// database means, and it distinguishes row presence from evidence that could not be obtained.
/// </summary>
public static class CdcEligibilityObservationMapper
{
    public static InitialCdcEligibilityObservation Map(
        CdcObservationContext context,
        InitialCdcProvisioningProof proof,
        CdcEligibilityReadResult read,
        DateTimeOffset observedAt
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(read);

        if (!read.Succeeded || read.Reading is not { } reading)
        {
            return Unavailable(context, proof, read.Outcome, observedAt);
        }

        List<CdcDiagnostic> diagnostics = [];

        // A provider clock marginally ahead of the control plane must not turn a good read into an
        // observation that reports durable state it claims to have observed before it existed.
        DateTimeOffset effectiveObservedAt =
            reading.DurableObservedAt > observedAt ? reading.DurableObservedAt : observedAt;

        return new InitialCdcEligibilityObservation(
            CdcJsonContract.CurrentContractVersion,
            context.OperationId,
            effectiveObservedAt,
            reading.DurableObservedAt,
            context.TargetIdentity,
            context.TargetIdentity.Provider,
            ReadFingerprint(context, reading.SourceIdentity, effectiveObservedAt, diagnostics),
            proof.SetupControllerRunId,
            proof.ProofId,
            CdcConsistencyScope.SingleProviderTransaction,
            ReadLifecycleState(reading.LifecycleStateToken, effectiveObservedAt, diagnostics),
            ReadCacheAheadState(reading.CacheAheadRecoveryRequired, effectiveObservedAt, diagnostics),
            reading.CanonicalRowsPresent,
            reading.CacheRowsPresent,
            reading.WorkRowsPresent,
            reading.ProviderConsistencyToken,
            CdcDiagnostic.NormalizeDiagnostics(diagnostics)
        );
    }

    /// <summary>
    /// The observation an unreadable database produces. Row presence has no unknown in the shared
    /// contract, so it is reported as present — the value that blocks — never as absent, and the
    /// unknown lifecycle and unusable consistency token keep the observation from passing its own
    /// contract at all.
    /// </summary>
    private static InitialCdcEligibilityObservation Unavailable(
        CdcObservationContext context,
        InitialCdcProvisioningProof proof,
        CdcEligibilityReadOutcome outcome,
        DateTimeOffset observedAt
    ) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            context.OperationId,
            observedAt,
            observedAt,
            context.TargetIdentity,
            context.TargetIdentity.Provider,
            null,
            proof.SetupControllerRunId,
            proof.ProofId,
            CdcConsistencyScope.SingleProviderTransaction,
            CdcLifecycleState.Unknown,
            CdcCacheAheadState.Unknown,
            canonicalRowsPresent: true,
            cacheRowsPresent: true,
            workRowsPresent: true,
            string.Empty,
            CdcDiagnostic.NormalizeDiagnostics([EvidenceUnavailable(outcome, observedAt)])
        );

    private static CdcLifecycleState ReadLifecycleState(
        string? lifecycleStateToken,
        DateTimeOffset observedAt,
        List<CdcDiagnostic> diagnostics
    )
    {
        if (
            lifecycleStateToken is not null
            && DocumentCacheLifecycleTokenParser.TryParse(
                lifecycleStateToken,
                out DocumentCacheLifecycleState lifecycleState
            )
        )
        {
            return lifecycleState switch
            {
                DocumentCacheLifecycleState.Disabled => CdcLifecycleState.Disabled,
                DocumentCacheLifecycleState.Resetting => CdcLifecycleState.Resetting,
                DocumentCacheLifecycleState.Rebuilding => CdcLifecycleState.Rebuilding,
                _ => CdcLifecycleState.Tracking,
            };
        }

        diagnostics.Add(
            new CdcDiagnostic(
                "eligibilityLifecycleUnreadable",
                CdcDiagnosticCategory.StatusObservationUnavailable,
                CdcDiagnosticSeverity.Warning,
                CdcDiagnosticComponent.Projection,
                observedAt,
                "CDC eligibility could not read an authoritative DocumentCache lifecycle state.",
                retryable: true,
                artifactKind: "documentCacheState",
                artifactName: QualifiedName(DocumentCacheInventoryDefinition.DocumentCacheState),
                expected: "a single authoritative lifecycle row",
                observed: lifecycleStateToken is null ? "absent" : "unrecognized"
            ).WithPath("$.lifecycleState")
        );

        return CdcLifecycleState.Unknown;
    }

    private static CdcCacheAheadState ReadCacheAheadState(
        bool? cacheAheadRecoveryRequired,
        DateTimeOffset observedAt,
        List<CdcDiagnostic> diagnostics
    )
    {
        if (cacheAheadRecoveryRequired is { } latch)
        {
            return latch ? CdcCacheAheadState.RecoveryRequired : CdcCacheAheadState.Clear;
        }

        diagnostics.Add(
            new CdcDiagnostic(
                "eligibilityCacheAheadLatchUnreadable",
                CdcDiagnosticCategory.StatusObservationUnavailable,
                CdcDiagnosticSeverity.Warning,
                CdcDiagnosticComponent.Projection,
                observedAt,
                "CDC eligibility could not read an authoritative cache-ahead latch.",
                retryable: true,
                artifactKind: "documentCacheState",
                artifactName: QualifiedName(DocumentCacheInventoryDefinition.DocumentCacheState),
                expected: "a single authoritative cache-ahead latch",
                observed: "absent"
            ).WithPath("$.cacheAheadState")
        );

        return CdcCacheAheadState.Unknown;
    }

    private static string? ReadFingerprint(
        CdcObservationContext context,
        string? sourceIdentity,
        DateTimeOffset observedAt,
        List<CdcDiagnostic> diagnostics
    )
    {
        if (sourceIdentity is not null)
        {
            try
            {
                return Ddl
                    .CdcSourceFingerprintMetadata.Compute(
                        ToDdlProvider(context.TargetIdentity.Provider),
                        sourceIdentity
                    )
                    .Value;
            }
            catch (ArgumentException)
            {
                // The exception carries the offending identity, so only the rejection is reported.
                diagnostics.Add(SourceIdentityUnusable(observedAt, "malformed"));
                return null;
            }
        }

        diagnostics.Add(SourceIdentityUnusable(observedAt, "absent"));
        return null;
    }

    private static CdcDiagnostic SourceIdentityUnusable(DateTimeOffset observedAt, string observed) =>
        new CdcDiagnostic(
            "eligibilityPhysicalSourceUnusable",
            CdcDiagnosticCategory.StatusObservationUnavailable,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.ProviderSetup,
            observedAt,
            "CDC eligibility could not identify the physical source from the instance database.",
            retryable: false,
            artifactKind: "dataStoreIdentity",
            artifactName: DataStoreIdentityTableDefinition.TableDisplayName,
            expected: "one non-zero physical source value",
            observed: observed
        ).WithPath("$.physicalSourceFingerprint");

    private static CdcDiagnostic EvidenceUnavailable(
        CdcEligibilityReadOutcome outcome,
        DateTimeOffset observedAt
    ) =>
        new CdcDiagnostic(
            outcome == CdcEligibilityReadOutcome.SchemaIncomplete
                ? "eligibilitySchemaIncomplete"
                : "eligibilityStateUnreadable",
            CdcDiagnosticCategory.StatusObservationUnavailable,
            CdcDiagnosticSeverity.Warning,
            CdcDiagnosticComponent.Projection,
            observedAt,
            outcome == CdcEligibilityReadOutcome.SchemaIncomplete
                ? "CDC eligibility found no provisioned DocumentCache state to observe."
                : "CDC eligibility could not read instance state from the physical source.",
            retryable: outcome != CdcEligibilityReadOutcome.SchemaIncomplete,
            artifactKind: "documentCacheState",
            artifactName: QualifiedName(DocumentCacheInventoryDefinition.DocumentCacheState),
            expected: "one read-only provider-consistent eligibility read",
            observed: outcome.ToString()
        ).WithPath("$.lifecycleState");

    private static string QualifiedName(DbTableName table) => CdcEligibilitySql.QualifiedName(table);

    private static Ddl.CdcProvider ToDdlProvider(CdcProvider provider) =>
        provider == CdcProvider.Postgresql ? Ddl.CdcProvider.Postgresql : Ddl.CdcProvider.SqlServer;
}
