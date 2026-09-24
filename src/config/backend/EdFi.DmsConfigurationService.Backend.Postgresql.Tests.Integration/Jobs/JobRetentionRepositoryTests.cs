// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;

public class JobRetentionRepositoryTests
{
    private const int OneHour = 3600;

    public abstract class JobRetentionTestBase : JobSchemaTestBase
    {
        protected static JobRetentionRepository Repository(IOptions<DatabaseOptions>? options = null) =>
            new(options ?? Configuration.DatabaseOptions);

        /// <summary>
        /// Seeds one job. Offsets are seconds from database time; a null offset leaves the column null.
        /// </summary>
        protected async Task<long> SeedAsync(
            string status,
            double? finishedOffset,
            double nextAttemptOffset = -1,
            double? leaseOffset = null,
            string? leaseOwner = null
        ) =>
            await Connection!.ExecuteScalarAsync<long>(
                """
                INSERT INTO "dmscs"."Job" (
                    "JobId", "JobType", "PayloadVersion", "Payload", "Status", "NextAttemptAt", "FinishedAt",
                    "LeaseExpiresAt", "LeaseOwner", "ErrorMessage", "AttemptCount")
                VALUES (@JobId, 'DataStore.RefreshEducationOrganizations', 1, '{"dataStoreId":1}', @Status,
                    CASE WHEN @Status IN ('Pending', 'InProgress')
                        THEN (clock_timestamp() AT TIME ZONE 'UTC') + @NextAttemptOffset * interval '1 second' END,
                    (clock_timestamp() AT TIME ZONE 'UTC') + @FinishedOffset * interval '1 second',
                    (clock_timestamp() AT TIME ZONE 'UTC') + @LeaseOffset * interval '1 second',
                    @LeaseOwner,
                    CASE WHEN @Status = 'Error' THEN 'The job handler failed.' END,
                    1)
                RETURNING "Id";
                """,
                new
                {
                    JobId = Guid.NewGuid().ToString("N"),
                    Status = status,
                    NextAttemptOffset = nextAttemptOffset,
                    FinishedOffset = finishedOffset,
                    LeaseOffset = leaseOffset,
                    LeaseOwner = leaseOwner,
                }
            );

        protected async Task<HashSet<long>> RemainingIdsAsync() =>
            [.. await Connection!.QueryAsync<long>("""SELECT "Id" FROM "dmscs"."Job";""")];

        protected static int DeletedCount(JobRetentionResult result) =>
            result.Should().BeOfType<JobRetentionResult.Success>().Subject.DeletedCount;
    }

    [TestFixture]
    public class Given_jobs_in_every_state_around_the_retention_window : JobRetentionTestBase
    {
        private readonly Dictionary<string, long> _ids = [];
        private JobRetentionResult _result = null!;
        private HashSet<long> _remaining = [];

        [SetUp]
        public async Task Setup()
        {
            _ids.Clear();
            _ids["pending"] = await SeedAsync(JobStatuses.Pending, finishedOffset: null);
            _ids["in progress, expired lease"] = await SeedAsync(
                JobStatuses.InProgress,
                finishedOffset: null,
                leaseOffset: -60,
                leaseOwner: "crashed-owner"
            );
            _ids["retry pending in backoff"] = await SeedAsync(
                JobStatuses.Pending,
                finishedOffset: null,
                nextAttemptOffset: 600
            );

            // Active rows that also carry an old FinishedAt: only the status keeps them.
            _ids["pending, old finished at"] = await SeedAsync(
                JobStatuses.Pending,
                finishedOffset: -2 * OneHour
            );
            _ids["in progress, old finished at"] = await SeedAsync(
                JobStatuses.InProgress,
                finishedOffset: -2 * OneHour,
                leaseOffset: 300,
                leaseOwner: "live-owner"
            );

            _ids["completed recently"] = await SeedAsync(JobStatuses.Completed, finishedOffset: -10);
            _ids["completed long ago"] = await SeedAsync(JobStatuses.Completed, finishedOffset: -2 * OneHour);
            _ids["error long ago"] = await SeedAsync(JobStatuses.Error, finishedOffset: -2 * OneHour);

            // Either side of the cutoff, by database time.
            _ids["completed just past the window"] = await SeedAsync(
                JobStatuses.Completed,
                finishedOffset: -(OneHour + 2)
            );
            _ids["completed just inside the window"] = await SeedAsync(
                JobStatuses.Completed,
                finishedOffset: -(OneHour - 5)
            );

            _result = await Repository().DeleteFinishedOlderThan(OneHour, 100, CancellationToken.None);
            _remaining = await RemainingIdsAsync();
        }

        [Test]
        public void It_deletes_only_finished_rows_older_than_retention()
        {
            DeletedCount(_result).Should().Be(3);
            _ids.Where(entry => !_remaining.Contains(entry.Value))
                .Select(entry => entry.Key)
                .Should()
                .BeEquivalentTo("completed long ago", "error long ago", "completed just past the window");
        }

