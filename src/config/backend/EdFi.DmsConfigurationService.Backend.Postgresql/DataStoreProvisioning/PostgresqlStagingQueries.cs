// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The staging database commands and their cleanup, each classified by what its outcome proves:
/// a normal return is definite success, a server-reported error whose SQLSTATE proves no commit is a
/// definite failure, and anything else (the driver's own cancellation after send, a connection-class
/// error, the server's statement of doubt) is <see cref="DmsCommandOutcome{T}.Unknown"/> naming the
/// session the statement ran on. The statements that cannot take parameters (<c>CREATE DATABASE</c>,
/// <c>COMMENT ON DATABASE</c>, <c>DROP DATABASE</c>) are built only from validated names and the claim
/// text; every catalog read is parameterized.
/// </summary>
internal static class PostgresqlStagingQueries
{
    public const string CreateStagingStep = "CreateStaging";

    /// <summary>The claim comment failed after the database was created; the database was dropped again on a best-effort basis.</summary>
    public const string ClaimStagingStep = "CreateStaging.Claim";

    public const string DropStagingStep = "DropStaging";

    /// <summary>The staging name holds a database that is not the operation's instance; nothing was dropped.</summary>
    public const string DropStagingInstanceStep = "DropStaging.InstanceChanged";

    /// <summary>The D-16 role check before a replay; SQLSTATE <c>55000</c> means the role exists but is not locked down.</summary>
    public const string EnsureEnqueueOwnerStep = "EnsureEnqueueOwner";

    /// <summary>The SQLSTATE the D-16 block raises for a role that exists but is not in the locked-down state.</summary>
    public const string RoleStateUnsafeSqlState = "55000";

    public const string InstanceByName =
        "SELECT d.oid, pg_catalog.shobj_description(d.oid, 'pg_database') FROM pg_catalog.pg_database d WHERE d.datname = @name;";

    /// <summary>
    /// The delivered <c>Get-PostgresqlRestoreGlobalRoleSql</c> block (D-16): creates the locked-down
    /// role when missing, refuses a role that exists with any login, inheritance, or elevated attribute, or
    /// with a privilege-bearing outgoing membership. The two refusals carry
    /// <see cref="RoleStateUnsafeSqlState"/> so the orchestrator can name them without reading a message.
    /// </summary>
    public const string EnsureEnqueueOwner = """
        DO $$
        DECLARE
            _owner_role oid := pg_catalog.to_regrole('edfi_dms_enqueue_owner');
        BEGIN
            IF _owner_role IS NULL THEN
                CREATE ROLE "edfi_dms_enqueue_owner" WITH NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
            ELSIF EXISTS (
                SELECT 1
                FROM pg_catalog.pg_roles roles
                WHERE roles.oid = _owner_role
                  AND (roles.rolcanlogin OR roles.rolinherit OR roles.rolsuper OR roles.rolcreatedb OR roles.rolcreaterole OR roles.rolreplication OR roles.rolbypassrls)
            ) THEN
                RAISE EXCEPTION 'PostgreSQL role edfi_dms_enqueue_owner exists but is not locked down as NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS. Drop or repair the role before restoring template packages.' USING ERRCODE = '55000';
            ELSIF EXISTS (
                SELECT 1
                FROM pg_catalog.pg_auth_members memberships
                WHERE memberships.member = _owner_role
                  AND (memberships.admin_option OR memberships.inherit_option OR memberships.set_option)
            ) THEN
                RAISE EXCEPTION 'PostgreSQL role edfi_dms_enqueue_owner must not hold outgoing privilege-bearing memberships before restoring template packages.' USING ERRCODE = '55000';
            END IF;
        END
        $$;
        """;

    /// <summary>
    /// D-5: the staging database is a copy of the pristine <c>template0</c>, never of <c>template1</c>, so
    /// no local object in <c>template1</c> reaches it and a session on <c>template1</c> cannot refuse the copy.
    /// No locale clause: the database takes <c>template0</c>'s locale, the cluster's initdb locale.
    /// </summary>
    public static string CreateDatabaseStatement(DmsDatabaseName name) =>
        $"CREATE DATABASE {PostgresqlIdentifiers.Quote(name.Value)} TEMPLATE template0 ENCODING 'UTF8';";

    public static string ClaimStatement(DmsDatabaseName name, DmsProvisionClaim claim) =>
        $"COMMENT ON DATABASE {PostgresqlIdentifiers.Quote(name.Value)} IS {PostgresqlIdentifiers.Literal(claim.Text)};";

    public static string DropDatabaseStatement(DmsDatabaseName name) =>
        $"DROP DATABASE {PostgresqlIdentifiers.Quote(name.Value)} WITH (FORCE);";

