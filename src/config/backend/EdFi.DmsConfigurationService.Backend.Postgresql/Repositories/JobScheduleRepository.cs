// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

/// <summary>
/// PostgreSQL recurring schedules for <c>dmscs.JobSchedule</c> and their materialization into <c>dmscs.Job</c>
/// (spec D-8, D-9, D-10). Failures are returned as results carrying a <see cref="JobFailureDiagnostic"/>; nothing
/// here logs a message or a value.
/// </summary>
/// <remarks>
/// A materialization is one transaction under one <see cref="JobScheduleTimings.MaterializationTimeout"/>
/// deadline: it locks one due schedule, skipping locked rows, leases it, inserts the occurrence as a job unless
/// one already exists, and advances the schedule with a statement whose one <c>clock_timestamp()</c> sample is
/// read after the insert. Upsert, disable, and list are single statements bounded by
/// <see cref="JobScheduleTimings.CommandTimeout"/>. Every wait runs through a <see cref="JobDatabaseSession"/>.
/// </remarks>
public sealed class JobScheduleRepository(
    IOptions<DatabaseOptions> databaseOptions,
    IAuditContext auditContext,
    ITenantContextProvider tenantContextProvider
) : IJobScheduleRepository
{
    private const string SystemUser = "system";

    private const string FreshTime = """WITH t AS (SELECT (clock_timestamp() AT TIME ZONE 'UTC') AS now)""";

    // D-10: the upsert waits on the row lock of a materialization in progress, and PostgreSQL evaluates the
    // DO UPDATE assignments after it holds the conflicting row, so their clock_timestamp() is read after any
    // wait. The insert path is a pure insert and uses the statement's now(), like the CreatedAt default.
    // Every assignment keeps the stored value when nothing changed, so an identical upsert is a no-op.
    private const string UpsertChanged = """
        (NOT s."Enabled" OR s."JobType" <> EXCLUDED."JobType" OR s."PayloadVersion" <> EXCLUDED."PayloadVersion"
            OR s."Payload" <> EXCLUDED."Payload" OR s."IntervalMinutes" <> EXCLUDED."IntervalMinutes")
        """;

    private const string UpsertFreshNow = """(clock_timestamp() AT TIME ZONE 'UTC')""";

    private const string UpsertNewInterval = """(EXCLUDED."IntervalMinutes" * interval '1 minute')""";

    private const string UpsertSqlTemplate = $"""
        INSERT INTO "dmscs"."JobSchedule" AS s (
            "TenantId", "ScheduleType", "JobType", "PayloadVersion", "Payload", "IntervalMinutes", "Enabled",
            "NextRunAt", "CreatedBy")
        SELECT @TenantId, @ScheduleType, @JobType, @PayloadVersion, @Payload, @IntervalMinutes, TRUE,
            CASE WHEN @RunFirstOccurrenceImmediately THEN sample.now
                ELSE sample.now + @IntervalMinutes * interval '1 minute' END,
            @User
        FROM (SELECT (now() AT TIME ZONE 'UTC') AS now) AS sample
        ON CONFLICT {"{0}"} DO UPDATE
        SET "Enabled" = TRUE,
            "JobType" = EXCLUDED."JobType",
            "PayloadVersion" = EXCLUDED."PayloadVersion",
            "Payload" = EXCLUDED."Payload",
            "IntervalMinutes" = EXCLUDED."IntervalMinutes",
            "NextRunAt" = CASE
                WHEN NOT s."Enabled" AND s."NextRunAt" <= {UpsertFreshNow}
                    THEN {UpsertFreshNow} + {UpsertNewInterval}
                WHEN s."IntervalMinutes" <> EXCLUDED."IntervalMinutes"
                    THEN LEAST(s."NextRunAt", {UpsertFreshNow} + {UpsertNewInterval})
                ELSE s."NextRunAt" END,
            "LastModifiedAt" = CASE WHEN {UpsertChanged} THEN {UpsertFreshNow} ELSE s."LastModifiedAt" END,
            "ModifiedBy" = CASE WHEN {UpsertChanged} THEN EXCLUDED."CreatedBy" ELSE s."ModifiedBy" END
        RETURNING s."Id";
        """;

    // The conflict targets name the predicates of the two partial unique indexes (D-10), so PostgreSQL infers
    // UX_JobSchedule_Tenant_Type or UX_JobSchedule_SingleTenant_Type.
    private static readonly string _upsertMultitenantSql = string.Format(
        UpsertSqlTemplate,
        """("TenantId", "ScheduleType") WHERE "TenantId" IS NOT NULL"""
    );

    private static readonly string _upsertSingleTenantSql = string.Format(
        UpsertSqlTemplate,
        """("ScheduleType") WHERE "TenantId" IS NULL"""
    );

    private const string DisableSet = """
        UPDATE "dmscs"."JobSchedule" AS s
        SET "Enabled" = FALSE,
            "LastModifiedAt" = CASE WHEN s."Enabled" THEN (clock_timestamp() AT TIME ZONE 'UTC')
                ELSE s."LastModifiedAt" END,
            "ModifiedBy" = CASE WHEN s."Enabled" THEN @User ELSE s."ModifiedBy" END
        """;

    private const string DisableByIdSql = $"""
        {DisableSet}
        WHERE s."Id" = @Id;
        """;

    private const string ListByTypeSql = """
        SELECT "Id", "TenantId", "Enabled", "IntervalMinutes", "NextRunAt"
        FROM "dmscs"."JobSchedule"
        WHERE "ScheduleType" = @ScheduleType
        ORDER BY "Id";
        """;

    // D-8 (1): one due schedule, skipping locked rows, so the statement never waits and its time is fresh. An
    // expired lease counts as free (defensive recovery); a live one does not.
    private const string LockDueSql = $"""
        {FreshTime}
        SELECT s."Id", s."NextRunAt"
        FROM "dmscs"."JobSchedule" AS s, t
        WHERE s."Enabled"
          AND s."NextRunAt" <= t.now
          AND (s."LeaseExpiresAt" IS NULL OR s."LeaseExpiresAt" <= t.now)
        ORDER BY s."NextRunAt", s."Id"
        LIMIT 1
        FOR UPDATE OF s SKIP LOCKED;
        """;

    // D-8 (2).
    private const string LeaseSql = $"""
        {FreshTime}
        UPDATE "dmscs"."JobSchedule" AS s
        SET "LeaseOwner" = @Owner,
            "LeaseExpiresAt" = t.now + @LeaseSeconds * interval '1 second',
            "FencingToken" = s."FencingToken" + 1
        FROM t
        WHERE s."Id" = @Id
        RETURNING s."FencingToken";
        """;

    // D-8 (3): the occurrence's values are copied from the locked schedule row. CreatedAt and NextAttemptAt share
    // one sample (A1). Only the occurrence index is an arbiter: any other unique violation still raises.
    private const string InsertOccurrenceSql = $"""
        INSERT INTO "dmscs"."Job" (
            "JobId", "TenantId", "JobType", "PayloadVersion", "Payload", "Status", "CreatedAt", "NextAttemptAt",
            "SourceScheduleId", "ScheduledOccurrence", "CreatedBy")
        SELECT @JobId, s."TenantId", s."JobType", s."PayloadVersion", s."Payload", 'Pending', sample.now,
            sample.now, s."Id", s."NextRunAt", '{SystemUser}'
        FROM "dmscs"."JobSchedule" AS s
        CROSS JOIN (SELECT (now() AT TIME ZONE 'UTC') AS now) AS sample
        WHERE s."Id" = @Id
        ON CONFLICT ("SourceScheduleId", "ScheduledOccurrence")
            WHERE "SourceScheduleId" IS NOT NULL AND "ScheduledOccurrence" IS NOT NULL
        DO NOTHING;
        """;

    // D-9 (A2): b = NextRunAt + j0·iv with j0 = GREATEST(floor(elapsed / interval), 0), then the first boundary
    // strictly after t.now. Written inline because a FROM item cannot reference the UPDATE target.
    private const string Interval = """(s."IntervalMinutes" * interval '1 minute')""";

    private const string Boundary =
        $"""(s."NextRunAt" + GREATEST(floor(extract(epoch from (t.now - s."NextRunAt")) / (s."IntervalMinutes" * 60)), 0) * {Interval})""";

    // D-8 (4), the coalescing decision point: the lease must still be this call's, live by fresh time.
    private const string AdvanceSql = $"""
        {FreshTime}
        UPDATE "dmscs"."JobSchedule" AS s
        SET "LastEnqueuedOccurrence" = s."NextRunAt",
            "NextRunAt" = CASE WHEN {Boundary} > t.now THEN {Boundary} ELSE {Boundary} + {Interval} END,
            "LeaseOwner" = NULL,
            "LeaseExpiresAt" = NULL
        FROM t
        WHERE s."Id" = @Id
          AND s."LeaseOwner" = @Owner
          AND s."FencingToken" = @Token
          AND s."LeaseExpiresAt" > t.now
          AND s."Enabled"
        RETURNING s."NextRunAt" AS "NewNextRunAt", t.now AS "DatabaseUtcNow";
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

        string sql = TenantContext switch
        {
            TenantContext.Multitenant => _upsertMultitenantSql,
            TenantContext.NotMultitenant => _upsertSingleTenantSql,
            _ => throw new InvalidOperationException(
                $"Unexpected tenant context type: {TenantContext.GetType().Name}"
            ),
        };

        JobDeadline budget = JobDeadline.Start(JobScheduleTimings.CommandTimeout);
        JobDatabaseSession session = NewSession();
        try
        {
            await PostgresqlJobSession.OpenAsync(session, budget, cancellationToken);
            long id = await session.RunAsync(
                "Upsert",
                token =>
                    session.Connection.ExecuteScalarAsync<long>(
                        PostgresqlJobSession.Command(
                            session,
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
                            budget,
                            token
                        )
                    ),
                budget,
                cancellationToken
            );

            return new JobScheduleUpsertResult.Success(id);
        }
        catch (Exception exception)
        {
            return new JobScheduleUpsertResult.FailureUnknown(
                PostgresqlJobDiagnostics.From(exception, "UpsertSchedule")
            );
        }
        finally
        {
            // An autocommitted statement leaves no transaction open.
            await session.EndAsync(budget, null);
        }
    }

    public Task<JobScheduleDisableResult> Disable(string scheduleType, CancellationToken cancellationToken)
    {
        ThrowIfNotKey(scheduleType, nameof(scheduleType));

        return DisableAsync(
            "DisableSchedule",
            $"""
            {DisableSet}
            WHERE s."ScheduleType" = @ScheduleType AND {TenantContext.TenantWhereClause("s")};
            """,
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
            DisableByIdSql,
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
        JobDeadline budget = JobDeadline.Start(JobScheduleTimings.CommandTimeout);
        JobDatabaseSession session = NewSession();
        try
        {
            await PostgresqlJobSession.OpenAsync(session, budget, cancellationToken);
            int rows = await session.RunAsync(
                operation,
                token =>
                    session.Connection.ExecuteAsync(
                        PostgresqlJobSession.Command(session, sql, parameters, budget, token)
                    ),
                budget,
                cancellationToken
            );

            return rows == 0
                ? new JobScheduleDisableResult.FailureNotFound()
                : new JobScheduleDisableResult.Success();
        }
        catch (Exception exception)
        {
            return new JobScheduleDisableResult.FailureUnknown(
                PostgresqlJobDiagnostics.From(exception, operation)
            );
        }
        finally
        {
            // An autocommitted statement leaves no transaction open.
            await session.EndAsync(budget, null);
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
            await PostgresqlJobSession.OpenAsync(session, budget, cancellationToken);
            IEnumerable<SummaryRow> rows = await session.RunAsync(
                "ListByType",
                token =>
                    session.Connection.QueryAsync<SummaryRow>(
                        PostgresqlJobSession.Command(
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
                PostgresqlJobDiagnostics.From(exception, "ListSchedulesByType")
            );
        }
        finally
        {
            // An autocommitted statement leaves no transaction open.
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
            await PostgresqlJobSession.OpenAsync(session, budget, cancellationToken);
            await PostgresqlJobSession.BeginAsync(session, budget, cancellationToken);

            DueRow? due = await session.RunAsync(
                "LockDue",
                token =>
                    session.Connection.QuerySingleOrDefaultAsync<DueRow>(
                        PostgresqlJobSession.Command(session, LockDueSql, null, budget, token)
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
                        PostgresqlJobSession.Command(
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
                    session.Connection.ExecuteAsync(
                        PostgresqlJobSession.Command(
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
                        PostgresqlJobSession.Command(
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

            await PostgresqlJobSession.CommitAsync(session, budget, cancellationToken);
            committed = true;

            DateTime occurrence = AsUtc(due.NextRunAt);
            DateTime newNextRunAt = AsUtc(advanced.NewNextRunAt);
            DateTime databaseUtcNow = AsUtc(advanced.DatabaseUtcNow);
            return inserted == 0
                ? new JobScheduleMaterializeResult.AlreadyEnqueued(
                    due.Id,
                    occurrence,
                    newNextRunAt,
                    databaseUtcNow
                )
                : new JobScheduleMaterializeResult.Materialized(
                    due.Id,
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
                PostgresqlJobDiagnostics.From(exception, "MaterializeNextDue")
            );
        }
        finally
        {
            // Every outcome but a commit rolls the transaction back, within what is left of the deadline, so no
            // lease is ever persisted.
            await session.EndAsync(budget, committed ? null : PostgresqlJobSession.Rollback(session));
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
        new(new NpgsqlConnection(databaseOptions.Value.DatabaseConnection), SessionHooks);

    // The columns hold UTC in TIMESTAMP (without time zone), which Npgsql reads as DateTimeKind.Unspecified.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    // Positional rows: Dapper binds the columns to the constructor in order, by name and type.
    private sealed record DueRow(long Id, DateTime NextRunAt);

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
