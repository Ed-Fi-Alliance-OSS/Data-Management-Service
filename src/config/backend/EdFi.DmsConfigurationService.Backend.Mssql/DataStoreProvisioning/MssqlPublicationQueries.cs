// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The publication commands and the compensation drop, classified the way the
/// staging commands are: a normal return is definite success, an error the server reported in the command's own
/// response is a definite failure classified by its first error number, and anything else is
/// <see cref="DmsCommandOutcome.Unknown"/> naming the session the statement ran on. The rename and the drop are each
/// one guarded batch: the name, the <c>database_id</c> and the primary file are checked, and only then is the
/// statement run, in the same batch and without <c>TRY/CATCH</c>, so the error that refuses it reaches the client with
/// its own number (D-13; the check is still followed by a mutation, within the supported boundary). A guard that
/// fails answers a one-row result and touches nothing. <c>ALTER DATABASE … MODIFY NAME</c> and <c>DROP DATABASE</c>
/// take no parameters, so their names are quoted from validated names; the guards, the marker and every catalog
/// read are parameterized.
/// </summary>
internal static class MssqlPublicationQueries
{
    public const string RenameStep = "RenameIntoTarget";

    /// <summary>The staging name no longer holds the operation's instance; nothing was renamed.</summary>
    public const string RenameInstanceStep = "RenameIntoTarget.InstanceChanged";

    public const string AssignMarkerStep = "AssignMarker";

    /// <summary>The marker statement updated no row: no singleton row, or the session's database is not the instance.</summary>
    public const string AssignMarkerNoRowStep = "AssignMarker.NoRow";

    public const string DropInstanceStep = "DropInstance";

    /// <summary>The target name no longer holds the instance; nothing was dropped.</summary>
    public const string DropInstanceInstanceStep = "DropInstance.InstanceChanged";

    /// <summary>The value the guard's one-row result carries when the name does not hold the verified instance.</summary>
    private const int GuardFailed = 0;

    /// <summary>
    /// The guard every mutation shares: the database under <c>@name</c> is <c>@id</c> and its primary file is
    /// <c>@file</c>.
    /// </summary>
    private const string InstanceGuard =
        "EXISTS (SELECT 1 FROM sys.databases d WHERE d.name = @name AND d.database_id = @id AND (SELECT f.physical_name FROM sys.master_files f WHERE f.database_id = d.database_id AND f.file_id = 1) = @file)";

    /// <summary>
    /// The rename's identity transition: the name and the <c>create_date</c> of the instance, found by its stable pair
    /// (<c>database_id</c> and primary file), never by name.
    /// </summary>
    private const string TransitionQuery =
        "SELECT d.name, d.create_date FROM sys.databases d WHERE d.database_id = @id AND (SELECT f.physical_name FROM sys.master_files f WHERE f.database_id = d.database_id AND f.file_id = 1) = @file;";

    /// <summary>
    /// The marker: the singleton row, guarded in the statement itself by the session's database still being
    /// the verified instance (<c>DB_ID()</c> and its primary file). The row count is read from <c>@@ROWCOUNT</c>
    /// because the session may run with <c>NOCOUNT</c> on (the delivered identity read sets it).
    /// </summary>
    public const string MarkerStatement =
        "SET NOCOUNT ON; UPDATE [dms].[DataStoreIdentity] SET [SourceIdentity] = @expected WHERE [DataStoreIdentitySingletonId] = 1 AND DB_ID() = @id AND (SELECT f.physical_name FROM sys.master_files f WHERE f.database_id = DB_ID() AND f.file_id = 1) = @file; SELECT CONVERT(int, @@ROWCOUNT);";

    public static string GuardedRename(DmsDatabaseName staging, DmsTargetDatabaseName target) =>
        $"IF {InstanceGuard} ALTER DATABASE {MssqlIdentifiers.Quote(staging.Value)} MODIFY NAME = {MssqlIdentifiers.Quote(target.Value)}; ELSE SELECT CONVERT(int, {GuardFailed});";

    public static string GuardedDrop(DmsTargetDatabaseName target) =>
        $"IF {InstanceGuard} DROP DATABASE {MssqlIdentifiers.Quote(target.Value)}; ELSE SELECT CONVERT(int, {GuardFailed});";

