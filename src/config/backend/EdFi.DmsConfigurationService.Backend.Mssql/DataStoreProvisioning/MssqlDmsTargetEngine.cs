// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The SQL Server adapter of the engine port (4.1). This build delivers the read members: server facts with
/// the default data and log paths and the server's UTC clock (6.1 step 5, 6.4 step 2), inspection by name
/// with the instance identity <c>database_id</c> plus primary-file <c>physical_name</c> plus the
/// <c>create_date</c> snapshot (6.1 step 6, D-13), instance-bound sessions that verify all three at open and
/// capture <c>@@SPID</c> and <c>login_time</c> (D-7b), the delivered identity read, and the catalog facts the
/// validator judges (6.9c), including the compatibility level and the security state. Every read is on a
/// dedicated connection. And the target lock (D-12): <see cref="AcquireTargetLockAsync"/> opens the holder
/// session (<see cref="MssqlTargetLockSession"/>) that every later selected-target mutation runs on.
/// And session termination (D-22): <see cref="TerminateSessionAsync"/> kills a session addressed by
/// <c>session_id</c> and <c>login_time</c> from a fresh maintenance session and proves it gone
/// (<see cref="MssqlSessionQueries"/>).
/// Connection and driver failures of a read propagate for the orchestrator to
/// classify; a catalog row the inventory contract cannot describe is reported as a content-profile
/// violation, because the authoritative DDL never produces one. The mutating members arrive with Steps 4.5
/// to 4.9.
/// </summary>
public sealed class MssqlDmsTargetEngine : IDmsTargetEngine
{
    /// <summary>The <c>Diagnostic.Operation</c> of a facts read whose inventory or security rows do not split as the delivered contract requires.</summary>
    public const string CatalogRowShapeStep = "CatalogRowShape";

    /// <summary>The fixed operation names an access refusal's diagnostic carries.</summary>
    public const string OpenSessionOperation = "OpenSession";

    public const string ReadIdentityOperation = "ReadIdentity";

    public const string ReadTargetFactsOperation = "ReadTargetFacts";

    /// <summary>The <c>database_id</c> of the highest system database (<c>master</c> 1, <c>tempdb</c> 2, <c>model</c> 3, <c>msdb</c> 4).</summary>
    public const int HighestSystemDatabaseId = 4;

    private const string ServerFactsQuery =
        "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')), CONVERT(int, SERVERPROPERTY('ProductMajorVersion')), CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultDataPath')), CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultLogPath')), SYSUTCDATETIME();";

    private const string InspectQuery =
        "SELECT d.database_id, d.state_desc, d.user_access_desc, d.create_date, (SELECT f.physical_name FROM sys.master_files f WHERE f.database_id = d.database_id AND f.file_id = 1) FROM sys.databases d WHERE d.name = @name;";

    private const string SessionIdentityQuery =
        "SELECT CONVERT(int, DB_ID()), (SELECT f.physical_name FROM sys.master_files f WHERE f.database_id = DB_ID() AND f.file_id = 1), (SELECT d.create_date FROM sys.databases d WHERE d.database_id = DB_ID()), CONVERT(int, @@SPID), (SELECT s.login_time FROM sys.dm_exec_sessions s WHERE s.session_id = @@SPID);";

    private const string OnlineState = "ONLINE";

    /// <summary>The <c>state_desc</c> and <c>user_access_desc</c> values that make a database fenced (6.1 step 6).</summary>
    private static readonly HashSet<string> _fencedStates = new(StringComparer.Ordinal)
    {
        "OFFLINE",
        "RESTORING",
    };

    private static readonly HashSet<string> _fencedUserAccess = new(StringComparer.Ordinal)
    {
        "SINGLE_USER",
        "RESTRICTED_USER",
    };

