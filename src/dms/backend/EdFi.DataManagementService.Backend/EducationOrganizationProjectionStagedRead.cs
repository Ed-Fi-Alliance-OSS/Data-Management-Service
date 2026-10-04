// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using Microsoft.Extensions.Logging;
using Stage = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionReadStage;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// An open connection and whatever owns it. Disposing the owner releases the connection, and with it
/// any lease the provider took to open it.
/// </summary>
internal readonly record struct EducationOrganizationProjectionConnection(
    DbConnection Connection,
    IAsyncDisposable Owner
);

/// <summary>
/// What differs between the providers' projection reads.
/// </summary>
/// <param name="Dialect">The dialect the statement is compiled for.</param>
/// <param name="IsolationLevel">
/// The isolation that makes one fully consumed statement read one consistent set: PostgreSQL
/// <c>RepeatableRead</c> (a snapshot), SQL Server <c>Serializable</c> (shared and range locks held to
/// the end of the transaction).
/// </param>
/// <param name="IsExpectedAcquisitionFailure">
/// The provider's type-based classification of a failure to establish a connection. Applied to the
/// Acquire stage only.
/// </param>
/// <param name="Describe">The log-safe description of a provider exception: its type and provider code.</param>
/// <param name="ClassifyExecutionFailure">
/// The Execute stage's code-table classification of a provider exception, given its description.
/// </param>
/// <param name="LockTimeoutStatement">
/// The statement that bounds lock waits for the rest of the transaction, given whole seconds.
/// </param>
/// <param name="SessionRestoreStatement">
/// The statement that returns the session to its default isolation after the transaction ends, when
/// ending the transaction does not, so a pooled connection does not carry the read's isolation into
/// the next request. <see langword="null"/> when the provider needs none.
/// </param>
internal sealed record EducationOrganizationProjectionProvider(
    SqlDialect Dialect,
    IsolationLevel IsolationLevel,
    Predicate<Exception> IsExpectedAcquisitionFailure,
    Func<Exception, string> Describe,
    Func<DbException, string, EducationOrganizationProjectionSetResult> ClassifyExecutionFailure,
    Func<int, string> LockTimeoutStatement,
    string? SessionRestoreStatement
);

/// <summary>
/// Test-only observation of the read's database interaction. Production passes none.
/// </summary>
internal interface IEducationOrganizationProjectionReadObserver
{
    /// <summary>Called after the Acquire stage opened the connection, before the Prepare stage.</summary>
    Task AfterAcquireAsync(DbConnection connection, CancellationToken cancellationToken);

    /// <summary>Called for every command the read creates, after it is configured and before it runs.</summary>
    void CommandCreated(DbCommand command, DbTransaction transaction);

    /// <summary>
    /// Called between the Execute and Commit stages, after every row has been read.
    /// </summary>
    /// <param name="connection">The read's connection.</param>
    /// <param name="readerClosed">Whether the statement's data reader was closed.</param>
    /// <param name="cancellationToken">The read's token.</param>
    Task BeforeCommitAsync(DbConnection connection, bool readerClosed, CancellationToken cancellationToken);
}