    /// <summary>
    /// On the holder's connection: <paramref name="staging"/> must still hold
    /// <paramref name="stagingInstance"/>, then <c>ALTER DATABASE … MODIFY NAME</c>, which fails with <c>1801</c> when
    /// any database holds the target name and leaves that database untouched (D-6). Once the rename has returned, the
    /// identity transition is read to its end, whatever happens to <paramref name="cancellationToken"/>, bounded by
    /// the driver's command timeout: the instance found by its stable pair must be under <paramref name="target"/>, and
    /// its <c>create_date</c>, which the rename changed, is the snapshot every later guard uses. A read that
    /// fails or finds the instance elsewhere leaves the outcome unknown for the caller to resolve.
    /// </summary>
    public static async Task<DmsCommandOutcome<DmsInstanceIdentity>> RenameAsync(
        SqlConnection connection,
        DmsDatabaseName staging,
        DmsInstanceIdentity.Mssql stagingInstance,
        DmsTargetDatabaseName target,
        DmsServerSessionId session,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(stagingInstance);
        DmsCommandNotDispatchedException.ThrowIfCancellationRequested(cancellationToken);
        bool? guarded;
        try
        {
            guarded = await RunGuardedAsync(
                connection,
                GuardedRename(staging, target),
                staging,
                stagingInstance,
                commandTimeoutSeconds,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is SqlException or OperationCanceledException)
        {
            return Classify<DmsInstanceIdentity>(exception, session, RenameStep);
        }

        if (guarded == false)
        {
            return new DmsCommandOutcome<DmsInstanceIdentity>.Failed(
                new DmsCommandFailure(
                    DmsCommandFailureClass.DoesNotExist,
                    DmsProvisioningDiagnostic.ForOperation(RenameInstanceStep)
                )
            );
        }

        try
        {
            return
                await ReadTransitionAsync(
                    connection,
                    stagingInstance,
                    target,
                    commandTimeoutSeconds,
                    CancellationToken.None
                )
                    is { } renamed
                ? new DmsCommandOutcome<DmsInstanceIdentity>.Succeeded(renamed)
                : new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(session);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The rename returned, so the name changed, but the instance could not be read back.
            return new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(session);
        }
    }

    /// <summary>
    /// On the instance-bound connection: the marker statement with the instance predicate, then
    /// the delivered identity read on the same connection. One row updated and the expected value read back is
    /// success; no row updated, or an error the server reported in the statement's own response (a missing table, a
    /// constraint, a privilege), is a definite failure; anything else, including a read-back that fails or shows
    /// another value after the update returned, is unknown for <paramref name="session"/>.
    /// </summary>
    public static async Task<DmsCommandOutcome> AssignMarkerAsync(
        SqlConnection connection,
        DmsInstanceIdentity.Mssql instance,
        Guid expectedSourceIdentity,
        DmsServerSessionId session,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(instance);
        DmsCommandNotDispatchedException.ThrowIfCancellationRequested(cancellationToken);
        int updated;
        try
        {
            await using SqlCommand command = new(MarkerStatement, connection)
            {
                CommandTimeout = commandTimeoutSeconds,
            };
            command.Parameters.Add(
                new SqlParameter("@expected", SqlDbType.UniqueIdentifier) { Value = expectedSourceIdentity }
            );
            command.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = instance.DatabaseId });
            command.Parameters.Add(
                new SqlParameter("@file", SqlDbType.NVarChar, 260) { Value = instance.PrimaryFileTag }
            );
            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The marker statement returned no row count.");
            }

