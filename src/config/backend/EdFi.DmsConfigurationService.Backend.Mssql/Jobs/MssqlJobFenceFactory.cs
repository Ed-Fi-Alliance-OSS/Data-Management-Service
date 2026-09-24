// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Jobs;

/// <summary>Creates SQL Server fences bound to one execution's claim and ownership gate (spec D-5).</summary>
public sealed class MssqlJobFenceFactory(IOptions<DatabaseOptions> databaseOptions, JobLeaseTimings timings)
    : IJobFenceFactory
{
    /// <summary>Test seam: the statement that commits a fence, so a test can make the commit stall.</summary>
    internal string CommitStatement { get; init; } = MssqlJobSession.CommitTransaction;

    /// <summary>Test seam: the statement that ends a fence's session, so a test can make cleanup stall.</summary>
    internal string EndSessionStatement { get; init; } = MssqlJobSession.EndSession;

    public IJobFence Create(ClaimedJob job, JobExecutionOwnership ownership) =>
        new MssqlJobFence(
            databaseOptions.Value.DatabaseConnection,
            timings,
            job,
            ownership,
            CommitStatement,
            EndSessionStatement
        );

    /// <summary>
    /// One execution's fence (D-5). Under the execution gate it locks the job row, validates ownership with
    /// fresh database time in a separate batch, runs the consumer's work in the same transaction under a
    /// deadline taken from that time, revalidates ownership, and commits only while ownership is certain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Validation is a separate batch because SQL Server reads <c>SYSUTCDATETIME()</c> when a statement starts,
    /// so in a statement that waited for the row lock a lease that expired during the wait would still look live.
    /// <c>LOCK_TIMEOUT</c> is session-scoped: <see cref="JobLeaseTimings.FenceLockWait"/> applies to the whole
    /// fence transaction, the consumer's work included, and is reset to <c>-1</c> when the session ends.
    /// </para>
    /// <para>
    /// Every step is bounded while the fence holds the execution gate: the acquisition by its own timeout, and
    /// the work, the revalidation, the commit, and the session cleanup by the fence deadline. The commit is a
    /// T-SQL statement because SqlClient's own commit is synchronous and ignores the deadline
    /// (<see cref="MssqlJobSession"/>); a commit the deadline ends has an unknown outcome.
    /// </para>
    /// </remarks>
    private sealed class MssqlJobFence(
        string connectionString,
        JobLeaseTimings timings,
        ClaimedJob job,
        JobExecutionOwnership ownership,
        string commitStatement,
        string endSessionStatement
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

            FenceSession session = new(
                new SqlConnection(connectionString),
                OperationDeadline.Start(JobLeaseTimings.FenceAcquisitionTimeout)
            );
            try
            {
                await ExecuteLockedAsync(session, work, cancellationToken);
            }
            finally
            {
                if (!session.HandedOff)
                {
                    await MssqlJobSession.EndAsync(
                        session.Connection,
                        session.Transaction,
                        session.Deadline,
                        endSessionStatement
                    );
                }
            }
        }

        private async Task ExecuteLockedAsync(
            FenceSession session,
            Func<DbTransaction, CancellationToken, Task> work,
            CancellationToken cancellationToken
        )
        {
            // (2) Open and begin, bounded like the acquisition. The consumer's work needs an API transaction.
            using (CancellationTokenSource opening = session.Deadline.CreateTokenSource(cancellationToken))
            {
                await session.Connection.OpenAsync(opening.Token);
            }

            DbTransaction transaction;
            try
            {
                transaction = await MssqlJobSession.BeginApiTransactionAsync(
                    session.Connection,
                    session.Deadline,
                    cancellationToken,
                    () => session.HandedOff = true
                );
            }
            catch (TimeoutException)
            {
                throw new JobFenceUnavailableException();
            }

            session.Transaction = transaction;

            // (3) Acquisition, bounded by its own timeout rather than the fence deadline.
            session.Deadline = OperationDeadline.Start(JobLeaseTimings.FenceAcquisitionTimeout);
            TimeSpan remainingLease = await AcquireAsync(session, transaction, cancellationToken);

            // (4) One deadline, taken from fresh database time after the lock is held, covers the work, the
            // checks, the revalidation, the commit, and the session cleanup. It is never raised.
            TimeSpan fenceTimeout = Min(
                timings.FenceTimeout,
                remainingLease - JobLeaseTimings.FenceLeaseReserve
            );
            session.Deadline = OperationDeadline.Start(fenceTimeout);
            using CancellationTokenSource deadline = session.Deadline.CreateTokenSource(cancellationToken);

            await work(transaction, deadline.Token);

            // (5) The work returned: ownership must still be certain and the deadline not reached.
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested || ownership.State != JobOwnershipState.Owned)
            {
                throw new JobLeaseLostException();
            }

            // (6) Revalidation with fresh time, within the deadline.
            await RevalidateAsync(session, transaction, deadline, cancellationToken);

            // (7) A commit whose outcome is unknown, the deadline ending it included, makes this execution
            // uncertain. It is never retried.
            try
            {
                await MssqlJobSession.ExecuteAsync(
                    session.Connection,
                    commitStatement,
                    transaction,
                    session.Deadline,
                    deadline.Token
                );
            }
            catch (Exception)
            {
                ownership.TryMarkUncertain("FenceCommitUnknown");
                throw new JobLeaseLostException();
            }
        }

        private async Task<TimeSpan> AcquireAsync(
            FenceSession session,
            DbTransaction transaction,
            CancellationToken cancellationToken
        )
        {
            using CancellationTokenSource acquisition = session.Deadline.CreateTokenSource(cancellationToken);
            try
            {
                await MssqlJobSession.ExecuteAsync(
                    session.Connection,
                    _setFenceLockWait,
                    transaction,
                    session.Deadline,
                    acquisition.Token
                );
                await session.Connection.ExecuteScalarAsync<long?>(
                    new CommandDefinition(
                        LockJobRow,
                        new { Id = job.Id },
                        transaction,
                        session.Deadline.CommandTimeoutSeconds,
                        cancellationToken: acquisition.Token
                    )
                );
            }
            catch (SqlException exception) when (exception.Number == LockTimeoutErrorNumber)
            {
                throw new JobFenceUnavailableException();
            }

            LeaseRow? lease = await session.Connection.QuerySingleOrDefaultAsync<LeaseRow>(
                new CommandDefinition(
                    OwnedLease,
                    OwnershipParameters,
                    transaction,
                    session.Deadline.CommandTimeoutSeconds,
                    cancellationToken: acquisition.Token
                )
            );

            return lease is not null && lease.Remaining >= JobLeaseTimings.FenceMinimumRemainingLease
                ? lease.Remaining
                : throw new JobLeaseLostException();
        }

        private async Task RevalidateAsync(
            FenceSession session,
            DbTransaction transaction,
            CancellationTokenSource deadline,
            CancellationToken cancellationToken
        )
        {
            LeaseRow? lease;
            try
            {
                lease = await session.Connection.QuerySingleOrDefaultAsync<LeaseRow>(
                    new CommandDefinition(
                        OwnedLease,
                        OwnershipParameters,
                        transaction,
                        session.Deadline.CommandTimeoutSeconds,
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

        /// <summary>
        /// The fence's connection, its API transaction once begun, and the deadline that bounds the current
        /// step, which also bounds the session cleanup.
        /// </summary>
        private sealed class FenceSession(SqlConnection connection, OperationDeadline deadline)
        {
            public SqlConnection Connection { get; } = connection;

            public DbTransaction? Transaction { get; set; }

            public OperationDeadline Deadline { get; set; } = deadline;

            /// <summary>An unfinished begin now owns the connection and releases it when it ends.</summary>
            public bool HandedOff { get; set; }
        }

        private sealed record LeaseRow(DateTime LeaseExpiresAt, DateTime DatabaseUtcNow)
        {
            public TimeSpan Remaining => LeaseExpiresAt - DatabaseUtcNow;
        }
    }
}
