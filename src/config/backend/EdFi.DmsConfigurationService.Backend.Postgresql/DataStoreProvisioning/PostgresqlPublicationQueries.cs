// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Npgsql;
using NpgsqlTypes;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The publication commands of 6.3 steps 8 and 10 and the compensation drop of 6.7, classified by 6.9a the
/// way the staging commands are: a normal return is definite success, a server-reported error whose
/// SQLSTATE proves no commit is a definite failure, and anything else is
/// <see cref="DmsCommandOutcome.Unknown"/> naming the session the statement ran on. Every mutation first
/// re-reads the instance under the name it addresses (D-13) and touches nothing when it differs; a failure
/// of that read is a definite failure, because the mutation was never sent. The statements that cannot
/// take parameters (<c>ALTER DATABASE … RENAME</c>, <c>DROP DATABASE</c>) are built only from validated
/// names (6.8); the marker and every catalog read are parameterized.
/// </summary>
internal static class PostgresqlPublicationQueries
{
    public const string RenameStep = "RenameIntoTarget";

    /// <summary>The instance read before the rename failed; the rename was never sent.</summary>
    public const string RenameVerifyStep = "RenameIntoTarget.Verify";

    /// <summary>The staging name no longer holds the operation's instance; nothing was renamed.</summary>
    public const string RenameInstanceStep = "RenameIntoTarget.InstanceChanged";

    public const string AssignMarkerStep = "AssignMarker";

    /// <summary>The marker statement updated no row: no singleton row, or the session's database is not the instance.</summary>
    public const string AssignMarkerNoRowStep = "AssignMarker.NoRow";

    public const string DropInstanceStep = "DropInstance";

    /// <summary>The instance read before the drop failed; the drop was never sent.</summary>
    public const string DropInstanceVerifyStep = "DropInstance.Verify";

    /// <summary>The target name no longer holds the instance; nothing was dropped.</summary>
    public const string DropInstanceInstanceStep = "DropInstance.InstanceChanged";

    /// <summary>
    /// The 6.3 step 10 marker: the singleton row, guarded in the statement itself by the session's database
    /// still being the verified instance.
    /// </summary>
    public const string MarkerStatement =
        "UPDATE \"dms\".\"DataStoreIdentity\" SET \"SourceIdentity\" = @expected WHERE \"DataStoreIdentitySingletonId\" = 1 AND (SELECT d.oid FROM pg_catalog.pg_database d WHERE d.datname = pg_catalog.current_database()) = @oid;";

    public static string RenameStatement(DmsDatabaseName staging, DmsTargetDatabaseName target) =>
        $"ALTER DATABASE {PostgresqlIdentifiers.Quote(staging.Value)} RENAME TO {PostgresqlIdentifiers.Quote(target.Value)};";