/// <summary>
/// The provider-neutral projection read: one statement inside one explicit transaction, in four
/// stages with separate failure boundaries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Acquire</b> opens the connection through the provider's seam; only here are the type-based
/// acquisition classifiers applied. <b>Prepare</b> begins the transaction and bounds lock waits.
/// <b>Execute</b> runs the statement and reads every row into memory, then closes the data reader;
/// a database error is classified by the provider's code table and a value that cannot be read as
/// its required type is a permanent materialization mismatch. <b>Commit</b> commits; any failure or
/// indeterminate outcome there is transient and the rows are discarded, never returned.
/// </para>
/// <para>
/// Caller cancellation always propagates as <see cref="OperationCanceledException"/>, whatever the
/// provider raised for it; an exception the stage does not classify propagates as a defect. On every
/// path that does not commit, the transaction is rolled back and the connection released.
/// </para>
/// <para>
/// Nothing about an exception except its <see cref="EducationOrganizationProjectionProvider.Describe"/>
/// output is logged: never its message, the SQL, an object name, the connection string, or a hash.
/// </para>
/// </remarks>
internal static class EducationOrganizationProjectionStagedRead
{
    public static async Task<EducationOrganizationProjectionSetResult> ReadAsync(
        EducationOrganizationProjectionProvider provider,
        Func<CancellationToken, Task<EducationOrganizationProjectionConnection>> acquireAsync,
        EducationOrganizationProjectionSetReadRequest request,
        ILogger logger,
        IEducationOrganizationProjectionReadObserver? observer,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(acquireAsync);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(logger);

        var compilation = new EducationOrganizationProjectionSqlCompiler(provider.Dialect).Compile(
            request.MappingSet
        );

        if (compilation is EducationOrganizationProjectionSqlCompilation.Incompatible incompatible)
        {
            logger.LogWarning(
                "Education organization projection mapping is incompatible ({Reason})",
                incompatible.Incompatibility.Reason
            );
            return new EducationOrganizationProjectionSetResult.MappingIncompatible(
                incompatible.Incompatibility
            );
        }

        var plan = ((EducationOrganizationProjectionSqlCompilation.Compiled)compilation).Plan;
        int rowLimit = EducationOrganizationProjectionSetResult.RowLimitFor(request.MaxProjectionRows);
        long startedAt = Stopwatch.GetTimestamp();

        // Acquire
        EducationOrganizationProjectionConnection opened;
        try
        {
            opened = await acquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            throw CallerCancellation(exception, cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown or disposal of a data source is not an unavailable database.
            throw;
        }
        catch (DatabaseConnectionUnavailableException exception)
        {
            // The shared guard wraps only snapshot targets and the projection reads the primary, so
            // this is not expected; its description is already log-safe if it happens.
            return Unavailable(logger, Stage.Acquire, exception.FailureDescription);
        }
        catch (Exception exception) when (provider.IsExpectedAcquisitionFailure(exception))
        {
            return Unavailable(logger, Stage.Acquire, provider.Describe(exception));
        }

        await using (opened.Owner.ConfigureAwait(false))
        {
            DbConnection connection = opened.Connection;
            DbTransaction? transaction = null;

            try
            {
                // Prepare
                try
                {
                    if (observer is not null)
                    {
                        await observer.AfterAcquireAsync(connection, cancellationToken).ConfigureAwait(false);
                    }

                    transaction = await connection
                        .BeginTransactionAsync(provider.IsolationLevel, cancellationToken)
                        .ConfigureAwait(false);

                    await using DbCommand lockTimeout = CreateCommand(
                        connection,
                        transaction,
                        provider.LockTimeoutStatement(request.ReadLockTimeoutSeconds),
                        request.ReadCommandTimeoutSeconds,
                        observer
                    );
                    await lockTimeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw CallerCancellation(exception, cancellationToken);
                }
                catch (DbException exception)
                {
                    return Unavailable(logger, Stage.Prepare, provider.Describe(exception));
                }

                // Execute and Materialize
                List<EducationOrganizationProjectionRow> rows;
                bool readerClosed;
                try
                {
                    await using DbCommand select = CreateCommand(
                        connection,
                        transaction,
                        plan.Sql,
                        request.ReadCommandTimeoutSeconds,
                        observer,
                        (plan.RowLimitParameter, rowLimit)
                    );

                    DbDataReader reader = await select
                        .ExecuteReaderAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        rows = await EducationOrganizationProjectionRowReader
                            .ReadAllAsync(reader, plan.ResultColumns, cancellationToken)
                            .ConfigureAwait(false);

                        // Every row has been read; close before committing.
                        await reader.CloseAsync().ConfigureAwait(false);
                        readerClosed = reader.IsClosed;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw CallerCancellation(exception, cancellationToken);
                }
                catch (DbException exception)
                {
                    var classified = provider.ClassifyExecutionFailure(
                        exception,
                        provider.Describe(exception)
                    );
                    LogFailure(logger, classified);
                    return classified;
                }
                catch (Exception exception)
                    when (EducationOrganizationProjectionExecutionClassifier.IsMaterializationFailure(
                            exception
                        )
                    )
                {
                    var mismatch = new EducationOrganizationProjectionSetResult.SchemaIncompatible(
                        EducationOrganizationProjectionSchemaIncompatibilityReason.MaterializationTypeMismatch,
                        null
                    );
                    LogFailure(logger, mismatch);
                    return mismatch;
                }

                // Commit
                try
                {
                    if (observer is not null)
                    {
                        await observer
                            .BeforeCommitAsync(connection, readerClosed, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw CallerCancellation(exception, cancellationToken);
                }
                catch (Exception exception) when (exception is DbException or InvalidOperationException)
                {
                    // A broken connection surfaces as either type; in both the outcome is unknown, so
                    // the rows are discarded.
                    return Unavailable(logger, Stage.Commit, provider.Describe(exception));
                }

                var result = EducationOrganizationProjectionSetResult.FromCappedRows(
                    rows,
                    request.MaxProjectionRows
                );

                logger.LogDebug(
                    "Education organization projection read {RowCount} rows ({Outcome}) in {ElapsedMs} ms",
                    rows.Count,
                    result is EducationOrganizationProjectionSetResult.TooLarge ? "TooLarge" : "Set",
                    (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds
                );

                return result;
            }
            finally
            {
                if (transaction is not null)
                {
                    await DisposeTransactionAsync(transaction, provider, logger).ConfigureAwait(false);
                    await RestoreSessionAsync(connection, provider, request, logger).ConfigureAwait(false);
                }
            }
        }
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction transaction,
        string commandText,
        int commandTimeoutSeconds,
        IEducationOrganizationProjectionReadObserver? observer,
        (QuerySqlParameter Parameter, int Value)? rowLimit = null
    )
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.CommandTimeout = commandTimeoutSeconds;

        if (rowLimit is { } limit)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = $"@{limit.Parameter.ParameterName}";
            parameter.DbType = DbType.Int32;
            parameter.Value = limit.Value;
            command.Parameters.Add(parameter);
        }

        observer?.CommandCreated(command, transaction);
        return command;
    }

    /// <summary>
    /// Ends a transaction that may not have committed. Disposing an uncommitted transaction rolls it
    /// back; a failure doing so (the connection is already broken) must not replace the read's own
    /// outcome, and the connection is released next in any case.
    /// </summary>
    private static async Task DisposeTransactionAsync(
        DbTransaction transaction,
        EducationOrganizationProjectionProvider provider,
        ILogger logger
    )
    {
        try
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
#pragma warning disable S6667 // The exception carries provider text; only its description is logged.
            logger.LogDebug(
                "Education organization projection transaction cleanup failed with {Failure}",
                provider.Describe(exception)
            );
#pragma warning restore S6667
        }
    }

    /// <summary>
    /// Returns the session to its default isolation before the connection goes back to the pool. It
    /// runs on every path that began a transaction, including cancellation, so it does not take the
    /// caller's token. A failure means the connection is broken, and a broken connection is not
    /// reused, so it must not replace the read's own outcome.
    /// </summary>
    private static async Task RestoreSessionAsync(
        DbConnection connection,
        EducationOrganizationProjectionProvider provider,
        EducationOrganizationProjectionSetReadRequest request,
        ILogger logger
    )
    {
        if (provider.SessionRestoreStatement is null || connection.State != ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using DbCommand restore = connection.CreateCommand();
            restore.CommandText = provider.SessionRestoreStatement;
            restore.CommandTimeout = request.ReadCommandTimeoutSeconds;
            await restore.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbException exception)
        {
#pragma warning disable S6667 // The exception carries provider text; only its description is logged.
            logger.LogDebug(
                "Education organization projection session restore failed with {Failure}",
                provider.Describe(exception)
            );
#pragma warning restore S6667
        }
    }

    /// <summary>
    /// The caller's cancellation, whatever the provider raised for it. Npgsql 8 and SqlClient 6 raise
    /// <see cref="OperationCanceledException"/> themselves; a provider that reports a cancelled
    /// command as one of its own exceptions would otherwise be classified as an unavailable target.
    /// </summary>
    private static OperationCanceledException CallerCancellation(
        Exception exception,
        CancellationToken cancellationToken
    ) => new("The education organization projection read was cancelled.", exception, cancellationToken);

    private static EducationOrganizationProjectionSetResult Unavailable(
        ILogger logger,
        Stage stage,
        string describe
    )
    {
        var result = new EducationOrganizationProjectionSetResult.TargetUnavailable(stage, describe);
        LogFailure(logger, result);
        return result;
    }

    private static void LogFailure(ILogger logger, EducationOrganizationProjectionSetResult result)
    {
        switch (result)
        {
            case EducationOrganizationProjectionSetResult.TargetUnavailable unavailable:
                logger.LogWarning(
                    "Education organization projection read failed in the {Stage} stage with {Failure}",
                    unavailable.Stage,
                    unavailable.Describe
                );
                break;
            case EducationOrganizationProjectionSetResult.SchemaIncompatible incompatible:
                logger.LogWarning(
                    "Education organization projection read is schema incompatible ({Reason}, code {ProviderCode})",
                    incompatible.Reason,
                    incompatible.ProviderCode ?? "none"
                );
                break;
        }
    }
}
