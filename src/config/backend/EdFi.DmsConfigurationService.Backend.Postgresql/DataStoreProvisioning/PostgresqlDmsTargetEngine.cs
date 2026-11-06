// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
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
/// through <see cref="PostgresqlSessionQueries"/>. Every read is on a dedicated connection; the members that
/// mutate or restore arrive with Steps 3.5 to 3.9 and throw until then. Connection and driver failures
/// propagate for the orchestrator to classify; a catalog row the inventory contract cannot describe is
/// reported as a content-profile violation, because the authoritative DDL never produces one.
/// </summary>
public sealed class PostgresqlDmsTargetEngine : IDmsTargetEngine
{
    /// <summary>The <c>Diagnostic.Operation</c> of a facts read whose inventory or security rows do not split as the delivered contract requires.</summary>
    public const string CatalogRowShapeStep = "CatalogRowShape";

    private const string ServerFactsQuery =
        "SELECT current_setting('server_version'), current_setting('server_version_num')::int, (SELECT pg_catalog.pg_encoding_to_char(encoding) FROM pg_catalog.pg_database WHERE datname = 'template1'), pg_catalog.now();";

    private const string InspectQuery =
        "SELECT d.oid, d.datistemplate, d.datallowconn, pg_catalog.shobj_description(d.oid, 'pg_database') FROM pg_catalog.pg_database d WHERE d.datname = @name;";

    private const string SessionIdentityQuery =
        "SELECT d.oid, pg_catalog.shobj_description(d.oid, 'pg_database'), a.pid, a.backend_start FROM pg_catalog.pg_database d CROSS JOIN pg_catalog.pg_stat_activity a WHERE d.datname = pg_catalog.current_database() AND a.pid = pg_catalog.pg_backend_pid();";

    private readonly PostgresqlAdminConnection _adminConnection;
    private readonly TimeSpan _lockAcquireTimeout;
    private readonly TimeSpan _lockHeartbeatInterval;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <param name="adminConnection">The resolved administrative connection (4.3 <c>AdminConnectionString</c> and <c>CommandTimeout</c>).</param>
    /// <param name="lockAcquireTimeout"><c>LockAcquireTimeout</c>: the server-side wait for the target lock (D-12).</param>
    /// <param name="lockHeartbeatInterval"><c>LockHeartbeatInterval</c>: the holder session's probe cadence (D-12).</param>
    /// <param name="timeProvider">Drives the holder heartbeat and every holder deadline.</param>
    /// <param name="logger">Receives the holder's ignored release and probe errors as type chains; never a value.</param>
    public PostgresqlDmsTargetEngine(
        PostgresqlAdminConnection adminConnection,
        TimeSpan lockAcquireTimeout,
        TimeSpan lockHeartbeatInterval,
        TimeProvider timeProvider,
        ILogger logger
    )
    {
        ArgumentNullException.ThrowIfNull(adminConnection);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockAcquireTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lockAcquireTimeout, TimeSpan.FromHours(1));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockHeartbeatInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lockHeartbeatInterval, TimeSpan.FromHours(1));
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _adminConnection = adminConnection;
        _lockAcquireTimeout = lockAcquireTimeout;
        _lockHeartbeatInterval = lockHeartbeatInterval;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string Provider => DmsDataStoreProvisioningOptions.PostgresqlProvider;

    public Task<DmsEngineEnvironment> ValidateEnvironmentAsync(CancellationToken cancellationToken) =>
        throw NotYetImplemented(nameof(ValidateEnvironmentAsync), "Step 3.9");

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
            _lockAcquireTimeout,
            _lockHeartbeatInterval,
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

        return values.Count switch
        {
            0 => new DmsIdentityRead.Invalid(DmsIdentityReadProblem.NoRows),
            1 => ParseIdentity(values[0]),
            _ => new DmsIdentityRead.Invalid(DmsIdentityReadProblem.MultipleRows),
        };
    }

    public async Task<DmsTargetFacts> ReadTargetFactsAsync(
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

    public Task<DmsCommandOutcome<DmsInstanceIdentity>> CreateStagingAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsProvisionClaim claim,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(CreateStagingAsync), "Step 3.5");

    public Task<DmsCommandOutcome<DmsInstanceIdentity>> RestoreIntoStagingAsync(
        DmsDatabaseName staging,
        StagedArtifact artifact,
        DmsOperationId operation,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(RestoreIntoStagingAsync), "Step 3.5");

    public Task<DmsCommandOutcome<DmsInstanceIdentity>> RenameIntoTargetAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(RenameIntoTargetAsync), "Step 3.6");

    public Task<DmsCommandOutcome> AssignMarkerAsync(
        DmsSession session,
        Guid expectedSourceIdentity,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(AssignMarkerAsync), "Step 3.6");

    public Task<DmsCommandOutcome> FenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsServerSessionId? keep,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(FenceAsync), "Step 3.8");

    public Task<DmsCommandOutcome> UnfenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(UnfenceAsync), "Step 3.8");

    public Task<DmsCommandOutcome> DropInstanceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsDropReason reason,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(DropInstanceAsync), "Step 3.6");

    public Task<DmsCommandOutcome> DropStagingAsync(
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(DropStagingAsync), "Step 3.5");

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

    private static NotImplementedException NotYetImplemented(string member, string step) =>
        new(
            $"{member} of the PostgreSQL DMS target engine arrives with {step}; this build contains the read members, the target lock, and session termination only."
        );
}
