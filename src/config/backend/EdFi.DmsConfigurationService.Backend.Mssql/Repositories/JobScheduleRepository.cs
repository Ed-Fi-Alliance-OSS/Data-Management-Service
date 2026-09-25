// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Repositories;

/// <summary>
/// SQL Server recurring schedules for <c>dmscs.JobSchedule</c> and their materialization into <c>dmscs.Job</c>
/// (spec D-8, D-9, D-10). Failures are returned as results carrying a <see cref="JobFailureDiagnostic"/>; nothing
/// here logs a message or a value.
/// </summary>
/// <remarks>
/// SQL Server reads <c>SYSUTCDATETIME()</c> once, when a statement starts, so every write that may follow a lock
/// wait takes its time from a statement issued after the lock is held. A materialization is one API transaction
/// under one <see cref="JobScheduleTimings.MaterializationTimeout"/> deadline: it locks one due schedule, skipping
/// row-locked schedules, leases it, inserts the occurrence as a job unless one already exists, and advances the
/// schedule with a statement whose one time sample is read after the insert. Upsert and disable are short
/// transactions that lock the schedule's key and then write with time read after the lock; list is one statement.
/// Each is bounded by <see cref="JobScheduleTimings.CommandTimeout"/>. Every wait runs through a
/// <see cref="JobDatabaseSession"/>, commits are T-SQL statements bounded by the same deadline, and cleanup uses only
/// what is left of it (<see cref="MssqlJobSession"/>).
/// </remarks>
public sealed class JobScheduleRepository(
    IOptions<DatabaseOptions> databaseOptions,
    IAuditContext auditContext,
    ITenantContextProvider tenantContextProvider
) : IJobScheduleRepository
{
    private const string SystemUser = "system";

    // D-14: the key column is BIN2, and DATALENGTH rejects a value that matches only after SQL Server pads the
    // shorter operand with spaces.
    private const string ScheduleTypeMatch =
        "ScheduleType = @ScheduleType AND DATALENGTH(ScheduleType) = DATALENGTH(@ScheduleType)";

    private const string NewInterval = "DATEADD(minute, @IntervalMinutes, @Now)";

    // D-10: the key read takes an update lock and a key-range lock (HOLDLOCK), so a concurrent upsert of the same key
    // waits here, and a materialization in progress blocks it until its commit. @Now is declared after that read,
    // so its time is read after any wait. The update runs only when something changes, so an identical upsert
    // changes nothing. The comparisons use the columns: JobType is BIN2, and Payload, stored in the database
    // collation, is compared in BIN2 with a length guard, so a change of case or of trailing spaces is a change.
    private const string UpsertSqlTemplate = $"""
        DECLARE @ScheduleId BIGINT;
        SELECT @ScheduleId = Id
        FROM dmscs.JobSchedule WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
        WHERE {"{0}"} AND {ScheduleTypeMatch};
        {"{1}"}
        DECLARE @Now DATETIME2 = SYSUTCDATETIME();
        IF @ScheduleId IS NULL
        BEGIN
            INSERT INTO dmscs.JobSchedule (
                TenantId, ScheduleType, JobType, PayloadVersion, Payload, IntervalMinutes, Enabled, NextRunAt,
                CreatedAt, CreatedBy)
            VALUES (@TenantId, @ScheduleType, @JobType, @PayloadVersion, @Payload, @IntervalMinutes, 1,
                IIF(@RunFirstOccurrenceImmediately = 1, @Now, {NewInterval}), @Now, @User);
            SET @ScheduleId = SCOPE_IDENTITY();
        END
        ELSE
        BEGIN
            UPDATE dmscs.JobSchedule
            SET Enabled = 1,
                JobType = @JobType,
                PayloadVersion = @PayloadVersion,
                Payload = @Payload,
                IntervalMinutes = @IntervalMinutes,
                NextRunAt = CASE
                    WHEN Enabled = 0 AND NextRunAt <= @Now THEN {NewInterval}
                    WHEN IntervalMinutes <> @IntervalMinutes THEN IIF(NextRunAt < {NewInterval}, NextRunAt, {NewInterval})
                    ELSE NextRunAt END,
                LastModifiedAt = @Now,
                ModifiedBy = @User
            WHERE Id = @ScheduleId
              AND (Enabled = 0
                OR IntervalMinutes <> @IntervalMinutes
                OR PayloadVersion <> @PayloadVersion
                OR JobType <> @JobType OR DATALENGTH(JobType) <> DATALENGTH(@JobType)
                OR Payload COLLATE Latin1_General_BIN2 <> @Payload COLLATE Latin1_General_BIN2
                OR DATALENGTH(Payload) <> DATALENGTH(@Payload));
        END
        SELECT @ScheduleId;
        """;

    // D-10 disable: lock the schedule, then write with time read after the lock. Disabling a disabled schedule
    // matches it and leaves its audit columns unchanged.
    private const string DisableSqlTemplate = """
        DECLARE @ScheduleId BIGINT;
        SELECT @ScheduleId = Id FROM dmscs.JobSchedule WITH (UPDLOCK, ROWLOCK) WHERE {0};
        DECLARE @Now DATETIME2 = SYSUTCDATETIME();
        UPDATE dmscs.JobSchedule
        SET Enabled = 0,
            LastModifiedAt = IIF(Enabled = 1, @Now, LastModifiedAt),
            ModifiedBy = IIF(Enabled = 1, @User, ModifiedBy)
        OUTPUT inserted.Id
        WHERE Id = @ScheduleId;
        """;

    private static readonly string _disableByIdSql = string.Format(DisableSqlTemplate, "Id = @Id");

    private const string ListByTypeSql = $"""
        SELECT Id, TenantId, Enabled, IntervalMinutes, NextRunAt
        FROM dmscs.JobSchedule
        WHERE {ScheduleTypeMatch}
        ORDER BY Id;
        """;

    // D-8 (1): one due schedule, skipping row-locked schedules. The statement's time is read when it starts; a wait
    // on a page lock only makes the choice conservative, and the advance validates with its own fresh time. An
    // expired lease counts as free (defensive recovery); a live one does not.
    private const string LockDueSql = """
        SELECT TOP (1) Id, NextRunAt, ScheduleType
        FROM dmscs.JobSchedule WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE Enabled = 1
          AND NextRunAt <= SYSUTCDATETIME()
          AND (LeaseExpiresAt IS NULL OR LeaseExpiresAt <= SYSUTCDATETIME())
        ORDER BY NextRunAt, Id;
        """;

    // D-8 (2).
    private const string LeaseSql = """
        DECLARE @Now DATETIME2 = SYSUTCDATETIME();
        UPDATE dmscs.JobSchedule
        SET LeaseOwner = @Owner,
            LeaseExpiresAt = DATEADD(second, @LeaseSeconds, @Now),
            FencingToken = FencingToken + 1
        OUTPUT inserted.FencingToken
        WHERE Id = @Id;
        """;

    // D-8 (3): the occurrence's values are copied from the locked schedule row, CreatedAt and NextAttemptAt share
    // one sample (A1), and an occurrence that already exists is skipped. The locks on the occurrence probe make a
    // concurrent insert of the same occurrence wait and then be seen, as PostgreSQL's ON CONFLICT does. Any other
    // unique violation, such as a JobId already taken, still raises.
    private const string InsertOccurrenceSql = $"""
        DECLARE @Now DATETIME2 = SYSUTCDATETIME();
        INSERT INTO dmscs.Job (
            JobId, TenantId, JobType, PayloadVersion, Payload, Status, CreatedAt, NextAttemptAt, SourceScheduleId,
            ScheduledOccurrence, CreatedBy)
        SELECT @JobId, s.TenantId, s.JobType, s.PayloadVersion, s.Payload, N'Pending', @Now, @Now, s.Id,
            s.NextRunAt, N'{SystemUser}'
        FROM dmscs.JobSchedule AS s
        WHERE s.Id = @Id
          AND NOT EXISTS (
            SELECT 1
            FROM dmscs.Job AS j WITH (UPDLOCK, HOLDLOCK)
            WHERE j.SourceScheduleId = s.Id AND j.ScheduledOccurrence = s.NextRunAt);
        SELECT @@ROWCOUNT;
        """;

    // D-9: j0 from DATEDIFF_BIG(second, …), which counts second boundaries and can overestimate the elapsed time by
    // less than one second, so j0 is the exact floor or one too high; the IIF keeps b when it is still in the future
    // and otherwise takes the next boundary.
    private const string Boundary =
        "DATEADD(minute, CAST(IIF(DATEDIFF_BIG(second, s.NextRunAt, @Now) < 0, 0, DATEDIFF_BIG(second, s.NextRunAt, @Now) / (s.IntervalMinutes * 60)) AS INT) * s.IntervalMinutes, s.NextRunAt)";

    // D-8 (4), the coalescing decision point, with one time sample read after the insert: the lease must still be
    // this call's, live by that time.
    private const string AdvanceSql = $"""
        DECLARE @Now DATETIME2 = SYSUTCDATETIME();
        UPDATE s
        SET LastEnqueuedOccurrence = s.NextRunAt,
            NextRunAt = IIF(b.Boundary > @Now, b.Boundary, DATEADD(minute, s.IntervalMinutes, b.Boundary)),
            LeaseOwner = NULL,
            LeaseExpiresAt = NULL
        OUTPUT inserted.NextRunAt AS NewNextRunAt, @Now AS DatabaseUtcNow
        FROM dmscs.JobSchedule AS s
        CROSS APPLY (SELECT {Boundary} AS Boundary) AS b
        WHERE s.Id = @Id
          AND s.LeaseOwner = @Owner
          AND s.FencingToken = @Token
          AND s.LeaseExpiresAt > @Now
          AND s.Enabled = 1;
        """;

    private TenantContext TenantContext => tenantContextProvider.Context;

    private long? TenantId =>
        TenantContext is TenantContext.Multitenant multitenant ? multitenant.TenantId : null;

    /// <summary>
    /// Test seam: runs on the materialization's transaction after the occurrence insert and before the
    /// advancing <c>UPDATE</c>.
    /// </summary>
    internal Func<DbTransaction, Task>? AfterInsertHook { get; init; }

    /// <summary>Test seam: replaces <see cref="JobScheduleTimings.MaterializationTimeout"/>.</summary>
    internal TimeSpan MaterializationTimeout { get; init; } = JobScheduleTimings.MaterializationTimeout;

    /// <summary>Test seam: the statement that commits a transaction, so a test can make a commit stall.</summary>
    internal string CommitStatement { get; init; } = MssqlJobSession.CommitTransaction;

    /// <summary>
    /// Test seam: a statement the upsert batch runs after its key read and before its write, so a test can hold two
    /// upserts between their reads and their inserts.
    /// </summary>
    internal string AfterUpsertKeyRead { get; init; } = "";

    /// <summary>Test seam: hooks into every session this repository opens.</summary>
    internal JobDatabaseSessionHooks? SessionHooks { get; init; }

    public async Task<JobScheduleUpsertResult> Upsert(
        JobScheduleUpsertCommand command,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(command.PayloadJson);
        ThrowIfNotKey(command.ScheduleType, nameof(command.ScheduleType));
        ThrowIfNotKey(command.JobType, nameof(command.JobType));

        string sql = string.Format(UpsertSqlTemplate, TenantContext.TenantWhereClause(), AfterUpsertKeyRead);

        try
        {
            long id = await InTransactionAsync<long>(
                "UpsertSchedule",
                sql,
                new
                {
                    TenantId,
                    command.ScheduleType,
                    command.JobType,
                    command.PayloadVersion,
                    Payload = command.PayloadJson,
                    command.IntervalMinutes,
                    command.RunFirstOccurrenceImmediately,
                    User = auditContext.GetCurrentUser(),
                },
                cancellationToken
            );

            return new JobScheduleUpsertResult.Success(id);
        }
        catch (Exception exception)
        {
            return new JobScheduleUpsertResult.FailureUnknown(
                MssqlJobDiagnostics.From(exception, "UpsertSchedule")
            );
        }
    }

    public Task<JobScheduleDisableResult> Disable(string scheduleType, CancellationToken cancellationToken)
    {
        ThrowIfNotKey(scheduleType, nameof(scheduleType));

        return DisableAsync(
            "DisableSchedule",
            string.Format(DisableSqlTemplate, $"{ScheduleTypeMatch} AND {TenantContext.TenantWhereClause()}"),
            new
            {
                ScheduleType = scheduleType,
                TenantId,
                User = auditContext.GetCurrentUser(),
            },
            cancellationToken
        );
    }

    public Task<JobScheduleDisableResult> DisableById(long id, CancellationToken cancellationToken) =>
        DisableAsync(
            "DisableScheduleById",
            _disableByIdSql,
            new { Id = id, User = auditContext.GetCurrentUser() },
            cancellationToken
        );

    private async Task<JobScheduleDisableResult> DisableAsync(
        string operation,
        string sql,
        object parameters,
        CancellationToken cancellationToken
    )
    {
        try
        {
            long? id = await InTransactionAsync<long?>(operation, sql, parameters, cancellationToken);
            return id is null
                ? new JobScheduleDisableResult.FailureNotFound()
                : new JobScheduleDisableResult.Success();
        }
        catch (Exception exception)
        {
            return new JobScheduleDisableResult.FailureUnknown(
                MssqlJobDiagnostics.From(exception, operation)
            );
        }
    }

    /// <summary>
    /// Runs one batch that returns a single value in a short API transaction under one
    /// <see cref="JobScheduleTimings.CommandTimeout"/> deadline, which also bounds its commit and cleanup.
    /// </summary>
    private async Task<T?> InTransactionAsync<T>(
        string operation,
        string sql,
        object parameters,
        CancellationToken cancellationToken
    )
    {
        JobDeadline budget = JobDeadline.Start(JobScheduleTimings.CommandTimeout);
        JobDatabaseSession session = NewSession();
        bool committed = false;
        try
        {
            await MssqlJobSession.OpenAsync(session, budget, cancellationToken);
            await MssqlJobSession.BeginAsync(session, budget, cancellationToken);
            T? value = await session.RunAsync(
                operation,
                token =>
                    session.Connection.QuerySingleOrDefaultAsync<T>(
                        MssqlJobSession.Command(session, sql, parameters, budget, token)
                    ),
                budget,
                cancellationToken
            );
            await MssqlJobSession.ExecuteAsync(session, "Commit", CommitStatement, budget, cancellationToken);
            committed = true;
            return value;
        }
        finally
        {
            await session.EndAsync(
                budget,
                committed ? null : MssqlJobSession.EndSessionWith(session, budget, MssqlJobSession.EndSession)
            );
        }
    }

    public async Task<JobScheduleListResult> ListByType(
        string scheduleType,
        CancellationToken cancellationToken
    )
    {
        ThrowIfNotKey(scheduleType, nameof(scheduleType));

        JobDeadline budget = JobDeadline.Start(JobScheduleTimings.CommandTimeout);
        JobDatabaseSession session = NewSession();
        try
        {
            await MssqlJobSession.OpenAsync(session, budget, cancellationToken);
            IEnumerable<SummaryRow> rows = await session.RunAsync(
                "ListByType",
                token =>
                    session.Connection.QueryAsync<SummaryRow>(
                        MssqlJobSession.Command(
                            session,
                            ListByTypeSql,
                            new { ScheduleType = scheduleType },
                            budget,
                            token
                        )
                    ),
                budget,
                cancellationToken
            );

            return new JobScheduleListResult.Success([.. rows.Select(row => row.ToSummary())]);
        }
        catch (Exception exception)
        {
            return new JobScheduleListResult.FailureUnknown(
                MssqlJobDiagnostics.From(exception, "ListSchedulesByType")
            );
        }
        finally
        {
            // An autocommitted statement leaves no transaction open and changes no session setting.
            await session.EndAsync(budget, null);
        }
    }

    public async Task<JobScheduleMaterializeResult> MaterializeNextDue(
        string owner,
        int leaseSeconds,
        string newJobId,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(newJobId);
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseSeconds, 1);

        JobDeadline budget = JobDeadline.Start(MaterializationTimeout);
        JobDatabaseSession session = NewSession();
        bool committed = false;
        try
        {
            await MssqlJobSession.OpenAsync(session, budget, cancellationToken);
            await MssqlJobSession.BeginAsync(session, budget, cancellationToken);

            DueRow? due = await session.RunAsync(
                "LockDue",
                token =>
                    session.Connection.QuerySingleOrDefaultAsync<DueRow>(
                        MssqlJobSession.Command(session, LockDueSql, null, budget, token)
                    ),
                budget,
                cancellationToken
            );
            if (due is null)
            {
                return new JobScheduleMaterializeResult.NoneDue();
            }

            long fencingToken = await session.RunAsync(
                "Lease",
                token =>
                    session.Connection.ExecuteScalarAsync<long>(
                        MssqlJobSession.Command(
                            session,
                            LeaseSql,
                            new
                            {
                                due.Id,
                                Owner = owner,
                                LeaseSeconds = leaseSeconds,
                            },
                            budget,
                            token
                        )
                    ),
                budget,
                cancellationToken
            );

            int inserted = await session.RunAsync(
                "InsertOccurrence",
                token =>
                    session.Connection.ExecuteScalarAsync<int>(
                        MssqlJobSession.Command(
                            session,
                            InsertOccurrenceSql,
                            new { JobId = newJobId, due.Id },
                            budget,
                            token
                        )
                    ),
                budget,
                cancellationToken
            );

            if (AfterInsertHook is { } afterInsert)
            {
                await afterInsert(session.Transaction!);
            }

            AdvancedRow? advanced = await session.RunAsync(
                "Advance",
                token =>
                    session.Connection.QuerySingleOrDefaultAsync<AdvancedRow>(
                        MssqlJobSession.Command(
                            session,
                            AdvanceSql,
                            new
                            {
                                due.Id,
                                Owner = owner,
                                Token = fencingToken,
                            },
                            budget,
                            token
                        )
                    ),
                budget,
                cancellationToken
            );
            if (advanced is null)
            {
                return new JobScheduleMaterializeResult.OwnershipLost();
            }

            // Bounded by the deadline even once it has started: a commit it ends has an unknown outcome.
            await MssqlJobSession.ExecuteAsync(session, "Commit", CommitStatement, budget, cancellationToken);
            committed = true;

            DateTime occurrence = AsUtc(due.NextRunAt);
            DateTime newNextRunAt = AsUtc(advanced.NewNextRunAt);
            DateTime databaseUtcNow = AsUtc(advanced.DatabaseUtcNow);
            return inserted == 0
                ? new JobScheduleMaterializeResult.AlreadyEnqueued(
                    due.Id,
                    due.ScheduleType,
                    occurrence,
                    newNextRunAt,
                    databaseUtcNow
                )
                : new JobScheduleMaterializeResult.Materialized(
                    due.Id,
                    due.ScheduleType,
                    newJobId,
                    occurrence,
                    newNextRunAt,
                    databaseUtcNow
                );
        }
        catch (Exception exception)
        {
            // A commit that did not report its result may have committed; the occurrence's unique index keeps a
            // later attempt from enqueuing it twice.
            return new JobScheduleMaterializeResult.FailureUnknown(
                MssqlJobDiagnostics.From(exception, "MaterializeNextDue")
            );
        }
        finally
        {
            // Every outcome but a commit rolls the transaction back, within what is left of the deadline, so no
            // lease is ever persisted.
            await session.EndAsync(
                budget,
                committed ? null : MssqlJobSession.EndSessionWith(session, budget, MssqlJobSession.EndSession)
            );
        }
    }

    private static void ThrowIfNotKey(string key, string paramName)
    {
        if (!JobKeySyntax.IsValid(key))
        {
            throw new ArgumentException("The value is not a valid job key.", paramName);
        }
    }

    private JobDatabaseSession NewSession() =>
        new(new SqlConnection(databaseOptions.Value.DatabaseConnection), SessionHooks);

    // DATETIME2 holds UTC, and SqlClient reads it as DateTimeKind.Unspecified.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    // Positional rows: Dapper binds the columns to the constructor in order, by name and type.
    private sealed record DueRow(long Id, DateTime NextRunAt, string ScheduleType);

    private sealed record AdvancedRow(DateTime NewNextRunAt, DateTime DatabaseUtcNow);

    private sealed record SummaryRow(
        long Id,
        long? TenantId,
        bool Enabled,
        int IntervalMinutes,
        DateTime NextRunAt
    )
    {
        public JobScheduleSummary ToSummary() =>
            new(Id, TenantId, Enabled, IntervalMinutes, AsUtc(NextRunAt));
    }
}
