// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The PostgreSQL adapter of the engine port (4.1). This build delivers the read members: server facts
/// (6.1 step 5), inspection by name with the instance identity <c>oid</c> plus claim comment (6.1 step 6,
/// D-13), instance-bound sessions that verify the instance at open and capture the backend's <c>pid</c> and
/// <c>backend_start</c> (D-7b), the delivered identity read, and the catalog facts the validator judges
/// (6.9c); and the target lock (D-12): <see cref="AcquireTargetLockAsync"/> opens the holder session
/// (<see cref="PostgresqlTargetLockSession"/>) that every later selected-target mutation runs on; and session
/// termination (D-22): <see cref="TerminateSessionAsync"/> proves a session gone, or reports why it could not,
/// through <see cref="PostgresqlSessionQueries"/>; and the staging stage (6.3 steps 4 and 5, 6.7): the
/// staging database created and claimed on the holder session, the <c>psql</c> replay with its outcome
/// protocol, and the staging drop, in the second part of this class; and publication (6.3 steps 8 and 10,
/// 6.7): the rename into the target and the compensation drop on the holder session, and the marker on an
/// instance-bound session, through <see cref="PostgresqlPublicationQueries"/>; and the owned delete (6.6):
/// the fence, its reversal, and the guarded drop on the holder session, through
/// <see cref="PostgresqlDeletionQueries"/>; and the environmental validation (7.3 items 4, 5 and 7) in a
/// third part. Every read is on a dedicated connection. Connection and driver failures of a read propagate for the orchestrator to classify; a catalog
/// row the inventory contract cannot describe is reported as a content-profile violation, because the
/// authoritative DDL never produces one.
/// </summary>
public sealed partial class PostgresqlDmsTargetEngine : IDmsTargetEngine
{
    /// <summary>The <c>Diagnostic.Operation</c> of a facts read whose inventory or security rows do not split as the delivered contract requires.</summary>
    public const string CatalogRowShapeStep = "CatalogRowShape";

    /// <summary>The fixed operation names an access refusal's diagnostic carries.</summary>
    public const string OpenSessionOperation = "OpenSession";

    public const string ReadIdentityOperation = "ReadIdentity";

    public const string ReadTargetFactsOperation = "ReadTargetFacts";

    private const string ServerFactsQuery =
        "SELECT current_setting('server_version'), current_setting('server_version_num')::int, (SELECT pg_catalog.pg_encoding_to_char(encoding) FROM pg_catalog.pg_database WHERE datname = 'template1'), pg_catalog.now();";

    private const string InspectQuery =
        "SELECT d.oid, d.datistemplate, d.datallowconn, pg_catalog.shobj_description(d.oid, 'pg_database') FROM pg_catalog.pg_database d WHERE d.datname = @name;";

    private const string SessionIdentityQuery =
        "SELECT d.oid, pg_catalog.shobj_description(d.oid, 'pg_database'), a.pid, a.backend_start FROM pg_catalog.pg_database d CROSS JOIN pg_catalog.pg_stat_activity a WHERE d.datname = pg_catalog.current_database() AND a.pid = pg_catalog.pg_backend_pid();";

    private readonly PostgresqlAdminConnection _adminConnection;
    private readonly PostgresqlDmsEngineSettings _settings;
    private readonly PsqlRestoreRunner _runner;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <param name="adminConnection">The resolved administrative connection (4.3 <c>AdminConnectionString</c> and <c>CommandTimeout</c>).</param>
    /// <param name="settings">The range-checked 4.3 settings the engine reads.</param>
    /// <param name="timeProvider">Drives the holder heartbeat, every deadline, and the replay bounds.</param>
    /// <param name="logger">Receives the holder's ignored errors and the replay's progress as type chains and fixed values; never a value from a request, a file, or output.</param>
    public PostgresqlDmsTargetEngine(
        PostgresqlAdminConnection adminConnection,
        PostgresqlDmsEngineSettings settings,
        TimeProvider timeProvider,
        ILogger logger
    )
        : this(adminConnection, settings, timeProvider, logger, RunnerFor(settings, timeProvider, logger)) { }