        [Test]
        public void It_keeps_active_jobs_whatever_their_finished_at()
        {
            foreach (
                string name in new[]
                {
                    "pending",
                    "in progress, expired lease",
                    "retry pending in backoff",
                    "pending, old finished at",
                    "in progress, old finished at",
                }
            )
            {
                _remaining.Should().Contain(_ids[name], name);
            }
        }

        [Test]
        public void It_measures_the_window_by_database_time()
        {
            _remaining.Should().NotContain(_ids["completed just past the window"]);
            _remaining.Should().Contain(_ids["completed just inside the window"]);
            _remaining.Should().Contain(_ids["completed recently"]);
        }
    }

    [TestFixture]
    public class Given_more_expired_jobs_than_one_batch : JobRetentionTestBase
    {
        private readonly List<int> _batches = [];
        private HashSet<long> _remaining = [];

        [SetUp]
        public async Task Setup()
        {
            _batches.Clear();
            for (int job = 0; job < 7; job++)
            {
                await SeedAsync(JobStatuses.Completed, finishedOffset: -2 * OneHour);
            }

            JobRetentionRepository repository = Repository();
            int deleted;
            do
            {
                deleted = DeletedCount(
                    await repository.DeleteFinishedOlderThan(OneHour, 3, CancellationToken.None)
                );
                _batches.Add(deleted);
            } while (deleted > 0);

            _remaining = await RemainingIdsAsync();
        }

        [Test]
        public void It_deletes_at_most_the_batch_size_per_call() => _batches.Should().Equal(3, 3, 1, 0);

        [Test]
        public void It_deletes_every_expired_job_across_the_batches() => _remaining.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_expired_job_locked_by_another_transaction : JobRetentionTestBase
    {
        private long _locked;
        private JobRetentionResult _result = null!;
        private TimeSpan _elapsed;
        private HashSet<long> _remaining = [];

        [SetUp]
        public async Task Setup()
        {
            _locked = await SeedAsync(JobStatuses.Completed, finishedOffset: -2 * OneHour);
            await SeedAsync(JobStatuses.Completed, finishedOffset: -2 * OneHour);

            await using (
                JobLeaseRepositoryTests.RowLock rowLock = await JobLeaseRepositoryTests.RowLock.AcquireAsync(
                    _locked
                )
            )
            {
                long started = Stopwatch.GetTimestamp();
                _result = await Repository().DeleteFinishedOlderThan(OneHour, 100, CancellationToken.None);
                _elapsed = Stopwatch.GetElapsedTime(started);
            }

            _remaining = await RemainingIdsAsync();
        }

        [Test]
        public void It_skips_the_locked_job_without_waiting()
        {
            _elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
            DeletedCount(_result).Should().Be(1);
            _remaining.Should().Equal(_locked);
        }
    }

    [TestFixture]
    public class Given_a_database_failure : JobRetentionTestBase
    {
        private JobRetentionResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            NpgsqlConnectionStringBuilder missing = new(
                Configuration.DatabaseOptions.Value.DatabaseConnection
            )
            {
                Database = $"missing_{Guid.NewGuid():N}",
            };
            _result = await Repository(
                    Options.Create(
                        new DatabaseOptions
                        {
                            DatabaseConnection = missing.ConnectionString,
                            EncryptionKey = Configuration.DatabaseOptions.Value.EncryptionKey,
                        }
                    )
                )
                .DeleteFinishedOlderThan(OneHour, 100, CancellationToken.None);
        }

        [Test]
        public void It_reports_the_failure_with_the_type_chain_and_SqlState() =>
            _result
                .Should()
                .BeOfType<JobRetentionResult.FailureUnknown>()
                .Which.Diagnostic.Should()
                .Be(new JobFailureDiagnostic("Npgsql.PostgresException", "3D000", "DeleteFinishedOlderThan"));
    }

    [TestFixture]
    public class Given_arguments_outside_their_bounds : JobRetentionTestBase
    {
        private Exception? _negativeRetention;
        private Exception? _emptyBatch;

        [SetUp]
        public async Task Setup()
        {
            _negativeRetention = await ThrownByAsync(() =>
                Repository().DeleteFinishedOlderThan(-1, 100, CancellationToken.None)
            );
            _emptyBatch = await ThrownByAsync(() =>
                Repository().DeleteFinishedOlderThan(OneHour, 0, CancellationToken.None)
            );
        }

        [Test]
        public void It_rejects_a_negative_retention_window() =>
            _negativeRetention.Should().BeOfType<ArgumentOutOfRangeException>();

        [Test]
        public void It_rejects_a_batch_size_below_one() =>
            _emptyBatch.Should().BeOfType<ArgumentOutOfRangeException>();

        private static async Task<Exception?> ThrownByAsync(Func<Task> operation)
        {
            try
            {
                await operation();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }
}
