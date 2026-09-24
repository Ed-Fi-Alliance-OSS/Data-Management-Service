// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;

public class JobScheduleRepositoryTests
{
    private const string Owner = "dispatcher-a";
    private const int LeaseSeconds = 30;
    private const string ScheduledJobType = "DataStore.RefreshEducationOrganizations";
    private const string SeededPayload = """{"dataStoreId":3}""";

    private static readonly JobLeaseTimings _leaseTimings = new(
        RenewalTimeout: TimeSpan.FromSeconds(30),
        FenceTimeout: TimeSpan.FromSeconds(10)
    );

    /// <summary>A stored <c>dmscs.JobSchedule</c> row.</summary>
    public sealed class StoredSchedule
    {
        public long Id { get; set; }
        public long? TenantId { get; set; }
        public string ScheduleType { get; set; } = "";
        public string JobType { get; set; } = "";
        public short PayloadVersion { get; set; }
        public string Payload { get; set; } = "";
        public int IntervalMinutes { get; set; }
        public bool Enabled { get; set; }
        public DateTime NextRunAt { get; set; }
        public DateTime? LastEnqueuedOccurrence { get; set; }
        public string? LeaseOwner { get; set; }
        public DateTime? LeaseExpiresAt { get; set; }
        public long FencingToken { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? LastModifiedAt { get; set; }
        public string? ModifiedBy { get; set; }
    }

    /// <summary>
    /// An <see cref="JobScheduleRepository.AfterInsertHook"/> that holds a materialization, with its row lock,
    /// until it is released.
    /// </summary>
    public sealed class Pause
    {
        private readonly TaskCompletionSource _reached = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _released = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Func<DbTransaction, Task> Hook =>
            async _ =>
            {
                _reached.TrySetResult();
                await _released.Task.WaitAsync(TimeSpan.FromSeconds(30));
            };

        public Task Reached => _reached.Task.WaitAsync(TimeSpan.FromSeconds(15));

        public void Release() => _released.TrySetResult();
    }

    public abstract class JobScheduleTestBase : JobSchemaTestBase
    {
        protected static JobScheduleRepository Repository(
            TenantContext? tenant = null,
            Func<DbTransaction, Task>? afterInsert = null,
            TimeSpan? materializationTimeout = null,
            IOptions<DatabaseOptions>? options = null,
            string? commitStatement = null,
            string afterUpsertKeyRead = ""
        ) =>
            new(
                options ?? MssqlTestConfiguration.DatabaseOptions,
                new TestAuditContext(),
                new TenantContextProvider { Context = tenant ?? new TenantContext.NotMultitenant() }
            )
            {
                AfterInsertHook = afterInsert,
                MaterializationTimeout = materializationTimeout ?? JobScheduleTimings.MaterializationTimeout,
                CommitStatement = commitStatement ?? MssqlJobSession.CommitTransaction,
                AfterUpsertKeyRead = afterUpsertKeyRead,
            };

        protected static string NewScheduleType() => $"Test.{Guid.NewGuid():N}";

        protected static string NewJobId() => Guid.NewGuid().ToString("N");

        protected static JobScheduleUpsertCommand Command(
            string scheduleType,
            int intervalMinutes = 60,
            bool runFirstOccurrenceImmediately = false,
            string payload = SeededPayload,
            string jobType = ScheduledJobType,
            short payloadVersion = 1
        ) =>
            new(
                scheduleType,
                jobType,
                payloadVersion,
                payload,
                intervalMinutes,
                runFirstOccurrenceImmediately
            );

        protected static Task<JobScheduleMaterializeResult> MaterializeAsync(
            JobScheduleRepository? repository = null,
            string? jobId = null
        ) =>
            (repository ?? Repository()).MaterializeNextDue(
                Owner,
                LeaseSeconds,
                jobId ?? NewJobId(),
                CancellationToken.None
            );

        protected static long IdOf(JobScheduleUpsertResult result) =>
            result.Should().BeOfType<JobScheduleUpsertResult.Success>().Subject.ScheduleId;

        protected static JobScheduleMaterializeResult.Materialized MaterializedOf(
            JobScheduleMaterializeResult result
        ) => result.Should().BeOfType<JobScheduleMaterializeResult.Materialized>().Subject;

        protected static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

        protected async Task<DateTime> DatabaseNowAsync() =>
            Utc(await Connection!.ExecuteScalarAsync<DateTime>("SELECT SYSUTCDATETIME();"));

        /// <summary>Seeds one schedule whose times are offsets, in seconds, from database time.</summary>
        protected async Task<long> SeedScheduleAsync(
            double nextRunOffset,
            int intervalMinutes = 60,
            string? scheduleType = null,
            long? tenantId = null,
            bool enabled = true,
            string? leaseOwner = null,
            double? leaseOffset = null,
            long fencingToken = 0
        ) =>
            await Connection!.ExecuteScalarAsync<long>(
                """
                INSERT INTO dmscs.JobSchedule (
                    TenantId, ScheduleType, JobType, PayloadVersion, Payload, IntervalMinutes, Enabled, NextRunAt,
                    LeaseOwner, LeaseExpiresAt, FencingToken)
                OUTPUT inserted.Id
                VALUES (@TenantId, @ScheduleType, @JobType, 1, @Payload, @IntervalMinutes, @Enabled,
                    DATEADD(millisecond, CAST(@NextRunOffset * 1000 AS INT), SYSUTCDATETIME()),
                    @LeaseOwner,
                    DATEADD(millisecond, CAST(@LeaseOffset * 1000 AS INT), SYSUTCDATETIME()),
                    @FencingToken);
                """,
                new
                {
                    TenantId = tenantId,
                    ScheduleType = scheduleType ?? NewScheduleType(),
                    JobType = ScheduledJobType,
                    Payload = SeededPayload,
                    IntervalMinutes = intervalMinutes,
                    Enabled = enabled,
                    NextRunOffset = nextRunOffset,
                    LeaseOwner = leaseOwner,
                    LeaseOffset = leaseOffset,
                    FencingToken = fencingToken,
                }
            );

        protected async Task<StoredSchedule> ReadScheduleAsync(long id)
        {
            StoredSchedule schedule = await Connection!.QuerySingleAsync<StoredSchedule>(
                "SELECT * FROM dmscs.JobSchedule WHERE Id = @Id;",
                new { Id = id }
            );
            schedule.NextRunAt = Utc(schedule.NextRunAt);
            return schedule;
        }

        protected async Task<List<JobRepositoryTests.StoredJob>> JobsOfAsync(long scheduleId) =>
            [
                .. await Connection!.QueryAsync<JobRepositoryTests.StoredJob>(
                    "SELECT * FROM dmscs.Job WHERE SourceScheduleId = @Id ORDER BY Id;",
                    new { Id = scheduleId }
                ),
            ];

        protected async Task<long> JobCountAsync() =>
            await Connection!.ExecuteScalarAsync<long>("SELECT COUNT_BIG(*) FROM dmscs.Job;");
    }

    [TestFixture]
    public class Given_a_due_schedule : JobScheduleTestBase
    {
        private long _tenantId;
        private long _scheduleId;
        private DateTime _seededNextRunAt;
        private string _jobId = "";
        private JobScheduleMaterializeResult _result = null!;
        private JobScheduleMaterializeResult _second = null!;
        private StoredSchedule _schedule = null!;
        private List<JobRepositoryTests.StoredJob> _jobs = [];
        private JobClaimResult _claim = null!;