    private readonly MssqlAdminConnection _adminConnection;
    private readonly MssqlDmsEngineSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <param name="adminConnection">The resolved administrative connection (4.3 <c>AdminConnectionString</c> and <c>CommandTimeout</c>).</param>
    /// <param name="settings">The range-checked 4.3 settings the engine reads.</param>
    /// <param name="timeProvider">Drives the holder heartbeat and every deadline.</param>
    /// <param name="logger">Receives the holder's ignored errors as type chains; never a value.</param>
    public MssqlDmsTargetEngine(
        MssqlAdminConnection adminConnection,
        MssqlDmsEngineSettings settings,
        TimeProvider timeProvider,
        ILogger logger
    )
    {
        ArgumentNullException.ThrowIfNull(adminConnection);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _adminConnection = adminConnection;
        _settings = settings;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string Provider => DmsDataStoreProvisioningOptions.MssqlProvider;

    /// <summary>Test seam: the batch sent to kill a session; production sends <see cref="MssqlSessionQueries.GuardedKill"/>.</summary>
    internal Func<short, string> GuardedKillStatement { get; init; } = MssqlSessionQueries.GuardedKill;

    public Task<DmsEngineEnvironment> ValidateEnvironmentAsync(CancellationToken cancellationToken) =>
        throw NotYetImplemented(nameof(ValidateEnvironmentAsync), "Step 4.9");

    public async Task<DmsServerFacts> ReadServerFactsAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await _adminConnection.OpenMaintenanceAsync(cancellationToken);
        await using SqlCommand command = Command(ServerFactsQuery, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The server facts query returned no row.");
        }

        string productVersion = reader.GetString(0);
        int majorVersion = reader.GetInt32(1);
        string? defaultDataPath = await StringOrNullAsync(reader, 2, cancellationToken);
        string? defaultLogPath = await StringOrNullAsync(reader, 3, cancellationToken);
        DateTimeOffset serverUtcNow = AsUtc(await reader.GetFieldValueAsync<DateTime>(4, cancellationToken));
        return new DmsServerFacts(
            productVersion,
            majorVersion,
            null,
            defaultDataPath,
            defaultLogPath,
            serverUtcNow
        );
    }

    public Task<DmsTargetLockResult> AcquireTargetLockAsync(
        DmsTargetDatabaseName name,
        DmsOperationId operation,
        CancellationToken cancellationToken
    ) =>
        MssqlTargetLockSession.AcquireAsync(
            _adminConnection,
            name,
            operation,
            _settings.LockAcquireTimeout,
            _settings.LockHeartbeatInterval,
            _timeProvider,
            _logger,
            cancellationToken
        );