    /// <summary>
    /// Test seams for the create: <see cref="AfterCreate"/> runs once
    /// <c>CREATE DATABASE</c> has returned, and the statement overrides replace the claim or the best-effort
    /// drop, so a test can interrupt the create exactly where a database exists but is not yet claimed.
    /// </summary>
    internal sealed record CreateSeams(
        Func<Task>? AfterCreate = null,
        string? ClaimStatementOverride = null,
        string? DropStatementOverride = null
    );

    /// <summary>
    /// On the holder's connection: <c>CREATE DATABASE</c>, the claim comment, then the instance read
    /// back by name. Once the database exists, the claim and the read run to their own end whatever happens
    /// to <paramref name="cancellationToken"/>, bounded by the driver's command timeout, because stopping
    /// between them would leave an unclaimed database nobody accounts for. A definite failure of the claim or
    /// the read drops the database again on a best-effort basis; a drop that cannot be confirmed is returned
    /// as <see cref="DmsResidueKind.StagingDatabase"/> residue on the failure. A failure in doubt is
    /// <c>Unknown</c> and the orchestrator accounts for the name.
    /// </summary>
    public static Task<DmsCommandOutcome<DmsInstanceIdentity>> CreateAsync(
        NpgsqlConnection connection,
        DmsDatabaseName staging,
        DmsProvisionClaim claim,
        DmsServerSessionId session,
        CancellationToken cancellationToken
    ) => CreateAsync(connection, staging, claim, session, seams: null, cancellationToken);