        [SetUp]
        public async Task Setup()
        {
            _tenantId = await CreateTenantAsync();
            _scheduleId = await SeedScheduleAsync(
                nextRunOffset: -10,
                intervalMinutes: 60,
                tenantId: _tenantId
            );
            _seededNextRunAt = (await ReadScheduleAsync(_scheduleId)).NextRunAt;
            _jobId = NewJobId();

            _result = await MaterializeAsync(jobId: _jobId);
            _second = await MaterializeAsync();
            _schedule = await ReadScheduleAsync(_scheduleId);
            _jobs = await JobsOfAsync(_scheduleId);
            _claim = await new JobLeaseRepository(
                MssqlTestConfiguration.DatabaseOptions,
                _leaseTimings
            ).ClaimNext("worker-a", LeaseSeconds, maxAttempts: 5, CancellationToken.None);
        }

        [Test]
        public void It_returns_the_materialized_occurrence()
        {
            JobScheduleMaterializeResult.Materialized materialized = MaterializedOf(_result);
            materialized.ScheduleId.Should().Be(_scheduleId);
            materialized.JobId.Should().Be(_jobId);
            materialized.Occurrence.Should().Be(_seededNextRunAt);
            materialized.NewNextRunAt.Should().Be(_seededNextRunAt.AddMinutes(60));
        }

        [Test]
        public void It_enqueues_one_pending_job_copied_from_the_schedule()
        {
            JobRepositoryTests.StoredJob job = _jobs.Should().ContainSingle().Subject;
            job.JobId.Should().Be(_jobId);
            job.TenantId.Should().Be(_tenantId);
            job.JobType.Should().Be(ScheduledJobType);
            job.PayloadVersion.Should().Be(1);
            job.Payload.Should().Be(SeededPayload);
            job.ScheduledOccurrence.Should().Be(_seededNextRunAt);
            job.CreatedBy.Should().Be("system");
            job.AttemptCount.Should().Be(0);
        }

        [Test]
        public void It_makes_the_job_eligible_from_its_creation() =>
            _jobs.Single().NextAttemptAt.Should().Be(_jobs.Single().CreatedAt);

        [Test]
        public void It_lets_a_worker_claim_the_job() =>
            _claim.Should().BeOfType<JobClaimResult.Claimed>().Which.Job.JobId.Should().Be(_jobId);

        [Test]
        public void It_advances_the_schedule_and_clears_its_lease()
        {
            _schedule.NextRunAt.Should().Be(_seededNextRunAt.AddMinutes(60));
            _schedule.LastEnqueuedOccurrence.Should().Be(_seededNextRunAt);
            _schedule.LeaseOwner.Should().BeNull();
            _schedule.LeaseExpiresAt.Should().BeNull();
            _schedule.FencingToken.Should().Be(1);
        }

        [Test]
        public void It_is_not_due_again() =>
            _second.Should().BeOfType<JobScheduleMaterializeResult.NoneDue>();
    }

    [TestFixture]
    public class Given_no_due_schedule : JobScheduleTestBase
    {
        private readonly List<long> _ids = [];
        private readonly Dictionary<long, StoredSchedule> _before = [];
        private JobScheduleMaterializeResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _ids.Clear();
            _before.Clear();
            _ids.Add(await SeedScheduleAsync(nextRunOffset: -60, enabled: false));
            _ids.Add(await SeedScheduleAsync(nextRunOffset: 60));
            _ids.Add(
                await SeedScheduleAsync(nextRunOffset: -60, leaseOwner: "live-dispatcher", leaseOffset: 30)
            );
            foreach (long id in _ids)
            {
                _before[id] = await ReadScheduleAsync(id);
            }

            _result = await MaterializeAsync();
        }

        [Test]
        public void It_reports_none_due() =>
            _result.Should().BeOfType<JobScheduleMaterializeResult.NoneDue>();

        [Test]
        public async Task It_leaves_every_schedule_unchanged()
        {
            foreach (long id in _ids)
            {
                (await ReadScheduleAsync(id)).Should().BeEquivalentTo(_before[id]);
            }

            (await JobCountAsync()).Should().Be(0);
        }
    }

    [TestFixture]
    public class Given_a_failure_after_the_occurrence_insert : JobScheduleTestBase
    {
        private long _scheduleId;
        private StoredSchedule _before = null!;
        private JobScheduleMaterializeResult _result = null!;
        private StoredSchedule _after = null!;
        private long _jobCount;
        private JobScheduleMaterializeResult _retry = null!;

        [SetUp]
        public async Task Setup()
        {
            _scheduleId = await SeedScheduleAsync(nextRunOffset: -10);
            _before = await ReadScheduleAsync(_scheduleId);

            _result = await MaterializeAsync(
                Repository(afterInsert: _ => throw new InvalidOperationException("injected"))
            );
            _after = await ReadScheduleAsync(_scheduleId);
            _jobCount = await JobCountAsync();
            _retry = await MaterializeAsync();
        }

        [Test]
        public void It_materializes_in_one_transaction()
        {
            _result
                .Should()
                .BeOfType<JobScheduleMaterializeResult.FailureUnknown>()
                .Which.Diagnostic.ExceptionTypeChain.Should()
                .Be("System.InvalidOperationException");
            _jobCount.Should().Be(0);
            _after
                .Should()
                .BeEquivalentTo(_before, "the lease and the insert rolled back with the transaction");
        }

        [Test]
        public void It_materializes_the_occurrence_on_the_next_call() =>
            MaterializedOf(_retry).Occurrence.Should().Be(_before.NextRunAt);
    }

    [TestFixture]
    public class Given_two_dispatchers_racing_for_one_occurrence : JobScheduleTestBase
    {
        private long _scheduleId;
        private JobScheduleMaterializeResult _first = null!;
        private JobScheduleMaterializeResult _second = null!;

        [SetUp]
        public async Task Setup()
        {
            _scheduleId = await SeedScheduleAsync(nextRunOffset: -10);
            Pause pause = new();

            Task<JobScheduleMaterializeResult> first = MaterializeAsync(Repository(afterInsert: pause.Hook));
            await pause.Reached;
            _second = await MaterializeAsync();
            pause.Release();
            _first = await first;
        }

        [Test]
        public void It_serializes_two_dispatchers_to_one_occurrence()
        {
            MaterializedOf(_first).ScheduleId.Should().Be(_scheduleId);
            _second.Should().BeOfType<JobScheduleMaterializeResult.NoneDue>("the locked schedule is skipped");
        }

        [Test]
        public async Task It_enqueues_the_occurrence_once() =>
            (await JobsOfAsync(_scheduleId)).Should().ContainSingle();
    }

    [TestFixture]
    public class Given_a_disable_while_a_materialization_holds_the_schedule : JobScheduleTestBase
    {
        private string _scheduleType = "";
        private long _scheduleId;
        private bool _disableCompletedWhileHeld;
        private JobScheduleMaterializeResult _materialized = null!;
        private JobScheduleDisableResult _disable = null!;
        private JobScheduleMaterializeResult _afterDisable = null!;

