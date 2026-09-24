// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Jobs;

/// <summary>Creates SQL Server fences bound to one execution's claim and ownership gate (spec D-5).</summary>
public sealed class MssqlJobFenceFactory(IOptions<DatabaseOptions> databaseOptions, JobLeaseTimings timings)
    : IJobFenceFactory
{
    public IJobFence Create(ClaimedJob job, JobExecutionOwnership ownership) =>
        new MssqlJobFence(databaseOptions.Value.DatabaseConnection, timings, job, ownership);

    /// <summary>
    /// One execution's fence (D-5). Under the execution gate it locks the job row, validates ownership with
    /// fresh database time in a separate batch, runs the consumer's work in the same transaction under a
    /// deadline taken from that time, revalidates ownership, and commits only while ownership is certain.
    /// </summary>
    /// <remarks>
    /// Validation is a separate batch because SQL Server reads <c>SYSUTCDATETIME()</c> when a statement starts,
    /// so in a statement that waited for the row lock a lease that expired during the wait would still look live.
    /// <c>LOCK_TIMEOUT</c> is session-scoped: <see cref="JobLeaseTimings.FenceLockWait"/> applies to the whole
    /// fence transaction, the consumer's work included, and is reset to <c>-1</c> before the connection is
    /// released.
    /// </remarks>
    private sealed class MssqlJobFence(
        string connectionString,
        JobLeaseTimings timings,
        ClaimedJob job,
        JobExecutionOwnership ownership
    ) : IJobFence
    {
        private const int LockTimeoutErrorNumber = 1222;

        private static readonly string _setFenceLockWait =
            $"SET LOCK_TIMEOUT {(long)JobLeaseTimings.FenceLockWait.TotalMilliseconds};";

        private const string LockJobRow =
            "SELECT Id FROM dmscs.Job WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE Id = @Id;";

        private const string OwnedLease = """
            DECLARE @Now DATETIME2 = SYSUTCDATETIME();
            SELECT LeaseExpiresAt, @Now AS DatabaseUtcNow
            FROM dmscs.Job
            WHERE Id = @Id AND LeaseOwner = @Owner AND FencingToken = @Token AND Status = N'InProgress'
              AND LeaseExpiresAt > @Now;
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

            await using SqlConnection connection = new(connectionString);
            try
            {
                await ExecuteLockedAsync(connection, work, cancellationToken);
            }
            finally
            {
                await JobLeaseRepository.RestoreLockTimeoutAsync(connection);
            }
        }

        private async Task ExecuteLockedAsync(
            SqlConnection connection,
            Func<DbTransaction, CancellationToken, Task> work,
            CancellationToken cancellationToken
        )
        {
            // (2) and (3): acquisition, bounded by its own command timeout rather than the fence deadline.
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
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
                deadline,
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
            SqlConnection connection,
            DbTransaction transaction,
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
            catch (SqlException exception) when (exception.Number == LockTimeoutErrorNumber)
            {
                throw new JobFenceUnavailableException();
            }

            LeaseRow? lease = await connection.QuerySingleOrDefaultAsync<LeaseRow>(
                new CommandDefinition(
                    OwnedLease,
                    OwnershipParameters,
                    transaction,
                    acquisitionTimeout,
                    cancellationToken: cancellationToken
                )
            );

            return lease is not null && lease.Remaining >= JobLeaseTimings.FenceMinimumRemainingLease
                ? lease.Remaining
                : throw new JobLeaseLostException();
        }

        private async Task RevalidateAsync(
            SqlConnection connection,
            DbTransaction transaction,
            long started,
            TimeSpan fenceTimeout,
            CancellationTokenSource deadline,
            CancellationToken cancellationToken
        )
        {
            LeaseRow? lease;
            try
            {
                lease = await connection.QuerySingleOrDefaultAsync<LeaseRow>(
                    new CommandDefinition(
                        OwnedLease,
                        OwnershipParameters,
                        transaction,
                        JobLeaseRepository.SecondsLeft(started, fenceTimeout),
                        cancellationToken: deadline.Token
                    )
                );
            }
            catch (Exception)
                when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The deadline ended the revalidation; SqlClient reports that as a SqlException or a cancellation.
                throw new JobLeaseLostException();
            }

            if (lease is null)
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

        private sealed record LeaseRow(DateTime LeaseExpiresAt, DateTime DatabaseUtcNow)
        {
            public TimeSpan Remaining => LeaseExpiresAt - DatabaseUtcNow;
        }
    }
}
