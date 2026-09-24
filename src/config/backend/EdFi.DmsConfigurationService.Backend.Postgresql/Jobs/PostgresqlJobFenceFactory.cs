// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Jobs;

/// <summary>Creates PostgreSQL fences bound to one execution's claim and ownership gate (spec D-5).</summary>
public sealed class PostgresqlJobFenceFactory(
    IOptions<DatabaseOptions> databaseOptions,
    JobLeaseTimings timings
) : IJobFenceFactory
{
    public IJobFence Create(ClaimedJob job, JobExecutionOwnership ownership) =>
        new PostgresqlJobFence(databaseOptions.Value.DatabaseConnection, timings, job, ownership);

    /// <summary>
    /// One execution's fence (D-5). Under the execution gate it locks the job row, validates ownership with
    /// fresh database time in a separate statement, runs the consumer's work in the same transaction under a
    /// deadline taken from that time, revalidates ownership, and commits only while ownership is certain.
    /// </summary>
    /// <remarks>
    /// Validation is a separate statement because PostgreSQL evaluates the <c>WHERE</c> clause and select list
    /// of a <c>SELECT … FOR UPDATE</c> before it waits for the lock when the holder releases the row
    /// unchanged, so a lease that expired during the wait would still look live in that statement.
    /// </remarks>
    private sealed class PostgresqlJobFence(
        string connectionString,
        JobLeaseTimings timings,
        ClaimedJob job,
        JobExecutionOwnership ownership
    ) : IJobFence
    {
        private static readonly string _setFenceLockWait =
            $"SET LOCAL lock_timeout = '{(long)JobLeaseTimings.FenceLockWait.TotalMilliseconds}ms';";

        private const string LockJobRow = """SELECT "Id" FROM "dmscs"."Job" WHERE "Id" = @Id FOR UPDATE;""";

        private const string OwnedLease = """
            WITH t AS (SELECT (clock_timestamp() AT TIME ZONE 'UTC') AS now)
            SELECT j."LeaseExpiresAt" - t.now AS "Remaining"
            FROM "dmscs"."Job" AS j, t
            WHERE j."Id" = @Id AND j."LeaseOwner" = @Owner AND j."FencingToken" = @Token
              AND j."Status" = 'InProgress' AND j."LeaseExpiresAt" > t.now;
            """;

        public async Task ExecuteAsync(
            Func<DbTransaction, CancellationToken, Task> work,
            CancellationToken cancellationToken
        )
        {
            // (1) The gate admits a waiting renewal first; a lost or uncertain ownership never reaches the
            // database.
            using IDisposable gate = await ownership.EnterAsync(cancellationToken);
            if (ownership.State != JobOwnershipState.Owned)
            {
                throw new JobLeaseLostException();
            }

            // (2) and (3): acquisition, bounded by its own command timeout rather than the fence deadline.
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
                cancellationToken
            );
            TimeSpan remainingLease = await AcquireAsync(connection, transaction, cancellationToken);

            // (4) One deadline, taken from fresh database time after the lock is held, covers the work, the
            // checks, the revalidation, and the commit. It is never raised.
            TimeSpan fenceTimeout = Min(
                timings.FenceTimeout,
                remainingLease - JobLeaseTimings.FenceLeaseReserve
            );
            long started = Stopwatch.GetTimestamp();
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            deadline.CancelAfter(fenceTimeout);

            await work(transaction, deadline.Token);

            // (5) The work returned: ownership must still be certain and the deadline not reached.
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested || ownership.State != JobOwnershipState.Owned)
            {
                throw new JobLeaseLostException();
            }

            // (6) Revalidation with fresh time, within the deadline.
            await RevalidateAsync(
                connection,
                transaction,
                started,
                fenceTimeout,
                deadline.Token,
                cancellationToken
            );

            // (7) A commit whose outcome is unknown makes this execution uncertain.
            try
            {
                await transaction.CommitAsync(deadline.Token);
            }
            catch (Exception)
            {
                ownership.TryMarkUncertain("FenceCommitUnknown");
                throw new JobLeaseLostException();
            }
        }

        private async Task<TimeSpan> AcquireAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken
        )
        {
            int acquisitionTimeout = JobLeaseRepository.Seconds(JobLeaseTimings.FenceAcquisitionTimeout);
            try
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        _setFenceLockWait,
                        transaction: transaction,
                        commandTimeout: acquisitionTimeout,
                        cancellationToken: cancellationToken
                    )
                );
                await connection.ExecuteScalarAsync<long?>(
                    new CommandDefinition(
                        LockJobRow,
                        new { Id = job.Id },
                        transaction,
                        acquisitionTimeout,
                        cancellationToken: cancellationToken
                    )
                );
            }
            catch (PostgresException exception)
                when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                throw new JobFenceUnavailableException();
            }

            TimeSpan? remaining = await connection.QuerySingleOrDefaultAsync<TimeSpan?>(
                new CommandDefinition(
                    OwnedLease,
                    OwnershipParameters,
                    transaction,
                    acquisitionTimeout,
                    cancellationToken: cancellationToken
                )
            );

            return remaining is { } lease && lease >= JobLeaseTimings.FenceMinimumRemainingLease
                ? lease
                : throw new JobLeaseLostException();
        }

        private async Task RevalidateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long started,
            TimeSpan fenceTimeout,
            CancellationToken deadline,
            CancellationToken cancellationToken
        )
        {
            TimeSpan? remaining;
            try
            {
                remaining = await connection.QuerySingleOrDefaultAsync<TimeSpan?>(
                    new CommandDefinition(
                        OwnedLease,
                        OwnershipParameters,
                        transaction,
                        JobLeaseRepository.SecondsLeft(started, fenceTimeout),
                        cancellationToken: deadline
                    )
                );
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new JobLeaseLostException();
            }

            if (remaining is null)
            {
                throw new JobLeaseLostException();
            }
        }

        private object OwnershipParameters =>
            new
            {
                Id = job.Id,
                Owner = job.LeaseOwner,
                Token = job.FencingToken,
            };

        private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;
    }
}