        [SetUp]
        public async Task Setup()
        {
            _scheduleType = NewScheduleType();
            _scheduleId = await SeedScheduleAsync(nextRunOffset: -10, scheduleType: _scheduleType);
            Pause pause = new();

            Task<JobScheduleMaterializeResult> materialize = MaterializeAsync(
                Repository(afterInsert: pause.Hook)
            );
            await pause.Reached;
            Task<JobScheduleDisableResult> disable = Repository()
                .Disable(_scheduleType, CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            _disableCompletedWhileHeld = disable.IsCompleted;
            pause.Release();
            _materialized = await materialize;
            _disable = await disable;

            // Due again, as after a long pause: a disabled schedule must still not be materialized.
            await Connection!.ExecuteAsync(
                """
                UPDATE dmscs.JobSchedule
                SET NextRunAt = DATEADD(minute, -1, SYSUTCDATETIME())
                WHERE Id = @Id;
                """,
                new { Id = _scheduleId }
            );
            _afterDisable = await MaterializeAsync();
        }

        [Test]
        public void It_blocks_disable_until_commit_then_stops_enqueue()
        {
            _disableCompletedWhileHeld.Should().BeFalse();
            MaterializedOf(_materialized).ScheduleId.Should().Be(_scheduleId);
            _disable.Should().BeOfType<JobScheduleDisableResult.Success>();
            _afterDisable.Should().BeOfType<JobScheduleMaterializeResult.NoneDue>();
        }

        [Test]
        public async Task It_keeps_the_job_enqueued_before_the_disable()
        {
            (await JobsOfAsync(_scheduleId)).Should().ContainSingle();
            (await ReadScheduleAsync(_scheduleId)).Enabled.Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_an_upsert_waiting_on_a_materialization : JobScheduleTestBase
    {
        private long _scheduleId;
        private bool _upsertCompletedWhileHeld;
        private DateTime _releasedAt;
        private JobScheduleMaterializeResult _materialized = null!;
        private JobScheduleUpsertResult _upsert = null!;
        private StoredSchedule _schedule = null!;

        [SetUp]
        public async Task Setup()
        {
            string scheduleType = NewScheduleType();
            _scheduleId = await SeedScheduleAsync(
                nextRunOffset: -1,
                intervalMinutes: 60,
                scheduleType: scheduleType
            );
            Pause pause = new();

            Task<JobScheduleMaterializeResult> materialize = MaterializeAsync(
                Repository(afterInsert: pause.Hook)
            );
            await pause.Reached;
            Task<JobScheduleUpsertResult> upsert = Repository()
                .Upsert(Command(scheduleType, intervalMinutes: 5), CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            _upsertCompletedWhileHeld = upsert.IsCompleted;
            _releasedAt = await DatabaseNowAsync();
            pause.Release();
            _materialized = await materialize;
            _upsert = await upsert;
            _schedule = await ReadScheduleAsync(_scheduleId);
        }

        [Test]
        public void It_applies_the_upsert_after_the_materialization_commits()
        {
            _upsertCompletedWhileHeld.Should().BeFalse();
            IdOf(_upsert).Should().Be(_scheduleId);
            MaterializedOf(_materialized).NewNextRunAt.Should().BeAfter(_schedule.NextRunAt);
        }

        [Test]
        public void It_computes_the_new_interval_from_time_read_after_the_lock() =>
            _schedule.NextRunAt.Should().BeOnOrAfter(_releasedAt.AddMinutes(5));
    }

    [TestFixture]
    public class Given_concurrent_upserts_of_one_new_schedule : JobScheduleTestBase
    {
        private string _scheduleType = "";
        private long _insertedId;
        private bool _upsertCompletedBeforeCommit;
        private JobScheduleUpsertResult _upsert = null!;

        [SetUp]
        public async Task Setup()
        {
            _scheduleType = NewScheduleType();

            // Another upsert's insert of the same key, not yet committed.
            await using SqlConnection other = new(MssqlTestConfiguration.DatabaseConnectionString);
            await other.OpenAsync();
            await using DbTransaction transaction = await other.BeginTransactionAsync();
            _insertedId = await other.ExecuteScalarAsync<long>(
                """
                INSERT INTO dmscs.JobSchedule (
                    ScheduleType, JobType, PayloadVersion, Payload, IntervalMinutes, Enabled, NextRunAt)
                OUTPUT inserted.Id
                VALUES (@ScheduleType, @JobType, 1, @Payload, 60, 1, DATEADD(minute, 60, SYSUTCDATETIME()));
                """,
                new
                {
                    ScheduleType = _scheduleType,
                    JobType = ScheduledJobType,
                    Payload = SeededPayload,
                },
                transaction
            );

            Task<JobScheduleUpsertResult> upsert = Repository()
                .Upsert(Command(_scheduleType, intervalMinutes: 30), CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            _upsertCompletedBeforeCommit = upsert.IsCompleted;
            await transaction.CommitAsync();
            _upsert = await upsert;
        }

        [Test]
        public void It_serializes_concurrent_upsert()
        {
            _upsertCompletedBeforeCommit.Should().BeFalse();
            IdOf(_upsert).Should().Be(_insertedId);
        }

        [Test]
        public async Task It_keeps_one_row_with_the_later_upsert_applied()
        {
            List<StoredSchedule> rows =
            [
                .. await Connection!.QueryAsync<StoredSchedule>(
                    "SELECT * FROM dmscs.JobSchedule WHERE ScheduleType = @ScheduleType;",
                    new { ScheduleType = _scheduleType }
                ),
            ];
            rows.Should().ContainSingle().Which.IntervalMinutes.Should().Be(30);
        }
    }

    [TestFixture]
    public class Given_upserts_of_new_schedules : JobScheduleTestBase
    {
        private DateTime _before;
        private DateTime _after;
        private StoredSchedule _later = null!;
        private StoredSchedule _immediate = null!;
        private JobScheduleMaterializeResult _materialized = null!;

        [SetUp]
        public async Task Setup()
        {
            _before = await DatabaseNowAsync();
            long later = IdOf(
                await Repository()
                    .Upsert(Command(NewScheduleType(), intervalMinutes: 60), CancellationToken.None)
            );
            long immediate = IdOf(
                await Repository()
                    .Upsert(
                        Command(NewScheduleType(), intervalMinutes: 60, runFirstOccurrenceImmediately: true),
                        CancellationToken.None
                    )
            );
            _after = await DatabaseNowAsync();

            _later = await ReadScheduleAsync(later);
            _immediate = await ReadScheduleAsync(immediate);
            _materialized = await MaterializeAsync();
        }

        [Test]
        public void It_stores_an_enabled_schedule_with_the_command_values()
        {
            _later.Enabled.Should().BeTrue();
            _later.TenantId.Should().BeNull();
            _later.JobType.Should().Be(ScheduledJobType);
            _later.Payload.Should().Be(SeededPayload);
            _later.IntervalMinutes.Should().Be(60);
            _later.CreatedBy.Should().Be("test-user");
            _later.LastModifiedAt.Should().BeNull();
        }

        [Test]
        public void It_schedules_the_first_run_one_interval_after_database_now()
        {
            _later.NextRunAt.Should().BeOnOrAfter(_before.AddMinutes(60));
            _later.NextRunAt.Should().BeOnOrBefore(_after.AddMinutes(60));
        }

        [Test]
        public void It_schedules_the_first_run_now_when_asked()
        {
            _immediate.NextRunAt.Should().BeOnOrAfter(_before);
            _immediate.NextRunAt.Should().BeOnOrBefore(_after);
            MaterializedOf(_materialized).ScheduleId.Should().Be(_immediate.Id);
        }
    }

    [TestFixture]
    public class Given_an_identical_upsert : JobScheduleTestBase
    {
        private long _firstId;
        private long _secondId;
        private StoredSchedule _changed = null!;
        private StoredSchedule _afterIdentical = null!;

        [SetUp]
        public async Task Setup()
        {
            string scheduleType = NewScheduleType();
            await Repository().Upsert(Command(scheduleType), CancellationToken.None);

            // A change, so LastModifiedAt has a value an identical upsert could overwrite.
            JobScheduleUpsertCommand changed = Command(scheduleType, payload: """{"dataStoreId":4}""");
            _firstId = IdOf(await Repository().Upsert(changed, CancellationToken.None));
            _changed = await ReadScheduleAsync(_firstId);
            await Task.Delay(TimeSpan.FromMilliseconds(50));

            _secondId = IdOf(await Repository().Upsert(changed, CancellationToken.None));
            _afterIdentical = await ReadScheduleAsync(_secondId);
        }

        [Test]
        public void It_returns_the_same_schedule() => _secondId.Should().Be(_firstId);

        [Test]
        public void It_changes_nothing()
        {
            _changed.LastModifiedAt.Should().NotBeNull();
            _afterIdentical.Should().BeEquivalentTo(_changed);
        }
    }

    [TestFixture]
    public class Given_upserts_that_change_existing_schedules : JobScheduleTestBase
    {
        private StoredSchedule _payloadBefore = null!;
        private StoredSchedule _payloadAfter = null!;
        private DateTime _shortenedAt;
        private StoredSchedule _shortened = null!;
        private StoredSchedule _lengthenedBefore = null!;
        private StoredSchedule _lengthened = null!;

        [SetUp]
        public async Task Setup()
        {
            string payloadType = NewScheduleType();
            long payloadId = await SeedScheduleAsync(nextRunOffset: 600, scheduleType: payloadType);
            _payloadBefore = await ReadScheduleAsync(payloadId);
            await Repository()
                .Upsert(
                    Command(
                        payloadType,
                        payload: """{"dataStoreId":9}""",
                        jobType: "DataStore.Other",
                        payloadVersion: 2
                    ),
                    CancellationToken.None
                );
            _payloadAfter = await ReadScheduleAsync(payloadId);

            string shortenedType = NewScheduleType();
            long shortenedId = await SeedScheduleAsync(
                nextRunOffset: 3600,
                intervalMinutes: 60,
                scheduleType: shortenedType
            );
            _shortenedAt = await DatabaseNowAsync();
            await Repository().Upsert(Command(shortenedType, intervalMinutes: 5), CancellationToken.None);
            _shortened = await ReadScheduleAsync(shortenedId);

            string lengthenedType = NewScheduleType();
            long lengthenedId = await SeedScheduleAsync(
                nextRunOffset: 300,
                intervalMinutes: 5,
                scheduleType: lengthenedType
            );
            _lengthenedBefore = await ReadScheduleAsync(lengthenedId);
            await Repository().Upsert(Command(lengthenedType, intervalMinutes: 120), CancellationToken.None);
            _lengthened = await ReadScheduleAsync(lengthenedId);
        }

        [Test]
        public void It_replaces_the_job_values_and_keeps_the_next_run()
        {
            _payloadAfter.JobType.Should().Be("DataStore.Other");
            _payloadAfter.PayloadVersion.Should().Be(2);
            _payloadAfter.Payload.Should().Be("""{"dataStoreId":9}""");
            _payloadAfter.NextRunAt.Should().Be(_payloadBefore.NextRunAt);
            _payloadAfter.LastModifiedAt.Should().NotBeNull();
            _payloadAfter.ModifiedBy.Should().Be("test-user");
        }

        [Test]
        public void It_brings_the_next_run_forward_for_a_shorter_interval()
        {
            _shortened.IntervalMinutes.Should().Be(5);
            _shortened.NextRunAt.Should().BeOnOrAfter(_shortenedAt.AddMinutes(5));
            _shortened.NextRunAt.Should().BeBefore(_shortenedAt.AddMinutes(6));
        }

        [Test]
        public void It_keeps_an_earlier_next_run_for_a_longer_interval()
        {
            _lengthened.IntervalMinutes.Should().Be(120);
            _lengthened.NextRunAt.Should().Be(_lengthenedBefore.NextRunAt);
        }
    }

    [TestFixture]
    public class Given_disabled_schedules_that_are_enabled_again : JobScheduleTestBase
    {
        private long _pastId;
        private long _reenabledPastId;
        private DateTime _reenabledAt;
        private StoredSchedule _past = null!;
        private StoredSchedule _futureBefore = null!;
        private StoredSchedule _future = null!;
        private JobScheduleMaterializeResult _materialized = null!;

        [SetUp]
        public async Task Setup()
        {
            string pastType = NewScheduleType();
            _pastId = await SeedScheduleAsync(
                nextRunOffset: -3 * 3600,
                scheduleType: pastType,
                enabled: false
            );
            string futureType = NewScheduleType();
            long futureId = await SeedScheduleAsync(
                nextRunOffset: 600,
                scheduleType: futureType,
                enabled: false
            );
            _futureBefore = await ReadScheduleAsync(futureId);

            _reenabledAt = await DatabaseNowAsync();
            _reenabledPastId = IdOf(await Repository().Upsert(Command(pastType), CancellationToken.None));
            await Repository().Upsert(Command(futureType), CancellationToken.None);

            _past = await ReadScheduleAsync(_pastId);
            _future = await ReadScheduleAsync(futureId);
            _materialized = await MaterializeAsync();
        }

        [Test]
        public void It_keeps_the_schedule_id() => _reenabledPastId.Should().Be(_pastId);

        [Test]
        public void It_does_not_burst_after_a_past_next_run()
        {
            _past.Enabled.Should().BeTrue();
            _past.NextRunAt.Should().BeOnOrAfter(_reenabledAt.AddMinutes(60));
            _materialized.Should().BeOfType<JobScheduleMaterializeResult.NoneDue>();
        }

        [Test]
        public void It_keeps_a_future_next_run()
        {
            _future.Enabled.Should().BeTrue();
            _future.NextRunAt.Should().Be(_futureBefore.NextRunAt);
        }
    }

    [TestFixture]
    public class Given_schedules_in_two_tenants_and_without_a_tenant : JobScheduleTestBase
    {
        private string _scheduleType = "";
        private long _tenantAId;
        private long _tenantBId;
        private readonly Dictionary<string, long> _ids = [];
        private long _repeatedAId;
        private long _repeatedNoTenantId;
        private JobScheduleListResult _listed = null!;
        private JobScheduleDisableResult _disableA = null!;
        private JobScheduleDisableResult _disableMissing = null!;
        private JobScheduleDisableResult _disableByIdMissing = null!;
        private JobScheduleListResult _afterDisable = null!;

        [SetUp]
        public async Task Setup()
        {
            _ids.Clear();
            _scheduleType = NewScheduleType();
            _tenantAId = await CreateTenantAsync();
            _tenantBId = await CreateTenantAsync();
            TenantContext tenantA = new TenantContext.Multitenant(_tenantAId, "tenant-a");
            TenantContext tenantB = new TenantContext.Multitenant(_tenantBId, "tenant-b");

            _ids["A"] = IdOf(
                await Repository(tenantA).Upsert(Command(_scheduleType), CancellationToken.None)
            );
            _ids["B"] = IdOf(
                await Repository(tenantB).Upsert(Command(_scheduleType), CancellationToken.None)
            );
            _ids["none"] = IdOf(await Repository().Upsert(Command(_scheduleType), CancellationToken.None));
            _repeatedAId = IdOf(
                await Repository(tenantA)
                    .Upsert(Command(_scheduleType, intervalMinutes: 30), CancellationToken.None)
            );
            _repeatedNoTenantId = IdOf(
                await Repository().Upsert(Command(_scheduleType, intervalMinutes: 30), CancellationToken.None)
            );

            _listed = await Repository().ListByType(_scheduleType, CancellationToken.None);
            _disableA = await Repository(tenantA).Disable(_scheduleType, CancellationToken.None);
            _disableMissing = await Repository(tenantA).Disable(NewScheduleType(), CancellationToken.None);
            _disableByIdMissing = await Repository().DisableById(-1, CancellationToken.None);
            _afterDisable = await Repository().ListByType(_scheduleType, CancellationToken.None);
        }

        [Test]
        public void It_keeps_one_schedule_per_tenant_and_type()
        {
            _ids.Values.Should().OnlyHaveUniqueItems();
            _repeatedAId.Should().Be(_ids["A"]);
            _repeatedNoTenantId.Should().Be(_ids["none"]);
        }

        [Test]
        public void It_lists_every_tenant_schedule_of_the_type() =>
            _listed
                .Should()
                .BeOfType<JobScheduleListResult.Success>()
                .Which.Schedules.Select(schedule =>
                    (schedule.Id, schedule.TenantId, schedule.IntervalMinutes)
                )
                .Should()
                .BeEquivalentTo(
                    new (long, long?, int)[]
                    {
                        (_ids["A"], _tenantAId, 30),
                        (_ids["B"], _tenantBId, 60),
                        (_ids["none"], null, 30),
                    }
                );

        [Test]
        public void It_disables_only_the_current_tenant_schedule()
        {
            _disableA.Should().BeOfType<JobScheduleDisableResult.Success>();
            _afterDisable
                .Should()
                .BeOfType<JobScheduleListResult.Success>()
                .Which.Schedules.Where(schedule => !schedule.Enabled)
                .Select(schedule => schedule.Id)
                .Should()
                .Equal(_ids["A"]);
        }

        [Test]
        public void It_reports_a_missing_schedule()
        {
            _disableMissing.Should().BeOfType<JobScheduleDisableResult.FailureNotFound>();
            _disableByIdMissing.Should().BeOfType<JobScheduleDisableResult.FailureNotFound>();
        }
    }

    [TestFixture]
    public class Given_a_disabled_schedule_with_jobs : JobScheduleTestBase
    {
        private long _scheduleId;
        private JobScheduleDisableResult _disable = null!;
        private JobScheduleDisableResult _disableAgain = null!;
        private StoredSchedule _disabled = null!;
        private List<JobRepositoryTests.StoredJob> _jobs = [];
        private long _reenabledId;

        [SetUp]
        public async Task Setup()
        {
            string scheduleType = NewScheduleType();
            _scheduleId = IdOf(
                await Repository()
                    .Upsert(
                        Command(scheduleType, runFirstOccurrenceImmediately: true),
                        CancellationToken.None
                    )
            );
            MaterializedOf(await MaterializeAsync());

            _disable = await Repository().DisableById(_scheduleId, CancellationToken.None);
            _disabled = await ReadScheduleAsync(_scheduleId);
            _disableAgain = await Repository().Disable(scheduleType, CancellationToken.None);
            _jobs = await JobsOfAsync(_scheduleId);
            _reenabledId = IdOf(await Repository().Upsert(Command(scheduleType), CancellationToken.None));
        }

        [Test]
        public void It_disable_keeps_jobs_and_id()
        {
            _disable.Should().BeOfType<JobScheduleDisableResult.Success>();
            _disabled.Enabled.Should().BeFalse();
            _jobs.Should().ContainSingle();
            _reenabledId.Should().Be(_scheduleId);
        }

        [Test]
        public void It_records_who_disabled_the_schedule()
        {
            _disabled.ModifiedBy.Should().Be("test-user");
            _disabled.LastModifiedAt.Should().NotBeNull();
        }

        [Test]
        public void It_accepts_disabling_a_disabled_schedule() =>
            _disableAgain.Should().BeOfType<JobScheduleDisableResult.Success>();
    }

    [TestFixture]
    public class Given_schedules_with_seeded_expired_leases : JobScheduleTestBase
    {
        private long _enqueuedId;
        private long _freshId;
        private DateTime _enqueuedOccurrence;
        private JobScheduleMaterializeResult _first = null!;
        private JobScheduleMaterializeResult _second = null!;
        private JobScheduleMaterializeResult _third = null!;
        private StoredSchedule _enqueued = null!;
        private List<JobRepositoryTests.StoredJob> _enqueuedJobs = [];

        [SetUp]
        public async Task Setup()
        {
            // Left by a code path that persisted a lease and its occurrence, then crashed before advancing.
            _enqueuedId = await SeedScheduleAsync(
                nextRunOffset: -20,
                leaseOwner: "crashed-dispatcher",
                leaseOffset: -5,
                fencingToken: 7
            );
            await Connection!.ExecuteAsync(
                """
                INSERT INTO dmscs.Job (
                    JobId, JobType, PayloadVersion, Payload, Status, NextAttemptAt, SourceScheduleId,
                    ScheduledOccurrence, CreatedBy)
                SELECT @JobId, s.JobType, s.PayloadVersion, s.Payload, N'Pending', s.NextRunAt, s.Id, s.NextRunAt,
                    N'system'
                FROM dmscs.JobSchedule AS s
                WHERE s.Id = @Id;
                """,
                new { JobId = NewJobId(), Id = _enqueuedId }
            );
            _enqueuedOccurrence = (await ReadScheduleAsync(_enqueuedId)).NextRunAt;
            _freshId = await SeedScheduleAsync(
                nextRunOffset: -10,
                leaseOwner: "crashed-dispatcher",
                leaseOffset: -5
            );

            _first = await MaterializeAsync();
            _second = await MaterializeAsync();
            _third = await MaterializeAsync();
            _enqueued = await ReadScheduleAsync(_enqueuedId);
            _enqueuedJobs = await JobsOfAsync(_enqueuedId);
        }

        [Test]
        public void It_recovers_seeded_expired_lease_exactly_once()
        {
            JobScheduleMaterializeResult.AlreadyEnqueued already = _first
                .Should()
                .BeOfType<JobScheduleMaterializeResult.AlreadyEnqueued>()
                .Subject;
            already.ScheduleId.Should().Be(_enqueuedId);
            already.Occurrence.Should().Be(_enqueuedOccurrence);
            _enqueuedJobs.Should().ContainSingle("the existing occurrence is not enqueued again");
        }

        [Test]
        public void It_advances_past_the_recovered_occurrence()
        {
            JobScheduleMaterializeResult.AlreadyEnqueued already =
                (JobScheduleMaterializeResult.AlreadyEnqueued)_first;
            already.NewNextRunAt.Should().Be(_enqueuedOccurrence.AddMinutes(60));
            _enqueued.NextRunAt.Should().Be(already.NewNextRunAt);
            _enqueued.LeaseOwner.Should().BeNull();
            _enqueued.FencingToken.Should().Be(8);
        }

        [Test]
        public void It_treats_an_expired_lease_as_free() =>
            MaterializedOf(_second).ScheduleId.Should().Be(_freshId);

        [Test]
        public void It_is_not_due_again() => _third.Should().BeOfType<JobScheduleMaterializeResult.NoneDue>();
    }

    [TestFixture("owner")]
    [TestFixture("token")]
    [TestFixture("lease expiry")]
    [TestFixture("disable")]
    public class Given_the_lease_changed_before_the_advance(string change) : JobScheduleTestBase
    {
        private long _scheduleId;
        private StoredSchedule _before = null!;
        private JobScheduleMaterializeResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _scheduleId = await SeedScheduleAsync(nextRunOffset: -10);
            _before = await ReadScheduleAsync(_scheduleId);

            // No other connection can change the locked row, so the change is made inside the materialization's
            // own transaction, as a defect in a future code path would.
            string set = change switch
            {
                "owner" => "LeaseOwner = N'dispatcher-b'",
                "token" => "FencingToken = FencingToken + 1",
                "lease expiry" => "LeaseExpiresAt = SYSUTCDATETIME()",
                "disable" => "Enabled = 0",
                _ => throw new InvalidOperationException(change),
            };
            _result = await MaterializeAsync(
                Repository(afterInsert: transaction =>
                    transaction.Connection!.ExecuteAsync(
                        $"UPDATE dmscs.JobSchedule SET {set} WHERE Id = @Id;",
                        new { Id = _scheduleId },
                        transaction
                    )
                )
            );
        }

        [Test]
        public void It_rejects_advance_when_owner_or_token_changed() =>
            _result.Should().BeOfType<JobScheduleMaterializeResult.OwnershipLost>(change);

        [Test]
        public async Task It_rolls_back_the_occurrence_and_the_lease()
        {
            (await JobCountAsync()).Should().Be(0);
            (await ReadScheduleAsync(_scheduleId)).Should().BeEquivalentTo(_before);
        }
    }

    [TestFixture]
    public class Given_a_materialization_paused_across_an_interval_boundary : JobScheduleTestBase
    {
        private long _scheduleId;
        private DateTime _occurrence;
        private JobScheduleMaterializeResult.Materialized _materialized = null!;

        [SetUp]
        public async Task Setup()
        {
            // The first boundary after the occurrence is 0.5 s away; the pause outlasts it.
            _scheduleId = await SeedScheduleAsync(nextRunOffset: -59.5, intervalMinutes: 1);
            _occurrence = (await ReadScheduleAsync(_scheduleId)).NextRunAt;

            _materialized = MaterializedOf(
                await MaterializeAsync(
                    Repository(afterInsert: _ => Task.Delay(TimeSpan.FromMilliseconds(1500)))
                )
            );
        }

        [Test]
        public void It_advances_from_fresh_time_when_paused_after_insert()
        {
            _materialized.DatabaseUtcNow.Should().BeAfter(_occurrence.AddMinutes(1), "the boundary passed");
            _materialized.NewNextRunAt.Should().Be(_occurrence.AddMinutes(2));
            _materialized.NewNextRunAt.Should().BeAfter(_materialized.DatabaseUtcNow);
        }

        [Test]
        public async Task It_enqueues_exactly_one_job() =>
            (await JobsOfAsync(_scheduleId))
                .Should()
                .ContainSingle()
                .Which.ScheduledOccurrence.Should()
                .Be(_occurrence);
    }

    [TestFixture(-59.9)]
    [TestFixture(-60.0)]
    [TestFixture(-60.1)]
    public class Given_fractional_second_boundaries(double nextRunOffset) : JobScheduleTestBase
    {
        private static readonly TimeSpan _interval = TimeSpan.FromMinutes(1);
        private JobScheduleMaterializeResult.Materialized _materialized = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedScheduleAsync(nextRunOffset, intervalMinutes: 1);
            _materialized = MaterializedOf(await MaterializeAsync());
        }

        [Test]
        public void It_advances_to_first_future_boundary_with_fractional_seconds()
        {
            _materialized.NewNextRunAt.Should().BeAfter(_materialized.DatabaseUtcNow);
            (_materialized.NewNextRunAt - _interval).Should().BeOnOrBefore(_materialized.DatabaseUtcNow);
            _materialized
                .NewNextRunAt.Should()
                .Be(
                    ScheduleOccurrenceMath.Advance(
                        _materialized.Occurrence,
                        _interval,
                        _materialized.DatabaseUtcNow
                    )
                );
        }
    }

    [TestFixture]
    public class Given_a_schedule_missed_for_several_intervals : JobScheduleTestBase
    {
        private long _scheduleId;
        private DateTime _occurrence;
        private JobScheduleMaterializeResult _first = null!;
        private JobScheduleMaterializeResult _second = null!;

        [SetUp]
        public async Task Setup()
        {
            _scheduleId = await SeedScheduleAsync(nextRunOffset: -(5 * 300 + 30), intervalMinutes: 5);
            _occurrence = (await ReadScheduleAsync(_scheduleId)).NextRunAt;
            _first = await MaterializeAsync();
            _second = await MaterializeAsync();
        }

        [Test]
        public async Task It_enqueues_one_job_after_multi_interval_downtime()
        {
            MaterializedOf(_first).NewNextRunAt.Should().Be(_occurrence.AddMinutes(30));
            _second.Should().BeOfType<JobScheduleMaterializeResult.NoneDue>();
            (await JobsOfAsync(_scheduleId)).Should().ContainSingle();
        }
    }

    [TestFixture]
    public class Given_a_materialization_that_outlives_its_deadline : JobScheduleTestBase
    {
        private string _abandonedJobId = "";
        private JobScheduleMaterializeResult _result = null!;
        private JobScheduleMaterializeResult _retry = null!;
        private long _abandonedJobs;

        [SetUp]
        public async Task Setup()
        {
            await SeedScheduleAsync(nextRunOffset: -10);
            _abandonedJobId = NewJobId();

            _result = await MaterializeAsync(
                Repository(
                    afterInsert: _ => Task.Delay(TimeSpan.FromMilliseconds(1500)),
                    materializationTimeout: TimeSpan.FromSeconds(1)
                ),
                _abandonedJobId
            );

            // The abandoned transaction is rolled back in the background; until then its lock is skipped.
            _retry = new JobScheduleMaterializeResult.NoneDue();
            for (int attempt = 0; attempt < 50 && _retry is JobScheduleMaterializeResult.NoneDue; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                _retry = await MaterializeAsync();
            }

            _abandonedJobs = await Connection!.ExecuteScalarAsync<long>(
                "SELECT COUNT_BIG(*) FROM dmscs.Job WHERE JobId = @JobId;",
                new { JobId = _abandonedJobId }
            );
        }

        [Test]
        public void It_fails_without_advancing() =>
            _result
                .Should()
                .BeOfType<JobScheduleMaterializeResult.FailureUnknown>()
                .Which.Diagnostic.ExceptionTypeChain.Should()
                .Be("System.TimeoutException");

        [Test]
        public void It_rolls_back_the_abandoned_occurrence()
        {
            _abandonedJobs.Should().Be(0);
            MaterializedOf(_retry).JobId.Should().NotBe(_abandonedJobId);
        }
    }

    [TestFixture]
    public class Given_an_occurrence_whose_job_id_is_taken : JobScheduleTestBase
    {
        private long _scheduleId;
        private StoredSchedule _before = null!;
        private JobScheduleMaterializeResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            string takenJobId = NewJobId();
            (await TryInsertJobAsync(jobId: takenJobId)).Should().BeNull();
            _scheduleId = await SeedScheduleAsync(nextRunOffset: -10);
            _before = await ReadScheduleAsync(_scheduleId);

            _result = await MaterializeAsync(jobId: takenJobId);
        }

        [Test]
        public void It_reports_the_unique_violation_as_unknown() =>
            _result
                .Should()
                .BeOfType<JobScheduleMaterializeResult.FailureUnknown>()
                .Which.Diagnostic.ProviderErrorCode.Should()
                .Be(UniqueConstraintViolation.ToString(CultureInfo.InvariantCulture));

        [Test]
        public async Task It_rolls_back_the_lease()
        {
            (await JobsOfAsync(_scheduleId)).Should().BeEmpty();
            (await ReadScheduleAsync(_scheduleId)).Should().BeEquivalentTo(_before);
        }
    }

    [TestFixture]
    public class Given_a_cancelled_caller : JobScheduleTestBase
    {
        private long _scheduleId;
        private StoredSchedule _before = null!;
        private JobScheduleMaterializeResult _materialize = null!;
        private JobScheduleUpsertResult _upsert = null!;

        [SetUp]
        public async Task Setup()
        {
            _scheduleId = await SeedScheduleAsync(nextRunOffset: -10);
            _before = await ReadScheduleAsync(_scheduleId);
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync();

            _materialize = await Repository()
                .MaterializeNextDue(Owner, LeaseSeconds, NewJobId(), cancelled.Token);
            _upsert = await Repository().Upsert(Command(NewScheduleType()), cancelled.Token);
        }

        [Test]
        public void It_reports_the_cancellation_as_a_failure()
        {
            _materialize.Should().BeOfType<JobScheduleMaterializeResult.FailureUnknown>();
            _upsert.Should().BeOfType<JobScheduleUpsertResult.FailureUnknown>();
        }

        [Test]
        public async Task It_writes_nothing()
        {
            (await JobCountAsync()).Should().Be(0);
            (await ReadScheduleAsync(_scheduleId)).Should().BeEquivalentTo(_before);
            (await Connection!.ExecuteScalarAsync<long>("SELECT COUNT_BIG(*) FROM dmscs.JobSchedule;"))
                .Should()
                .Be(1);
        }
    }

    [TestFixture]
    public class Given_a_database_failure : JobScheduleTestBase
    {
        private readonly Dictionary<string, JobFailureDiagnostic> _diagnostics = [];

        [SetUp]
        public async Task Setup()
        {
            _diagnostics.Clear();
            SqlConnectionStringBuilder missing = new(MssqlTestConfiguration.DatabaseConnectionString)
            {
                InitialCatalog = $"missing_{Guid.NewGuid():N}",
            };
            JobScheduleRepository repository = Repository(
                options: Options.Create(
                    new DatabaseOptions
                    {
                        DatabaseConnection = missing.ConnectionString,
                        EncryptionKey = MssqlTestConfiguration.DatabaseOptions.Value.EncryptionKey,
                    }
                )
            );

            _diagnostics["UpsertSchedule"] = (
                (JobScheduleUpsertResult.FailureUnknown)
                    await repository.Upsert(Command(NewScheduleType()), CancellationToken.None)
            ).Diagnostic;
            _diagnostics["DisableSchedule"] = (
                (JobScheduleDisableResult.FailureUnknown)
                    await repository.Disable(NewScheduleType(), CancellationToken.None)
            ).Diagnostic;
            _diagnostics["DisableScheduleById"] = (
                (JobScheduleDisableResult.FailureUnknown)
                    await repository.DisableById(1, CancellationToken.None)
            ).Diagnostic;
            _diagnostics["ListSchedulesByType"] = (
                (JobScheduleListResult.FailureUnknown)
                    await repository.ListByType(NewScheduleType(), CancellationToken.None)
            ).Diagnostic;
            _diagnostics["MaterializeNextDue"] = (
                (JobScheduleMaterializeResult.FailureUnknown)await MaterializeAsync(repository)
            ).Diagnostic;
        }

        [Test]
        public void It_reports_each_failure_with_the_type_chain_and_error_number()
        {
            foreach ((string operation, JobFailureDiagnostic diagnostic) in _diagnostics)
            {
                diagnostic
                    .Should()
                    .Be(
                        new JobFailureDiagnostic("Microsoft.Data.SqlClient.SqlException", "4060", operation),
                        operation
                    );
            }
        }
    }

    [TestFixture]
    public class Given_arguments_outside_their_bounds : JobScheduleTestBase
    {
        private readonly Dictionary<string, Exception?> _thrown = [];

        [SetUp]
        public async Task Setup()
        {
            _thrown.Clear();
            JobScheduleRepository repository = Repository();
            _thrown["schedule type"] = await ThrownByAsync(() =>
                repository.Upsert(Command("not a key"), CancellationToken.None)
            );
            _thrown["job type"] = await ThrownByAsync(() =>
                repository.Upsert(Command(NewScheduleType(), jobType: "bad type"), CancellationToken.None)
            );
            _thrown["disable type"] = await ThrownByAsync(() =>
                repository.Disable("pending ", CancellationToken.None)
            );
            _thrown["list type"] = await ThrownByAsync(() =>
                repository.ListByType("", CancellationToken.None)
            );
            _thrown["lease"] = await ThrownByAsync(() =>
                repository.MaterializeNextDue(Owner, 0, NewJobId(), CancellationToken.None)
            );
        }

        [Test]
        public void It_rejects_each_as_a_programming_error()
        {
            foreach ((string argument, Exception? thrown) in _thrown)
            {
                thrown.Should().BeAssignableTo<ArgumentException>(argument);
            }
        }

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

    [TestFixture]
    public class Given_a_next_run_whose_fraction_is_later_than_now : JobScheduleTestBase
    {
        private DateTime _wholeSecond;
        private long _boundaryCount;
        private JobScheduleMaterializeResult.Materialized _materialized = null!;

        [SetUp]
        public async Task Setup()
        {
            // The D-9 overshoot, as in the review example (next run 12:00:00.900, now 12:01:00.100). S is a whole
            // second one to two seconds ahead, and the occurrence is S + 950 ms - 60 s. Advanced at a time t in
            // (S, S + 950 ms), the true elapsed time is under one interval while DATEDIFF_BIG(second, …) counts 60
            // second boundaries, so j0 is one too high and only the IIF correction keeps the first future boundary.
            long scheduleId = await SeedScheduleAsync(nextRunOffset: -60, intervalMinutes: 1);
            DateTime occurrence = Utc(
                await Connection!.ExecuteScalarAsync<DateTime>(
                    """
                    UPDATE dmscs.JobSchedule
                    SET NextRunAt = DATEADD(millisecond, 950 - 60000,
                        DATEADD(second, CAST(DATEDIFF_BIG(second, '2000-01-01', SYSUTCDATETIME()) + 2 AS INT),
                            CAST('2000-01-01' AS DATETIME2)))
                    OUTPUT inserted.NextRunAt
                    WHERE Id = @Id;
                    """,
                    new { Id = scheduleId }
                )
            );
            _wholeSecond = occurrence.AddMilliseconds(60000 - 950);

            while (await DatabaseNowAsync() <= _wholeSecond)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }

            _materialized = MaterializedOf(await MaterializeAsync());

            DynamicParameters sample = new();
            sample.Add("Occurrence", _materialized.Occurrence, DbType.DateTime2);
            sample.Add("Now", _materialized.DatabaseUtcNow, DbType.DateTime2);
            _boundaryCount = await Connection!.ExecuteScalarAsync<long>(
                "SELECT DATEDIFF_BIG(second, @Occurrence, @Now);",
                sample
            );
        }

        [Test]
        public void It_advanced_within_the_overshoot_window()
        {
            (_materialized.DatabaseUtcNow - _wholeSecond).Should().BeLessThan(TimeSpan.FromMilliseconds(950));
            (_materialized.DatabaseUtcNow - _materialized.Occurrence)
                .Should()
                .BeLessThan(TimeSpan.FromMinutes(1));
            _boundaryCount.Should().Be(60, "DATEDIFF_BIG counted a whole interval that had not yet elapsed");
        }

        [Test]
        public void It_corrects_the_overshoot_to_the_first_future_boundary()
        {
            _materialized.NewNextRunAt.Should().Be(_materialized.Occurrence.AddMinutes(1));
            _materialized.NewNextRunAt.Should().BeAfter(_materialized.DatabaseUtcNow);
        }
    }

    [TestFixture]
    public class Given_payload_changes_the_database_collation_would_ignore : JobScheduleTestBase
    {
        private StoredSchedule _case = null!;
        private StoredSchedule _trailingSpace = null!;

        [SetUp]
        public async Task Setup()
        {
            string caseType = NewScheduleType();
            await Repository()
                .Upsert(Command(caseType, payload: """{"code":"abc"}"""), CancellationToken.None);
            long caseId = IdOf(
                await Repository()
                    .Upsert(Command(caseType, payload: """{"code":"ABC"}"""), CancellationToken.None)
            );
            _case = await ReadScheduleAsync(caseId);

            string spaceType = NewScheduleType();
            await Repository()
                .Upsert(Command(spaceType, payload: """{"code":"abc"}"""), CancellationToken.None);
            long spaceId = IdOf(
                await Repository()
                    .Upsert(Command(spaceType, payload: """{"code":"abc"} """), CancellationToken.None)
            );
            _trailingSpace = await ReadScheduleAsync(spaceId);
        }

        [Test]
        public void It_applies_a_change_of_case()
        {
            _case.Payload.Should().Be("""{"code":"ABC"}""");
            _case.LastModifiedAt.Should().NotBeNull();
        }

        [Test]
        public void It_applies_a_change_of_trailing_space()
        {
            _trailingSpace.Payload.Should().Be("""{"code":"abc"} """);
            _trailingSpace.LastModifiedAt.Should().NotBeNull();
        }
    }

    [TestFixture]
    public class Given_a_materialization_commit_that_outlives_its_deadline : JobScheduleTestBase
    {
        private long _scheduleId;
        private StoredSchedule _before = null!;
        private string _abandonedJobId = "";
        private JobScheduleMaterializeResult _result = null!;
        private TimeSpan _elapsed;
        private JobScheduleMaterializeResult _retry = null!;
        private long _abandonedJobs;

        [SetUp]
        public async Task Setup()
        {
            _scheduleId = await SeedScheduleAsync(nextRunOffset: -10);
            _before = await ReadScheduleAsync(_scheduleId);
            _abandonedJobId = NewJobId();

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            _result = await MaterializeAsync(
                Repository(
                    materializationTimeout: TimeSpan.FromSeconds(1),
                    commitStatement: "WAITFOR DELAY '00:00:05'; COMMIT TRANSACTION;"
                ),
                _abandonedJobId
            );
            _elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);

            // The abandoned commit is cancelled and its connection closed in the background, which rolls back the
            // API transaction; until then the locked schedule is skipped.
            _retry = new JobScheduleMaterializeResult.NoneDue();
            for (int attempt = 0; attempt < 100 && _retry is JobScheduleMaterializeResult.NoneDue; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                _retry = await MaterializeAsync();
            }

            _abandonedJobs = await Connection!.ExecuteScalarAsync<long>(
                "SELECT COUNT_BIG(*) FROM dmscs.Job WHERE JobId = @JobId;",
                new { JobId = _abandonedJobId }
            );
        }

        [Test]
        public void It_stops_waiting_for_the_commit_at_the_deadline()
        {
            _result.Should().BeOfType<JobScheduleMaterializeResult.FailureUnknown>();
            _elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        }

        [Test]
        public void It_rolls_back_the_abandoned_occurrence_and_lease()
        {
            _abandonedJobs.Should().Be(0);
            JobScheduleMaterializeResult.Materialized retry = MaterializedOf(_retry);
            retry.Occurrence.Should().Be(_before.NextRunAt, "the abandoned transaction did not advance it");
        }
    }