    /// <summary>
    /// 6.3 step 8 on the holder's connection: the staging name must still hold
    /// <paramref name="stagingInstance"/>, then <c>ALTER DATABASE … RENAME TO</c>, which fails with
    /// <c>42P04</c> when any database holds the target name and leaves that database untouched (D-6). Once
    /// the rename has returned, the instance is read back under the target name to its end, whatever
    /// happens to <paramref name="cancellationToken"/>, bounded by the driver's command timeout; a read that
    /// fails or finds nothing leaves the outcome unknown for the caller to resolve.
    /// </summary>
    public static async Task<DmsCommandOutcome<DmsInstanceIdentity>> RenameAsync(
        NpgsqlConnection connection,
        DmsDatabaseName staging,
        DmsInstanceIdentity stagingInstance,
        DmsTargetDatabaseName target,
        DmsServerSessionId session,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(stagingInstance);
        DmsCommandOutcome? refused = await VerifyAsync(
            connection,
            staging,
            stagingInstance,
            RenameVerifyStep,
            RenameInstanceStep,
            readCancellationNotDispatched: true,
            cancellationToken
        );
        if (refused is DmsCommandOutcome.Failed failed)
        {
            return new DmsCommandOutcome<DmsInstanceIdentity>.Failed(failed.Failure);
        }

        // The last check before the rename is handed to the driver: a cancellation from here on proves nothing.
        DmsCommandNotDispatchedException.ThrowIfCancellationRequested(cancellationToken);
        try
        {
            await ExecuteAsync(connection, RenameStatement(staging, target), cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                || PostgresqlStagingQueries.IsAfterSend(exception)
            )
        {
            return PostgresqlStagingQueries.Classify<DmsInstanceIdentity>(exception, session, RenameStep);
        }

        try
        {
            DmsInstanceIdentity.Postgresql? renamed = await PostgresqlStagingQueries.ReadInstanceAsync(
                connection,
                target,
                CancellationToken.None
            );
            return renamed is null
                ? new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(session)
                : new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(renamed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The rename returned, so the name changed, but the instance under it could not be read back.
            return new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(session);
        }
    }

    /// <summary>
    /// 6.3 step 10 and 6.9b on the instance-bound connection: the marker statement with the instance
    /// predicate, then the identity re-read on the same connection. One row updated and the expected value
    /// read back is success; no row updated, or a server error that proves no commit (a missing relation, a
    /// constraint, a privilege), is a definite failure; anything else, including a read-back that fails or
    /// shows another value after the update returned, is unknown for <paramref name="session"/>.
    /// </summary>
    public static async Task<DmsCommandOutcome> AssignMarkerAsync(
        NpgsqlConnection connection,
        uint oid,
        Guid expectedSourceIdentity,
        DmsServerSessionId session,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The last check before the marker is handed to the driver: a cancellation from here on proves nothing.
        DmsCommandNotDispatchedException.ThrowIfCancellationRequested(cancellationToken);
        int updated;
        try
        {
            await using NpgsqlCommand command = new(MarkerStatement, connection);
            command.Parameters.AddWithValue("expected", expectedSourceIdentity);
            command.Parameters.Add(new NpgsqlParameter<uint>("oid", NpgsqlDbType.Oid) { TypedValue = oid });
            updated = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                || PostgresqlStagingQueries.IsAfterSend(exception)
            )
        {
            return PostgresqlStagingQueries.Classify(exception, session, AssignMarkerStep);
        }

        if (updated == 0)
        {
            return new DmsCommandOutcome.Failed(
                new DmsCommandFailure(
                    DmsCommandFailureClass.Rejected,
                    DmsProvisioningDiagnostic.ForOperation(AssignMarkerNoRowStep)
                )
            );
        }

        // The update committed on its own (autocommit); the read-back runs to its end, bounded by the
        // driver's command timeout, and anything but the expected value is the caller's to resolve.
        try
        {
            await using NpgsqlCommand read = new(PostgresqlInventoryQueries.SourceIdentityRows, connection);
            await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(CancellationToken.None);
            List<string?> values = [];
            while (await reader.ReadAsync(CancellationToken.None))
            {
                values.Add(
                    await reader.IsDBNullAsync(0, CancellationToken.None) ? null : reader.GetString(0)
                );
            }

            return
                values.Count == 1
                && Guid.TryParseExact(values[0], "D", out Guid readBack)
                && readBack == expectedSourceIdentity
                ? new DmsCommandOutcome.Succeeded()
                : new DmsCommandOutcome.Unknown(session);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new DmsCommandOutcome.Unknown(session);
        }
    }

    /// <summary>
    /// The 6.7 compensation drop on the holder's connection: the target name must still hold
    /// <paramref name="instance"/>, then <c>DROP DATABASE … WITH (FORCE)</c>, which ends any session that
    /// connected to the unmarked database since the rename. A database already gone is success; a
    /// different instance under the name is a definite <see cref="DmsCommandFailureClass.DoesNotExist"/>
    /// that touches nothing.
    /// </summary>
    public static async Task<DmsCommandOutcome> DropInstanceAsync(
        NpgsqlConnection connection,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsServerSessionId session,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(instance);
        DmsCommandOutcome? refused = await VerifyAsync(
            connection,
            target,
            instance,
            DropInstanceVerifyStep,
            DropInstanceInstanceStep,
            readCancellationNotDispatched: false,
            cancellationToken
        );
        if (refused is not null)
        {
            return refused;
        }

        try
        {
            await ExecuteAsync(
                connection,
                PostgresqlStagingQueries.DropDatabaseStatement(target),
                cancellationToken
            );
            return new DmsCommandOutcome.Succeeded();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            return new DmsCommandOutcome.Succeeded();
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                || PostgresqlStagingQueries.IsAfterSend(exception)
            )
        {
            return PostgresqlStagingQueries.Classify(exception, session, DropInstanceStep);
        }
    }

    /// <summary>
    /// D-13 before a mutation: null when <paramref name="name"/> still holds <paramref name="expected"/>;
    /// otherwise the definite failure to return. A read that fails proves only that the mutation was not
    /// sent; a cancellation before anything was sent propagates as <see cref="DmsCommandNotDispatchedException"/>.
    /// With <paramref name="readCancellationNotDispatched"/> (the rename, which the caller withdraws on that
    /// exception) a read cancelled after it was sent (<c>57014</c>) propagates the same way: the read changed
    /// nothing and the mutation was not sent. Without it (the compensation drop) that read is the definite failure
    /// it was.
    /// </summary>
    private static async Task<DmsCommandOutcome?> VerifyAsync(
        NpgsqlConnection connection,
        DmsDatabaseName name,
        DmsInstanceIdentity expected,
        string verifyStep,
        string instanceStep,
        bool readCancellationNotDispatched,
        CancellationToken cancellationToken
    )
    {
        DmsInstanceIdentity.Postgresql? current;
        try
        {
            current = await PostgresqlStagingQueries.ReadInstanceAsync(connection, name, cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException
                || (!readCancellationNotDispatched && PostgresqlStagingQueries.IsAfterSend(exception))
            )
        {
            return new DmsCommandOutcome.Failed(
                new DmsCommandFailure(
                    DmsCommandFailureClass.Rejected,
                    PostgresqlErrorClassifier.Diagnostic(exception, verifyStep)
                )
            );
        }
        catch (OperationCanceledException canceled)
        {
            throw new DmsCommandNotDispatchedException(canceled);
        }

        return current is not null && current.IsSameStableInstance(expected)
            ? null
            : new DmsCommandOutcome.Failed(
                new DmsCommandFailure(
                    DmsCommandFailureClass.DoesNotExist,
                    DmsProvisioningDiagnostic.ForOperation(instanceStep)
                )
            );
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