            updated = reader.GetInt32(0);
            await DrainAsync(reader, cancellationToken);
        }
        catch (Exception exception) when (exception is SqlException or OperationCanceledException)
        {
            return Classify(exception, session, AssignMarkerStep);
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

        // The update committed on its own (autocommit); the read-back runs to its end, bounded by the driver's
        // command timeout, and anything but the expected value is the caller's to resolve.
        try
        {
            await using SqlCommand read = new(MssqlInventoryQueries.SourceIdentityRows, connection)
            {
                CommandTimeout = commandTimeoutSeconds,
            };
            await using SqlDataReader reader = await read.ExecuteReaderAsync(CancellationToken.None);
            List<(string? Text, bool Overlength)> values = [];
            while (await reader.ReadAsync(CancellationToken.None))
            {
                values.Add(MssqlInventoryQueries.ReadSourceIdentityRow(reader));
            }

            // A longer stored value is never accepted as its expected prefix.
            return
                values.Count == 1
                && !values[0].Overlength
                && Guid.TryParseExact(values[0].Text, "D", out Guid readBack)
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
    /// The compensation drop on the holder's connection: <paramref name="target"/> must still hold
    /// <paramref name="instance"/> (<c>database_id</c> and primary file), then <c>DROP DATABASE</c> in the same batch.
    /// Another database under the name, or none, is a definite <see cref="DmsCommandFailureClass.DoesNotExist"/> that
    /// touches nothing. Unlike PostgreSQL's <c>WITH (FORCE)</c>, a session connected to the unmarked database refuses
    /// the drop (<c>3702</c>, object in use) and nothing is ended.
    /// </summary>
    public static async Task<DmsCommandOutcome> DropInstanceAsync(
        SqlConnection connection,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity.Mssql instance,
        DmsServerSessionId session,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(instance);
        cancellationToken.ThrowIfCancellationRequested();
        bool? guarded;
        try
        {
            guarded = await RunGuardedAsync(
                connection,
                GuardedDrop(target),
                target,
                instance,
                commandTimeoutSeconds,
                cancellationToken
            );
        }
        catch (Exception exception) when (exception is SqlException or OperationCanceledException)
        {
            return Classify(exception, session, DropInstanceStep);
        }

        return guarded == false
            ? new DmsCommandOutcome.Failed(
                new DmsCommandFailure(
                    DmsCommandFailureClass.DoesNotExist,
                    DmsProvisioningDiagnostic.ForOperation(DropInstanceInstanceStep)
                )
            )
            : new DmsCommandOutcome.Succeeded();
    }

    /// <summary>
    /// Runs a guarded batch and reads its whole response: false when the guard's one-row result came back (nothing
    /// was run), null when the statement ran and returned no result. An error the statement raised surfaces here as
    /// a <see cref="SqlException"/>, because draining the response reads every token the server sent.
    /// </summary>
    private static async Task<bool?> RunGuardedAsync(
        SqlConnection connection,
        string batch,
        DmsDatabaseName name,
        DmsInstanceIdentity.Mssql instance,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = new(batch, connection) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = name.Value });
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = instance.DatabaseId });
        command.Parameters.Add(
            new SqlParameter("@file", SqlDbType.NVarChar, 260) { Value = instance.PrimaryFileTag }
        );
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        bool guardFailed = reader.FieldCount > 0 && await reader.ReadAsync(cancellationToken);
        await DrainAsync(reader, cancellationToken);
        return guardFailed ? false : null;
    }

    /// <summary>
    /// The instance found by its stable pair, with the <c>create_date</c> read now, when it is under
    /// <paramref name="target"/>; otherwise null.
    /// </summary>
    private static async Task<DmsInstanceIdentity.Mssql?> ReadTransitionAsync(
        SqlConnection connection,
        DmsInstanceIdentity.Mssql instance,
        DmsTargetDatabaseName target,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken
    )
    {
        await using SqlCommand command = new(TransitionQuery, connection)
        {
            CommandTimeout = commandTimeoutSeconds,
        };
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = instance.DatabaseId });
        command.Parameters.Add(
            new SqlParameter("@file", SqlDbType.NVarChar, 260) { Value = instance.PrimaryFileTag }
        );
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        string name = reader.GetString(0);
        DateTime createDate = await reader.GetFieldValueAsync<DateTime>(1, cancellationToken);
        return string.Equals(name, target.Value, StringComparison.Ordinal)
            ? instance.WithCreateDateSnapshot(
                new DateTimeOffset(DateTime.SpecifyKind(createDate, DateTimeKind.Utc), TimeSpan.Zero)
            )
            : null;
    }

    /// <summary>Reads every remaining result, so an error later in the response is raised rather than discarded.</summary>
    private static async Task DrainAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        bool more;
        do
        {
            more = await reader.NextResultAsync(cancellationToken);
        } while (more);
    }

    /// <summary>An error the server reported for the statement is definite; anything else leaves it in doubt on <paramref name="session"/>.</summary>
    private static DmsCommandOutcome Classify(Exception exception, DmsServerSessionId session, string step) =>
        exception is SqlException && MssqlErrorClassifier.FailureClassOf(exception) is { } failureClass
            ? new DmsCommandOutcome.Failed(
                new DmsCommandFailure(failureClass, MssqlErrorClassifier.Diagnostic(exception, step))
                {
                    Retryable = MssqlErrorClassifier.IsRetryableRejection(exception),
                }
            )
            : new DmsCommandOutcome.Unknown(session);

    internal static DmsCommandOutcome<T> Classify<T>(
        Exception exception,
        DmsServerSessionId session,
        string step
    ) =>
        exception is SqlException && MssqlErrorClassifier.FailureClassOf(exception) is { } failureClass
            ? new DmsCommandOutcome<T>.Failed(
                new DmsCommandFailure(failureClass, MssqlErrorClassifier.Diagnostic(exception, step))
                {
                    Retryable = MssqlErrorClassifier.IsRetryableRejection(exception),
                }
            )
            : new DmsCommandOutcome<T>.Unknown(session);
}