    /// <summary>The engine with its replay runner supplied, so a test can drive the outcome protocol with a fake process.</summary>
    internal PostgresqlDmsTargetEngine(
        PostgresqlAdminConnection adminConnection,
        PostgresqlDmsEngineSettings settings,
        TimeProvider timeProvider,
        ILogger logger,
        PsqlRestoreRunner runner
    )
    {
        ArgumentNullException.ThrowIfNull(adminConnection);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(runner);
        _adminConnection = adminConnection;
        _settings = settings;
        _runner = runner;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private static PsqlRestoreRunner RunnerFor(
        PostgresqlDmsEngineSettings settings,
        TimeProvider timeProvider,
        ILogger logger
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new PsqlRestoreRunner(settings.PsqlPath, settings.RestoreTimeout, timeProvider, logger);
    }

    public string Provider => DmsDataStoreProvisioningOptions.PostgresqlProvider;

    public async Task<DmsServerFacts> ReadServerFactsAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _adminConnection.OpenMaintenanceAsync(
            cancellationToken
        );
        await using NpgsqlCommand command = new(ServerFactsQuery, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The server facts query returned no row.");
        }

        string productVersion = reader.GetString(0);
        int serverVersionNum = reader.GetInt32(1);
        string? serverEncoding = await reader.IsDBNullAsync(2, cancellationToken)
            ? null
            : reader.GetString(2);
        DateTimeOffset serverUtcNow = ToUtc(await reader.GetFieldValueAsync<DateTime>(3, cancellationToken));
        return new DmsServerFacts(
            productVersion,
            serverVersionNum / 10000,
            serverEncoding,
            null,
            null,
            serverUtcNow
        );
    }

    public Task<DmsTargetLockResult> AcquireTargetLockAsync(
        DmsTargetDatabaseName name,
        DmsOperationId operation,
        CancellationToken cancellationToken
    ) =>
        PostgresqlTargetLockSession.AcquireAsync(
            _adminConnection,
            name,
            operation,
            _settings.LockAcquireTimeout,
            _settings.LockHeartbeatInterval,
            _timeProvider,
            _logger,
            cancellationToken
        );

    public async Task<DmsLiveTargetState> InspectAsync(
        DmsDatabaseName name,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlConnection connection = await _adminConnection.OpenMaintenanceAsync(
            cancellationToken
        );
        await using NpgsqlCommand command = new(InspectQuery, connection);
        command.Parameters.AddWithValue("name", name.Value);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new DmsLiveTargetState.Absent();
        }

        uint oid = await reader.GetFieldValueAsync<uint>(0, cancellationToken);
        bool isTemplate = reader.GetBoolean(1);
        bool allowsConnections = reader.GetBoolean(2);
        string claim = await ClaimOfAsync(reader, 3, cancellationToken);
        if (isTemplate)
        {
            return new DmsLiveTargetState.Protected();
        }