    /// <summary>
    /// What is under <paramref name="name"/>, as SQL Server resolves the name: protected for the system
    /// databases (<c>database_id</c> at most 4); fenced for <c>SINGLE_USER</c>, <c>RESTRICTED_USER</c>,
    /// <c>OFFLINE</c>, or <c>RESTORING</c>; otherwise present, online or in another state.
    /// </summary>
    public async Task<DmsLiveTargetState> InspectAsync(
        DmsDatabaseName name,
        CancellationToken cancellationToken
    )
    {
        await using SqlConnection connection = await _adminConnection.OpenMaintenanceAsync(cancellationToken);
        await using SqlCommand command = Command(InspectQuery, connection);
        command.Parameters.Add(
            new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = name.Value }
        );
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new DmsLiveTargetState.Absent();
        }

        int databaseId = reader.GetInt32(0);
        string state = reader.GetString(1);
        string userAccess = reader.GetString(2);
        DateTime createDate = await reader.GetFieldValueAsync<DateTime>(3, cancellationToken);
        string primaryFile = await StringOrNullAsync(reader, 4, cancellationToken) ?? string.Empty;
        if (databaseId <= HighestSystemDatabaseId)
        {
            return new DmsLiveTargetState.Protected();
        }

        DmsInstanceIdentity.Mssql instance = new(databaseId, primaryFile, AsUtc(createDate));
        if (_fencedStates.Contains(state) || _fencedUserAccess.Contains(userAccess))
        {
            return new DmsLiveTargetState.Fenced(instance);
        }

        return new DmsLiveTargetState.Present(
            instance,
            string.Equals(state, OnlineState, StringComparison.Ordinal)
                ? DmsDatabaseState.Online
                : DmsDatabaseState.Other
        );
    }

    /// <summary>
    /// Opens a dedicated connection to <paramref name="name"/> and verifies, on that connection, that its
    /// database is <paramref name="expectedInstance"/>: the same <c>database_id</c>, primary file, and
    /// <c>create_date</c> snapshot. A primary file the connection cannot see is refused as a changed instance,
    /// whatever the expected identity holds, so no session is ever handed out without file evidence. SQL Server
    /// answers a login to a database that does not exist and one the login may not open with the same error
    /// (4060), so that refusal is resolved by inspecting the name.
    /// </summary>
    public async Task<DmsSessionOpenResult> OpenSessionAsync(
        DmsDatabaseName name,
        DmsInstanceIdentity expectedInstance,
        CancellationToken cancellationToken
    )
    {
        if (expectedInstance is not DmsInstanceIdentity.Mssql expected)
        {
            throw new ArgumentException(
                "The SQL Server engine verifies SQL Server instance identities only.",
                nameof(expectedInstance)
            );
        }

        SqlConnection connection;
        try
        {
            connection = await _adminConnection.OpenAsync(
                name.Value,
                MssqlAdminConnection.SessionApplicationName,
                cancellationToken
            );
        }
        catch (SqlException exception)
            when (exception.Number == MssqlErrorClassifier.CannotOpenRequestedDatabase)
        {
            DmsSessionOpenResult? resolved = await ResolveRefusedOpenAsync(
                name,
                expected,
                exception,
                cancellationToken
            );
            if (resolved is null)
            {
                throw;
            }

            return resolved;
        }
        catch (SqlException exception) when (MssqlErrorClassifier.IsAccessRefusal(exception.Number))
        {
            return new DmsSessionOpenResult.AccessDenied(
                MssqlErrorClassifier.Diagnostic(exception, OpenSessionOperation)
            );
        }

        DmsInstanceIdentity.Mssql actual;
        DmsServerSessionId serverSession;
        try
        {
            (actual, serverSession) = await ReadSessionIdentityAsync(connection, cancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        // Missing file evidence never proves the instance, even when the expected identity is missing it too
        // (both read by a login that cannot see sys.master_files): only database_id and create_date would match.
        if (actual.PrimaryFileTag.Length == 0 || actual != expected)
        {
            await connection.DisposeAsync();
            return new DmsSessionOpenResult.InstanceChanged();
        }

        return new DmsSessionOpenResult.Opened(new MssqlDmsSession(connection, actual, serverSession));
    }

    public async Task<DmsIdentityRead> ReadIdentityAsync(
        DmsSession session,
        CancellationToken cancellationToken
    )
    {
        SqlConnection connection = RequireSession(session).Connection;
        List<string?> values;
        try
        {
            values = await ReadRowsAsync(
                connection,
                MssqlInventoryQueries.SourceIdentityRows,
                reader => reader.IsDBNull(0) ? null : reader.GetString(0),
                cancellationToken
            );
        }
        catch (SqlException exception) when (MssqlErrorClassifier.IsMissingRelation(exception.Number))
        {
            return new DmsIdentityRead.Invalid(DmsIdentityReadProblem.TableMissing);
        }
        catch (SqlException exception) when (MssqlErrorClassifier.IsAccessRefusal(exception.Number))
        {
            return new DmsIdentityRead.Invalid(DmsIdentityReadProblem.AccessDenied)
            {
                Diagnostic = MssqlErrorClassifier.Diagnostic(exception, ReadIdentityOperation),
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
    /// refuses for lack of a privilege raises <see cref="DmsAccessDeniedException"/> with the error number
    /// only; a missing singleton table is a typed read, and every other error, including cancellation and a
    /// lost connection, propagates unchanged.
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
        catch (SqlException exception) when (MssqlErrorClassifier.IsAccessRefusal(exception.Number))
        {
            throw new DmsAccessDeniedException(
                MssqlErrorClassifier.Diagnostic(exception, ReadTargetFactsOperation)
            );
        }
    }

    private async Task<DmsTargetFacts> ReadTargetFactsCoreAsync(
        DmsSession session,
        CancellationToken cancellationToken
    )
    {
        SqlConnection connection = RequireSession(session).Connection;
        DmsEffectiveSchemaRead effectiveSchema = await ReadEffectiveSchemaAsync(
            connection,
            cancellationToken
        );
        int engineMajorVersion = await ReadScalarAsync<int>(
            connection,
            MssqlInventoryQueries.ProductMajorVersion,
            cancellationToken
        );
        int compatibilityLevel = await ReadScalarAsync<int>(
            connection,
            MssqlInventoryQueries.CompatibilityLevel,
            cancellationToken
        );
        List<DmsDocumentJsonColumn> documentJsonColumns = await ReadRowsAsync(
            connection,
            MssqlInventoryQueries.DocumentJsonColumns,
            reader => new DmsDocumentJsonColumn("dms", reader.GetString(0), reader.GetString(1)),
            cancellationToken
        );
        List<string> schemaNames = await ReadRowsAsync(
            connection,
            MssqlInventoryQueries.SchemaNames,
            reader => reader.GetString(0),
            cancellationToken
        );
        List<DmsInventoryObject> inventoryObjects = await ReadPipeRowsAsync<DmsInventoryObject>(
            connection,
            MssqlInventoryQueries.Objects,
            DmsInventoryRows.TryParseObjectRow,
            cancellationToken
        );
        List<string> principals = await ReadRowsAsync(
            connection,
            MssqlInventoryQueries.Principals,
            reader => reader.GetString(0),
            cancellationToken
        );
        List<DmsDatabaseLevelObject> databaseLevelObjects = await ReadPipeRowsAsync<DmsDatabaseLevelObject>(
            connection,
            MssqlInventoryQueries.DatabaseLevelObjects,
            DmsInventoryRows.TryParseDatabaseLevelObjectRow,
            cancellationToken
        );
        DmsSecurityFacts.Mssql security = await ReadSecurityStateAsync(connection, cancellationToken);
        bool? hasPopulatedDocument = await ReadPopulatedPredicateAsync(connection, cancellationToken);

        return new DmsTargetFacts
        {
            EffectiveSchema = effectiveSchema,
            EngineMajorVersion = engineMajorVersion,
            CompatibilityLevel = compatibilityLevel,
            DocumentJsonColumns = documentJsonColumns,
            SchemaNames = schemaNames,
            InventoryObjects = inventoryObjects,
            Principals = principals,
            DatabaseLevelObjects = databaseLevelObjects,
            Security = security,
            HasPopulatedDocument = hasPopulatedDocument,
        };
    }

    public Task<DmsCommandOutcome<DmsInstanceIdentity>> CreateStagingAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsProvisionClaim claim,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(CreateStagingAsync), "Step 4.5");

    public Task<DmsCommandOutcome<DmsInstanceIdentity>> RestoreIntoStagingAsync(
        DmsDatabaseName staging,
        StagedArtifact artifact,
        DmsOperationId operation,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(RestoreIntoStagingAsync), "Step 4.5");

    public Task<DmsCommandOutcome<DmsInstanceIdentity>> RenameIntoTargetAsync(
        IDmsTargetLockHolder holder,
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        DmsTargetDatabaseName target,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(RenameIntoTargetAsync), "Step 4.6");

    public Task<DmsCommandOutcome> AssignMarkerAsync(
        DmsSession session,
        Guid expectedSourceIdentity,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(AssignMarkerAsync), "Step 4.6");

    public Task<DmsCommandOutcome> FenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsServerSessionId? keep,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(FenceAsync), "Step 4.8");

    public Task<DmsCommandOutcome> UnfenceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(UnfenceAsync), "Step 4.8");

    public Task<DmsCommandOutcome> DropInstanceAsync(
        IDmsTargetLockHolder holder,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsDropReason reason,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(DropInstanceAsync), "Steps 4.6 and 4.8");

    public Task<DmsCommandOutcome> DropStagingAsync(
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        CancellationToken cancellationToken
    ) => throw NotYetImplemented(nameof(DropStagingAsync), "Step 4.5");

    /// <summary>
    /// D-22 on a fresh maintenance session, so it works whether or not the session's own connection (the
    /// holder's, a work session's) is still usable: the guarded <c>KILL</c>, then verification by
    /// <c>session_id</c> and <c>login_time</c> until the session is gone or <paramref name="deadline"/> passes.
    /// The request is validated before any connection is opened.
    /// </summary>
    public async Task<DmsSessionTermination> TerminateSessionAsync(
        DmsServerSessionId session,
        TimeSpan deadline,
        CancellationToken cancellationToken
    )
    {
        MssqlSessionQueries.ValidateRequest(session, deadline);
        await using SqlConnection connection = await _adminConnection.OpenMaintenanceAsync(cancellationToken);
        return await MssqlSessionQueries.TerminateAsync(
            connection,
            session,
            deadline,
            _adminConnection.CommandTimeoutSeconds,
            _timeProvider,
            cancellationToken,
            GuardedKillStatement
        );
    }

    /// <summary>
    /// A 4060 refusal resolved by what is under the name now: absent; the expected instance, online, which
    /// the login may not open (an access refusal); another instance (changed); or null when the expected
    /// instance is there but fenced or not online, where the refusal is not about access and the caller
    /// rethrows it for the orchestrator to classify. A login refused a database may also be denied the
    /// file catalog, so a primary file it cannot see is not counted as a difference; the
    /// <c>database_id</c> and the <c>create_date</c> snapshot still must match.
    /// </summary>
    private async Task<DmsSessionOpenResult?> ResolveRefusedOpenAsync(
        DmsDatabaseName name,
        DmsInstanceIdentity.Mssql expected,
        SqlException refusal,
        CancellationToken cancellationToken
    ) =>
        await InspectAsync(name, cancellationToken) switch
        {
            DmsLiveTargetState.Absent => new DmsSessionOpenResult.Absent(),
            DmsLiveTargetState.Present { State: DmsDatabaseState.Online } present
                when IsExpectedAsFarAsVisible(present.Instance, expected) =>
                new DmsSessionOpenResult.AccessDenied(
                    MssqlErrorClassifier.Diagnostic(refusal, OpenSessionOperation)
                ),
            DmsLiveTargetState.Present present when IsExpectedAsFarAsVisible(present.Instance, expected) =>
                null,
            DmsLiveTargetState.Fenced fenced when IsExpectedAsFarAsVisible(fenced.Instance, expected) => null,
            _ => new DmsSessionOpenResult.InstanceChanged(),
        };

    private static bool IsExpectedAsFarAsVisible(
        DmsInstanceIdentity inspected,
        DmsInstanceIdentity.Mssql expected
    ) =>
        inspected is DmsInstanceIdentity.Mssql actual
        && actual.DatabaseId == expected.DatabaseId
        && actual.CreateDateSnapshot == expected.CreateDateSnapshot
        && (
            actual.PrimaryFileTag.Length == 0
            || string.Equals(actual.PrimaryFileTag, expected.PrimaryFileTag, StringComparison.Ordinal)
        );

    /// <summary>The instance behind the session's own database and the session serving it.</summary>
    private async Task<(
        DmsInstanceIdentity.Mssql Instance,
        DmsServerSessionId Session
    )> ReadSessionIdentityAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(SessionIdentityQuery, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The session identity query returned no row.");
        }

        int databaseId = reader.GetInt32(0);
        string primaryFile = await StringOrNullAsync(reader, 1, cancellationToken) ?? string.Empty;
        DateTime createDate = await reader.GetFieldValueAsync<DateTime>(2, cancellationToken);
        int spid = reader.GetInt32(3);
        DateTime loginTime = await reader.GetFieldValueAsync<DateTime>(4, cancellationToken);
        return (
            new DmsInstanceIdentity.Mssql(databaseId, primaryFile, AsUtc(createDate)),
            new DmsServerSessionId(spid, AsUtc(loginTime))
        );
    }

    private async Task<DmsEffectiveSchemaRead> ReadEffectiveSchemaAsync(
        SqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        List<DmsEffectiveSchemaRead.Found> rows;
        try
        {
            rows = await ReadRowsAsync(
                connection,
                MssqlInventoryQueries.EffectiveSchemaRows,
                reader => new DmsEffectiveSchemaRead.Found(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt16(2),
                    Convert.ToBase64String(reader.GetFieldValue<byte[]>(3))
                ),
                cancellationToken
            );
        }
        catch (SqlException exception) when (MssqlErrorClassifier.IsMissingRelation(exception.Number))
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

    /// <summary>The permission and membership rows; a row that does not split as the delivered gate requires is a content-profile violation.</summary>
    private async Task<DmsSecurityFacts.Mssql> ReadSecurityStateAsync(
        SqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        List<string?> rows = await ReadRowsAsync(
            connection,
            MssqlSecurityQueries.SecurityState,
            reader => reader.IsDBNull(0) ? null : reader.GetString(0),
            cancellationToken
        );
        if (
            !MssqlSecurityQueries.TryParseRows(
                rows,
                out List<DmsMssqlPermissionRow> permissions,
                out List<DmsMssqlRoleMemberRow> members
            )
        )
        {
            throw CatalogRowShapeViolation();
        }

        return new DmsSecurityFacts.Mssql(permissions, members);
    }

    /// <summary>The Populated predicate, or null when either table it reads is missing.</summary>
    private async Task<bool?> ReadPopulatedPredicateAsync(
        SqlConnection connection,
        CancellationToken cancellationToken
    )
    {
        bool tablesExist = await ReadScalarAsync<bool>(
            connection,
            MssqlInventoryQueries.PopulatedPredicateTablesExist,
            cancellationToken
        );
        if (!tablesExist)
        {
            return null;
        }

        return await ReadScalarAsync<bool>(
            connection,
            MssqlInventoryQueries.HasPopulatedDocument,
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

    private SqlCommand Command(string sql, SqlConnection connection) =>
        new(sql, connection) { CommandTimeout = _adminConnection.CommandTimeoutSeconds };

    private async Task<T> ReadScalarAsync<T>(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = Command(sql, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("A scalar catalog query returned no row.");
        }

        return await reader.GetFieldValueAsync<T>(0, cancellationToken);
    }

    private async Task<List<T>> ReadRowsAsync<T>(
        SqlConnection connection,
        string sql,
        Func<SqlDataReader, T> project,
        CancellationToken cancellationToken
    )
    {
        List<T> rows = [];
        await using SqlCommand command = Command(sql, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(project(reader));
        }

        return rows;
    }

    private delegate bool TryParseRow<T>(string? row, [NotNullWhen(true)] out T? entry)
        where T : class;

    /// <summary>Reads a pipe-separated single-column result; a row that does not split as the contract requires is a content-profile violation.</summary>
    private async Task<List<T>> ReadPipeRowsAsync<T>(
        SqlConnection connection,
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
                throw CatalogRowShapeViolation();
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static DmsProvisioningFailedException CatalogRowShapeViolation() =>
        new(
            DmsProvisioningFailure.Create(
                DmsProvisioningFailureCode.ContentProfileViolation,
                DmsProvisioningDiagnostic.ForOperation(CatalogRowShapeStep)
            )
        );

    private static MssqlDmsSession RequireSession(DmsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session as MssqlDmsSession
            ?? throw new ArgumentException(
                "The SQL Server engine reads only sessions it opened.",
                nameof(session)
            );
    }

    private static async Task<string?> StringOrNullAsync(
        SqlDataReader reader,
        int ordinal,
        CancellationToken cancellationToken
    ) => await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetString(ordinal);

    /// <summary>
    /// A catalog <c>datetime</c> as an offset value without conversion: <c>create_date</c> and <c>login_time</c>
    /// are server-local and serve only as equality guards against id reuse, never as a clock; <c>SYSUTCDATETIME()</c>
    /// is already UTC.
    /// </summary>
    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    private static NotImplementedException NotYetImplemented(string member, string step) =>
        new(
            $"{member} of the SQL Server DMS target engine arrives with {step}; this build contains the read members only."
        );
}