    [TestFixture]
    public class Given_two_upserts_that_both_read_a_new_key_first : JobScheduleTestBase
    {
        private const string HoldAfterRead = "WAITFOR DELAY '00:00:01';";
        private string _scheduleType = "";
        private JobScheduleUpsertResult[] _results = [];
        private List<StoredSchedule> _rows = [];

        [SetUp]
        public async Task Setup()
        {
            _scheduleType = NewScheduleType();

            // Each upsert holds for a second between its key read and its write. The second reads while the first
            // is held: its key-range lock must make the second wait, rather than let both insert the new key.
            Task<JobScheduleUpsertResult> first = Repository(afterUpsertKeyRead: HoldAfterRead)
                .Upsert(Command(_scheduleType, intervalMinutes: 60), CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            Task<JobScheduleUpsertResult> second = Repository(afterUpsertKeyRead: HoldAfterRead)
                .Upsert(Command(_scheduleType, intervalMinutes: 30), CancellationToken.None);
            _results = await Task.WhenAll(first, second);

            _rows =
            [
                .. await Connection!.QueryAsync<StoredSchedule>(
                    "SELECT * FROM dmscs.JobSchedule WHERE ScheduleType = @ScheduleType;",
                    new { ScheduleType = _scheduleType }
                ),
            ];
        }

        [Test]
        public void It_serializes_them_to_one_schedule()
        {
            IdOf(_results[1]).Should().Be(IdOf(_results[0]));
            _rows.Should().ContainSingle();
        }

        [Test]
        public void It_applies_the_second_after_the_first() => _rows.Single().IntervalMinutes.Should().Be(30);
    }
}