    internal static async Task<DmsCommandOutcome<DmsInstanceIdentity>> CreateAsync(
        NpgsqlConnection connection,
        DmsDatabaseName staging,
        DmsProvisionClaim claim,
        DmsServerSessionId session,
        CreateSeams? seams,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(claim);
        try
        {
            await ExecuteAsync(connection, CreateDatabaseStatement(staging), cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || IsAfterSend(exception))
        {
            return Classify<DmsInstanceIdentity>(exception, session, CreateStagingStep);
        }

        if (seams?.AfterCreate is { } afterCreate)
        {
            await afterCreate();
        }

        try
        {
            await ExecuteAsync(
                connection,
                seams?.ClaimStatementOverride ?? ClaimStatement(staging, claim),
                CancellationToken.None
            );
            DmsInstanceIdentity.Postgresql? instance = await ReadInstanceAsync(
                connection,
                staging,
                CancellationToken.None
            );
            if (instance is null)
            {
                return Failed<DmsInstanceIdentity>(
                    DmsCommandFailureClass.DoesNotExist,
                    DmsProvisioningDiagnostic.ForOperation(ClaimStagingStep)
                );
            }

            return new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(instance);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DmsCommandOutcome<DmsInstanceIdentity> outcome = Classify<DmsInstanceIdentity>(
                exception,
                session,
                ClaimStagingStep
            );
            if (
                outcome is not DmsCommandOutcome<DmsInstanceIdentity>.Failed
                || await TryDropAsync(connection, staging, seams?.DropStatementOverride)
            )
            {
                return outcome;
            }

            return outcome with
            {
                Residue = [new DmsCleanupResidue(DmsResidueKind.StagingDatabase, staging.Value)],
            };
        }
    }

    /// <summary>
    /// Cleanup on any session: the name must still hold the operation's instance (oid and claim), then
    /// <c>DROP DATABASE ... WITH (FORCE)</c>. An absent database, before or during the drop, is success; a
    /// different instance under the name is a definite failure that touches nothing.
    /// </summary>
    public static async Task<DmsCommandOutcome> DropStagingAsync(
        NpgsqlConnection connection,
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        DmsServerSessionId session,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(stagingInstance);
        DmsInstanceIdentity.Postgresql? current;
        try
        {
            current = await ReadInstanceAsync(connection, staging, cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || IsAfterSend(exception))
        {
            return Classify(exception, session, DropStagingStep);
        }

        if (current is null)
        {
            return new DmsCommandOutcome.Succeeded();
        }

        if (!current.IsSameStableInstance(stagingInstance))
        {
            return new DmsCommandOutcome.Failed(
                new DmsCommandFailure(
                    DmsCommandFailureClass.DoesNotExist,
                    DmsProvisioningDiagnostic.ForOperation(DropStagingInstanceStep)
                )
            );
        }

        try
        {
            await ExecuteAsync(connection, DropDatabaseStatement(staging), cancellationToken);
            return new DmsCommandOutcome.Succeeded();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            return new DmsCommandOutcome.Succeeded();
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || IsAfterSend(exception))
        {
            return Classify(exception, session, DropStagingStep);
        }
    }

    /// <summary>
    /// D-16 on any session: null when the role is in place, else the classified refusal (<c>55000</c> for an
    /// unsafe role, <c>42501</c> without <c>CREATEROLE</c>). The role is cluster-wide while target locks are
    /// per name, so creates for two targets can both find it missing on a fresh cluster and both send
    /// <c>CREATE ROLE</c>; the one that loses fails with <c>23505</c> or <c>42710</c> only once the winner's role
    /// is committed. That collision runs the block once more, in a new transaction that sees the committed
    /// role, so every attribute and membership check applies to it as to any existing role.
    /// </summary>
    public static Task<DmsCommandOutcome<DmsInstanceIdentity>?> EnsureEnqueueOwnerAsync(
        NpgsqlConnection connection,
        DmsServerSessionId session,
        CancellationToken cancellationToken
    ) => EnsureEnqueueOwnerAsync(connection, session, afterCollision: false, cancellationToken);

    private static async Task<DmsCommandOutcome<DmsInstanceIdentity>?> EnsureEnqueueOwnerAsync(
        NpgsqlConnection connection,
        DmsServerSessionId session,
        bool afterCollision,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            await ExecuteAsync(connection, EnsureEnqueueOwner, cancellationToken);
            return null;
        }
        catch (Exception exception)
            when (!afterCollision
                && PostgresqlErrorClassifier.SqlStateOf(exception)
                    is PostgresErrorCodes.UniqueViolation
                        or PostgresErrorCodes.DuplicateObject
            )
        {
            // A concurrent operation created the role first; its state is checked by the second run below.
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || IsAfterSend(exception))
        {
            string step =
                PostgresqlErrorClassifier.SqlStateOf(exception) == RoleStateUnsafeSqlState
                    ? DmsEngineOperations.RoleStateUnsafe
                    : EnsureEnqueueOwnerStep;
            return Classify<DmsInstanceIdentity>(exception, session, step);
        }

        return await EnsureEnqueueOwnerAsync(connection, session, afterCollision: true, cancellationToken);
    }

    /// <summary>The <c>oid</c> and claim comment under <paramref name="name"/>, or null when no database has the name.</summary>
    public static async Task<DmsInstanceIdentity.Postgresql?> ReadInstanceAsync(
        NpgsqlConnection connection,
        DmsDatabaseName name,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = new(InstanceByName, connection);
        command.Parameters.AddWithValue("name", name.Value);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        uint oid = await reader.GetFieldValueAsync<uint>(0, cancellationToken);
        string claim = await reader.IsDBNullAsync(1, cancellationToken) ? string.Empty : reader.GetString(1);
        return new DmsInstanceIdentity.Postgresql(oid, claim);
    }

    /// <summary>
    /// Over an exception: a SQLSTATE whose class proves no commit is <c>Failed</c>; every other
    /// exception (the driver's cancellation of a sent statement, a connection-class error, the server's
    /// statement of doubt, a transport failure) is <c>Unknown</c> for <paramref name="session"/>.
    /// </summary>
    public static DmsCommandOutcome Classify(Exception exception, DmsServerSessionId session, string step)
    {
        string? sqlState = PostgresqlErrorClassifier.SqlStateOf(exception);
        if (sqlState is not null && PostgresqlErrorClassifier.FailureClassOf(sqlState) is { } failureClass)
        {
            return new DmsCommandOutcome.Failed(
                new DmsCommandFailure(failureClass, PostgresqlErrorClassifier.Diagnostic(exception, step))
                {
                    Retryable = PostgresqlErrorClassifier.IsRetryableRejection(sqlState),
                }
            );
        }

        return new DmsCommandOutcome.Unknown(session);
    }

    public static DmsCommandOutcome<T> Classify<T>(
        Exception exception,
        DmsServerSessionId session,
        string step
    ) =>
        Classify(exception, session, step) switch
        {
            DmsCommandOutcome.Failed failed => new DmsCommandOutcome<T>.Failed(failed.Failure),
            _ => new DmsCommandOutcome<T>.Unknown(session),
        };

    private static DmsCommandOutcome<T> Failed<T>(
        DmsCommandFailureClass failureClass,
        DmsProvisioningDiagnostic diagnostic
    ) => new DmsCommandOutcome<T>.Failed(new DmsCommandFailure(failureClass, diagnostic));

    /// <summary>
    /// A cancellation the driver raised while a statement was in flight carries the server's cancellation
    /// state (<c>57014</c>) or follows a sent statement; one raised before anything was sent is the
    /// caller's and propagates. The driver reports the former with a PostgreSQL error in the chain.
    /// </summary>
    internal static bool IsAfterSend(Exception exception) =>
        PostgresqlErrorClassifier.SqlStateOf(exception) is not null;

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>One best-effort drop; true when the database is confirmed gone (dropped, or already absent).</summary>
    private static async Task<bool> TryDropAsync(
        NpgsqlConnection connection,
        DmsDatabaseName staging,
        string? statementOverride
    )
    {
        try
        {
            await ExecuteAsync(
                connection,
                statementOverride ?? DropDatabaseStatement(staging),
                CancellationToken.None
            );
            return true;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Not confirmed: the caller reports the name as residue.
            return false;
        }
    }
}