        DmsInstanceIdentity.Postgresql instance = new(oid, claim);
        return allowsConnections
            ? new DmsLiveTargetState.Present(instance, DmsDatabaseState.Online)
            : new DmsLiveTargetState.Fenced(instance);
    }

    public async Task<DmsSessionOpenResult> OpenSessionAsync(
        DmsDatabaseName name,
        DmsInstanceIdentity expectedInstance,
        CancellationToken cancellationToken
    )
    {
        if (expectedInstance is not DmsInstanceIdentity.Postgresql expected)
        {
            throw new ArgumentException(
                "The PostgreSQL engine verifies PostgreSQL instance identities only.",
                nameof(expectedInstance)
            );
        }

        NpgsqlConnection connection;
        try
        {
            connection = await _adminConnection.OpenAsync(
                name.Value,
                PostgresqlAdminConnection.SessionApplicationName,
                cancellationToken
            );
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            return new DmsSessionOpenResult.Absent();
        }
        catch (PostgresException exception)
            when (PostgresqlErrorClassifier.IsAccessRefusal(exception.SqlState))
        {
            return new DmsSessionOpenResult.AccessDenied(
                PostgresqlErrorClassifier.Diagnostic(exception, OpenSessionOperation)
            );
        }

        try
        {
            (DmsInstanceIdentity.Postgresql actual, DmsServerSessionId serverSession) =
                await ReadSessionIdentityAsync(connection, cancellationToken);
            if (!actual.IsSameStableInstance(expected))
            {
                await connection.DisposeAsync();
                return new DmsSessionOpenResult.InstanceChanged();
            }

            return new DmsSessionOpenResult.Opened(
                new PostgresqlDmsSession(connection, actual, serverSession)
            );
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<DmsIdentityRead> ReadIdentityAsync(
        DmsSession session,
        CancellationToken cancellationToken
    )
    {
        NpgsqlConnection connection = RequireSession(session).Connection;
        List<string?> values;
        try
        {
            values = await ReadRowsAsync(
                connection,
                PostgresqlInventoryQueries.SourceIdentityRows,
                reader => reader.IsDBNull(0) ? null : reader.GetString(0),
                cancellationToken
            );
        }
        catch (PostgresException exception)
            when (PostgresqlErrorClassifier.IsMissingRelation(exception.SqlState))
        {
            return new DmsIdentityRead.Invalid(DmsIdentityReadProblem.TableMissing);
        }
        catch (PostgresException exception)
            when (PostgresqlErrorClassifier.IsAccessRefusal(exception.SqlState))
        {
            return new DmsIdentityRead.Invalid(DmsIdentityReadProblem.AccessDenied)
            {
                Diagnostic = PostgresqlErrorClassifier.Diagnostic(exception, ReadIdentityOperation),
            };
        }

        return values.Count switch
        {
            0 => new DmsIdentityRead.Invalid(DmsIdentityReadProblem.NoRows),
            1 => ParseIdentity(values[0]),
            _ => new DmsIdentityRead.Invalid(DmsIdentityReadProblem.MultipleRows),
        };
    }

    /// <summary>
    /// The facts every 6.9c check needs, read on the instance-bound session. A query the server definitely
    /// refuses for lack of a privilege raises <see cref="DmsAccessDeniedException"/> with the SQLSTATE only;
    /// a missing singleton table is a typed read, and every other error, including cancellation and a lost
    /// connection, propagates unchanged.
    /// </summary>
    public async Task<DmsTargetFacts> ReadTargetFactsAsync(
        DmsSession session,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await ReadTargetFactsCoreAsync(session, cancellationToken);
        }
        catch (PostgresException exception)
            when (PostgresqlErrorClassifier.IsAccessRefusal(exception.SqlState))
        {
            throw new DmsAccessDeniedException(
                PostgresqlErrorClassifier.Diagnostic(exception, ReadTargetFactsOperation)
            );
        }
    }

    private async Task<DmsTargetFacts> ReadTargetFactsCoreAsync(
        DmsSession session,
        CancellationToken cancellationToken
    )
    {
        NpgsqlConnection connection = RequireSession(session).Connection;
        DmsEffectiveSchemaRead effectiveSchema = await ReadEffectiveSchemaAsync(
            connection,
            cancellationToken
        );
        int serverVersionNum = await ReadScalarAsync<int>(
            connection,
            PostgresqlInventoryQueries.ServerVersionNum,
            cancellationToken
        );
        List<DmsDocumentJsonColumn> documentJsonColumns = await ReadRowsAsync(
            connection,
            PostgresqlInventoryQueries.DocumentJsonColumns,
            reader => new DmsDocumentJsonColumn("dms", reader.GetString(0), reader.GetString(1)),
            cancellationToken
        );
        List<string> schemaNames = await ReadRowsAsync(
            connection,
            PostgresqlInventoryQueries.SchemaNames,
            reader => reader.GetString(0),
            cancellationToken
        );
        List<DmsInventoryObject> inventoryObjects = await ReadPipeRowsAsync<DmsInventoryObject>(
            connection,
            PostgresqlInventoryQueries.Objects,
            PostgresqlInventoryQueries.TryParseObjectRow,
            cancellationToken
        );
        List<DmsDatabaseLevelObject> databaseLevelObjects = await ReadPipeRowsAsync<DmsDatabaseLevelObject>(
            connection,
            PostgresqlInventoryQueries.DatabaseLevelObjects,
            PostgresqlInventoryQueries.TryParseDatabaseLevelObjectRow,
            cancellationToken
        );
        List<DmsPostgresqlOwnershipRow> ownership = await ReadPipeRowsAsync<DmsPostgresqlOwnershipRow>(
            connection,
            PostgresqlSecurityQueries.Ownership,
            PostgresqlSecurityQueries.TryParseOwnershipRow,
            cancellationToken
        );
        List<DmsPostgresqlPrivilegeRow> privileges = await ReadPipeRowsAsync<DmsPostgresqlPrivilegeRow>(
            connection,
            PostgresqlSecurityQueries.Privileges,
            PostgresqlSecurityQueries.TryParsePrivilegeRow,
            cancellationToken
        );
        bool? hasPopulatedDocument = await ReadPopulatedPredicateAsync(connection, cancellationToken);

        return new DmsTargetFacts
        {
            EffectiveSchema = effectiveSchema,
            EngineMajorVersion = serverVersionNum / 10000,
            CompatibilityLevel = null,
            DocumentJsonColumns = documentJsonColumns,
            SchemaNames = schemaNames,
            InventoryObjects = inventoryObjects,
            Principals = [],
            DatabaseLevelObjects = databaseLevelObjects,
            Security = new DmsSecurityFacts.Postgresql(ownership, privileges),
            HasPopulatedDocument = hasPopulatedDocument,
        };
    }

    public async Task<DmsCommandOutcome<DmsInstanceIdentity>> RenameIntoTargetAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    )
    {
        PostgresqlTargetLockSession session = RequireHolder(holder);
        ArgumentNullException.ThrowIfNull(stagingInstance);
        return await session.RunAsync(
            token =>
                PostgresqlPublicationQueries.RenameAsync(
                    session.Connection,
                    staging,
                    stagingInstance,
                    target,
                    session.Session,
                    token
                ),
            _adminConnection.CommandTimeout,
            cancellationToken
        );
    }

    public async Task<DmsCommandOutcome> AssignMarkerAsync(
        DmsSession session,
        Guid expectedSourceIdentity,
        CancellationToken cancellationToken
    )
    {
        PostgresqlDmsSession bound = RequireSession(session);
        if (bound.Instance is not DmsInstanceIdentity.Postgresql instance)
        {
            throw new ArgumentException(
                "The session is not bound to a PostgreSQL instance.",
                nameof(session)
            );
        }

        return await PostgresqlPublicationQueries.AssignMarkerAsync(
            bound.Connection,
            instance.Oid,
            expectedSourceIdentity,
            bound.ServerSession,
            cancellationToken
        );
    }

    /// <summary>
    /// 6.6 step 3 on the holder session, bounded by <see cref="FenceDeadline"/>: the termination loop has the
    /// whole termination bound, and the statements around it one command each.
    /// </summary>
    public async Task<DmsCommandOutcome> FenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsServerSessionId? keep,
        CancellationToken cancellationToken
    )
    {
        PostgresqlTargetLockSession session = RequireHolder(holder);
        ArgumentNullException.ThrowIfNull(instance);
        return await session.RunAsync(
            token =>
                PostgresqlDeletionQueries.FenceAsync(
                    session.Connection,
                    target,
                    instance,
                    keep,
                    session.Session,
                    _settings.SessionTerminationTimeout,
                    _adminConnection.CommandTimeout,
                    _timeProvider,
                    token
                ),
            FenceDeadline(_settings.SessionTerminationTimeout, _adminConnection.CommandTimeout),
            cancellationToken
        );
    }

    /// <summary>
    /// The fence's bound on the holder, the orchestrator's allowance for it: the instance check and the
    /// <c>ALTER DATABASE</c> take one command each before the termination loop starts, so neither can shorten
    /// the loop's termination bound.
    /// </summary>
    internal static TimeSpan FenceDeadline(TimeSpan sessionTerminationTimeout, TimeSpan commandTimeout) =>
        sessionTerminationTimeout + commandTimeout + commandTimeout;

    public async Task<DmsCommandOutcome> UnfenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        CancellationToken cancellationToken
    )
    {
        PostgresqlTargetLockSession session = RequireHolder(holder);
        ArgumentNullException.ThrowIfNull(instance);
        return await session.RunAsync(
            token =>
                PostgresqlDeletionQueries.UnfenceAsync(
                    session.Connection,
                    target,
                    instance,
                    session.Session,
                    token
                ),
            _adminConnection.CommandTimeout,
            cancellationToken
        );
    }

    /// <summary>Not used on PostgreSQL: the fence keeps the delete's own session, which reads the identity (6.6 step 4).</summary>
    public Task<DmsIdentityRead> ReadIdentityOnHolderAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    ) =>
        throw new NotSupportedException(
            "The PostgreSQL fence keeps the delete's own session; the identity is read there, not on the holder."
        );

    /// <summary>
    /// On the holder session: the compensation drop (6.7), or the owned delete (6.6 step 5), which drops only a
    /// target that is still fenced with no client session on it.
    /// </summary>
    public async Task<DmsCommandOutcome> DropInstanceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsDropReason reason,
        CancellationToken cancellationToken
    )
    {
        PostgresqlTargetLockSession session = RequireHolder(holder);
        ArgumentNullException.ThrowIfNull(instance);
        if (reason is not (DmsDropReason.Compensation or DmsDropReason.Delete))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return await session.RunAsync(
            token =>
                reason == DmsDropReason.Delete
                    ? PostgresqlDeletionQueries.DropTargetAsync(
                        session.Connection,
                        target,
                        instance,
                        session.Session,
                        token
                    )
                    : PostgresqlPublicationQueries.DropInstanceAsync(
                        session.Connection,
                        target,
                        instance,
                        session.Session,
                        token
                    ),
            _adminConnection.CommandTimeout,
            cancellationToken
        );
    }

    public async Task<DmsSessionTermination> TerminateSessionAsync(
        DmsServerSessionId session,
        TimeSpan deadline,
        CancellationToken cancellationToken
    )
    {
        PostgresqlSessionQueries.ValidateRequest(session, deadline);
        await using NpgsqlConnection connection = await _adminConnection.OpenMaintenanceAsync(
            cancellationToken
        );
        return await PostgresqlSessionQueries.TerminateAsync(
            connection,
            session,
            deadline,
            _adminConnection.CommandTimeout,
            _timeProvider,
            cancellationToken
        );
    }

    /// <summary>
    /// The sessions on <paramref name="database"/> whose <c>application_name</c> is
    /// <paramref name="applicationName"/>, oldest first: how the <c>psql</c> replay session
    /// (<c>PGAPPNAME = dms-provision:&lt;op&gt;</c>) is addressed for the outcome protocol (D-7b), which then
    /// hands each id to <see cref="TerminateSessionAsync"/>.
    /// </summary>
    internal async Task<IReadOnlyList<DmsServerSessionId>> FindSessionsAsync(
        DmsDatabaseName database,
        string applicationName,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        await using NpgsqlConnection connection = await _adminConnection.OpenMaintenanceAsync(
            cancellationToken
        );
        return await PostgresqlSessionQueries.FindByApplicationNameAsync(
            connection,
            database,
            applicationName,
            cancellationToken
        );
    }

    /// <summary>The instance behind the session's own database and the backend serving it.</summary>
    private static async Task<(
        DmsInstanceIdentity.Postgresql Instance,
        DmsServerSessionId Session
    )> ReadSessionIdentityAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(SessionIdentityQuery, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The session identity query returned no row.");
        }

        uint oid = await reader.GetFieldValueAsync<uint>(0, cancellationToken);
        string claim = await ClaimOfAsync(reader, 1, cancellationToken);
        long pid = reader.GetInt32(2);
        DateTimeOffset backendStart = ToUtc(await reader.GetFieldValueAsync<DateTime>(3, cancellationToken));
        return (new DmsInstanceIdentity.Postgresql(oid, claim), new DmsServerSessionId(pid, backendStart));
    }

    private static async Task<DmsEffectiveSchemaRead> ReadEffectiveSchemaAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        List<DmsEffectiveSchemaRead.Found> rows;
        try
        {
            rows = await ReadRowsAsync(
                connection,
                PostgresqlInventoryQueries.EffectiveSchemaRows,
                reader => new DmsEffectiveSchemaRead.Found(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt16(2),
                    Convert.ToBase64String(reader.GetFieldValue<byte[]>(3))
                ),
                cancellationToken
            );
        }
        catch (PostgresException exception)
            when (PostgresqlErrorClassifier.IsMissingRelation(exception.SqlState))
        {
            return new DmsEffectiveSchemaRead.Invalid(DmsEffectiveSchemaReadProblem.TableMissing);
        }

        return rows.Count switch
        {
            0 => new DmsEffectiveSchemaRead.Invalid(DmsEffectiveSchemaReadProblem.NoRows),
            1 => rows[0],
            _ => new DmsEffectiveSchemaRead.Invalid(DmsEffectiveSchemaReadProblem.MultipleRows),
        };
    }

    /// <summary>The Populated predicate, or null when either table it reads is missing.</summary>
    private static async Task<bool?> ReadPopulatedPredicateAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        bool tablesExist = await ReadScalarAsync<bool>(
            connection,
            PostgresqlInventoryQueries.PopulatedPredicateTablesExist,
            cancellationToken
        );
        if (!tablesExist)
        {
            return null;
        }

        return await ReadScalarAsync<bool>(
            connection,
            PostgresqlInventoryQueries.HasPopulatedDocument,
            cancellationToken
        );
    }

    private static DmsIdentityRead ParseIdentity(string? text)
    {
        if (!Guid.TryParseExact(text, "D", out Guid sourceIdentity))
        {
            return new DmsIdentityRead.Invalid(DmsIdentityReadProblem.Unparsable);
        }

        return sourceIdentity == Guid.Empty
            ? new DmsIdentityRead.Invalid(DmsIdentityReadProblem.EmptyGuid)
            : new DmsIdentityRead.Found(sourceIdentity);
    }

    private static async Task<T> ReadScalarAsync<T>(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = new(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("A scalar catalog query returned no row.");
        }

        return await reader.GetFieldValueAsync<T>(0, cancellationToken);
    }

    private static async Task<List<T>> ReadRowsAsync<T>(
        NpgsqlConnection connection,
        string sql,
        Func<NpgsqlDataReader, T> project,
        CancellationToken cancellationToken
    )
    {
        List<T> rows = [];
        await using NpgsqlCommand command = new(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(project(reader));
        }

        return rows;
    }

    private delegate bool TryParseRow<T>(string? row, [NotNullWhen(true)] out T? entry)
        where T : class;

    /// <summary>Reads a pipe-separated single-column result; a row that does not split as the contract requires is a content-profile violation.</summary>
    private static async Task<List<T>> ReadPipeRowsAsync<T>(
        NpgsqlConnection connection,
        string sql,
        TryParseRow<T> tryParse,
        CancellationToken cancellationToken
    )
        where T : class
    {
        List<string?> rows = await ReadRowsAsync(
            connection,
            sql,
            reader => reader.IsDBNull(0) ? null : reader.GetString(0),
            cancellationToken
        );
        List<T> entries = [];
        foreach (string? row in rows)
        {
            if (!tryParse(row, out T? entry))
            {
                throw new DmsProvisioningFailedException(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.ContentProfileViolation,
                        DmsProvisioningDiagnostic.ForOperation(CatalogRowShapeStep)
                    )
                );
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static PostgresqlDmsSession RequireSession(DmsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session as PostgresqlDmsSession
            ?? throw new ArgumentException(
                "The PostgreSQL engine reads only sessions it opened.",
                nameof(session)
            );
    }

    /// <summary>The database comment as read; empty when none is set. Foreign databases may carry any text, so it is kept verbatim as stable evidence.</summary>
    private static async Task<string> ClaimOfAsync(
        NpgsqlDataReader reader,
        int ordinal,
        CancellationToken cancellationToken
    ) => await reader.IsDBNullAsync(ordinal, cancellationToken) ? string.Empty : reader.GetString(ordinal);

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);
}
