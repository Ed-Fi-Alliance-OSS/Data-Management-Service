// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.Job;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;

public class JobRepositoryTests
{
    private const string CommandJobType = "DataStore.RefreshEducationOrganizations";
    private const string CommandPayload = """{"dataStoreId":7}""";

    private static readonly JobEnqueueCommand _command = new(CommandJobType, 2, CommandPayload);

    /// <summary>A stored <c>dmscs.Job</c> row, read on the fixture's own connection.</summary>
    public sealed class StoredJob
    {
        public string JobId { get; set; } = "";
        public long? TenantId { get; set; }
        public string JobType { get; set; } = "";
        public short PayloadVersion { get; set; }
        public string Payload { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime? NextAttemptAt { get; set; }
        public int AttemptCount { get; set; }
        public long FencingToken { get; set; }
        public string? CreatedBy { get; set; }
        public long? SourceScheduleId { get; set; }
        public DateTime? ScheduledOccurrence { get; set; }
        public DateTime? FinishedAt { get; set; }
        public DateTime? LeaseExpiresAt { get; set; }
        public string? LeaseOwner { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public abstract class JobRepositoryTestBase : JobSchemaTestBase
    {
        protected static JobRepository Repository(TenantContext? tenant = null) =>
            Repository(MssqlTestConfiguration.DatabaseOptions, tenant);

        protected static JobRepository Repository(IOptions<DatabaseOptions> options, TenantContext? tenant) =>
            new(
                options,
                new TestAuditContext(),
                new TenantContextProvider { Context = tenant ?? new TenantContext.NotMultitenant() }
            );

        protected static MssqlCmsTransactionFactory TransactionFactory() =>
            new(MssqlTestConfiguration.DatabaseOptions);

        protected static string JobIdOf(JobEnqueueResult result) =>
            result.Should().BeOfType<JobEnqueueResult.Success>().Subject.JobId;

        /// <summary>
        /// Reads a stored job. With <paramref name="skipLockedRows"/>, a row still locked by an open transaction
        /// is skipped instead of blocking the read, as it would under locking read committed.
        /// </summary>
        protected async Task<StoredJob?> StoredJobAsync(string jobId, bool skipLockedRows = false) =>
            await Connection!.QuerySingleOrDefaultAsync<StoredJob>(
                $"""
                SELECT JobId, TenantId, JobType, PayloadVersion, Payload, Status, CreatedAt, NextAttemptAt,
                       AttemptCount, FencingToken, CreatedBy, SourceScheduleId, ScheduledOccurrence, FinishedAt,
                       LeaseExpiresAt, LeaseOwner, ErrorMessage
                FROM dmscs.Job {(skipLockedRows ? "WITH (READPAST)" : "")}
                WHERE JobId = @JobId;
                """,
                new { JobId = jobId }
            );

        protected async Task<long> JobCountAsync() =>
            await Connection!.ExecuteScalarAsync<long>("SELECT COUNT_BIG(*) FROM dmscs.Job;");
    }

    [TestFixture]
    public class Given_a_job_enqueued_inside_a_caller_transaction_that_commits : JobRepositoryTestBase
    {
        private JobEnqueueResult _result = null!;
        private StoredJob? _beforeCommit;
        private StoredJob? _afterCommit;

        [SetUp]
        public async Task Setup()
        {
            await using ICmsTransaction transaction = await TransactionFactory()
                .BeginAsync(CancellationToken.None);

            _result = await Repository()
                .EnqueueJob(_command, transaction.Transaction, CancellationToken.None);
            _beforeCommit = await StoredJobAsync(JobIdOf(_result), skipLockedRows: true);

            await transaction.CommitAsync(CancellationToken.None);
            _afterCommit = await StoredJobAsync(JobIdOf(_result));
        }

        [Test]
        public void It_returns_a_new_32_character_hex_job_id() =>
            JobIdOf(_result).Should().MatchRegex("^[0-9a-f]{32}\\z");

        [Test]
        public void It_is_not_visible_to_another_connection_before_the_caller_commits() =>
            _beforeCommit.Should().BeNull();

        [Test]
        public void It_is_visible_after_the_caller_commits() => _afterCommit.Should().NotBeNull();

        [Test]
        public void It_stores_a_pending_manual_job_with_the_command_values() =>
            _afterCommit
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        JobId = JobIdOf(_result),
                        TenantId = (long?)null,
                        JobType = CommandJobType,
                        PayloadVersion = (short)2,
                        Payload = CommandPayload,
                        Status = JobStatuses.Pending,
                        AttemptCount = 0,
                        FencingToken = 0L,
                        CreatedBy = "test-user",
                        SourceScheduleId = (long?)null,
                        ScheduledOccurrence = (DateTime?)null,
                        FinishedAt = (DateTime?)null,
                        LeaseExpiresAt = (DateTime?)null,
                        LeaseOwner = (string?)null,
                        ErrorMessage = (string?)null,
                    }
                );

        [Test]
        public void It_sets_NextAttemptAt_to_CreatedAt_from_one_time_sample() =>
            _afterCommit!.NextAttemptAt.Should().Be(_afterCommit.CreatedAt);

        [Test]
        public void It_stores_CreatedAt_in_UTC() =>
            _afterCommit!.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
    }

    [TestFixture]
    public class Given_a_job_enqueued_inside_a_caller_transaction_that_rolls_back : JobRepositoryTestBase
    {
        private JobEnqueueResult _result = null!;
        private long _jobCount;

        [SetUp]
        public async Task Setup()
        {
            await using ICmsTransaction transaction = await TransactionFactory()
                .BeginAsync(CancellationToken.None);
            _result = await Repository()
                .EnqueueJob(_command, transaction.Transaction, CancellationToken.None);
            await transaction.RollbackAsync(CancellationToken.None);

            _jobCount = await JobCountAsync();
        }

        [Test]
        public void It_reports_the_insert_as_done_inside_the_transaction() =>
            _result.Should().BeOfType<JobEnqueueResult.Success>();

        [Test]
        public void It_leaves_no_job() => _jobCount.Should().Be(0);
    }

    [TestFixture]
    public class Given_a_job_enqueued_inside_a_caller_transaction_disposed_without_commit
        : JobRepositoryTestBase
    {
        private JobEnqueueResult _result = null!;
        private long _jobCount;

        [SetUp]
        public async Task Setup()
        {
            await using (
                ICmsTransaction transaction = await TransactionFactory().BeginAsync(CancellationToken.None)
            )
            {
                _result = await Repository()
                    .EnqueueJob(_command, transaction.Transaction, CancellationToken.None);
            }

            _jobCount = await JobCountAsync();
        }

        [Test]
        public void It_reports_the_insert_as_done_inside_the_transaction() =>
            _result.Should().BeOfType<JobEnqueueResult.Success>();

        [Test]
        public void It_leaves_no_job() => _jobCount.Should().Be(0);
    }

    [TestFixture]
    public class Given_a_job_enqueued_without_a_transaction : JobRepositoryTestBase
    {
        private JobEnqueueResult _result = null!;
        private StoredJob? _stored;

        [SetUp]
        public async Task Setup()
        {
            _result = await Repository().EnqueueJob(_command, null, CancellationToken.None);
            _stored = await StoredJobAsync(JobIdOf(_result));
        }

        [Test]
        public void It_commits_the_job_on_its_own_connection() =>
            _stored.Should().BeEquivalentTo(new { Status = JobStatuses.Pending, Payload = CommandPayload });

        [Test]
        public void It_sets_NextAttemptAt_to_CreatedAt() =>
            _stored!.NextAttemptAt.Should().Be(_stored.CreatedAt);
    }

    [TestFixture]
    public class Given_caller_transactions_that_have_already_completed : JobRepositoryTestBase
    {
        private readonly Dictionary<string, JobEnqueueResult> _results = [];
        private long _jobCount;

        [SetUp]
        public async Task Setup()
        {
            await using (
                ICmsTransaction transaction = await TransactionFactory().BeginAsync(CancellationToken.None)
            )
            {
                await transaction.CommitAsync(CancellationToken.None);
                _results["committed"] = await Repository()
                    .EnqueueJob(_command, transaction.Transaction, CancellationToken.None);
            }

            await using (
                ICmsTransaction transaction = await TransactionFactory().BeginAsync(CancellationToken.None)
            )
            {
                await transaction.RollbackAsync(CancellationToken.None);
                _results["rolled back"] = await Repository()
                    .EnqueueJob(_command, transaction.Transaction, CancellationToken.None);
            }

            ICmsTransaction disposed = await TransactionFactory().BeginAsync(CancellationToken.None);
            await disposed.DisposeAsync();
            _results["disposed"] = await Repository()
                .EnqueueJob(_command, disposed.Transaction, CancellationToken.None);

            _jobCount = await JobCountAsync();
        }

        [Test]
        public void It_fails_instead_of_writing_outside_the_callers_transaction()
        {
            foreach ((string ending, JobEnqueueResult result) in _results)
            {
                result
                    .Should()
                    .BeOfType<JobEnqueueResult.FailureUnknown>(ending)
                    .Which.Diagnostic.Should()
                    .Be(
                        new JobFailureDiagnostic("System.InvalidOperationException", null, "EnqueueJob"),
                        ending
                    );
            }
        }

        [Test]
        public void It_stores_no_job() => _jobCount.Should().Be(0);
    }

    [TestFixture]
    public class Given_jobs_enqueued_for_two_tenants_and_without_a_tenant : JobRepositoryTestBase
    {
        private long _tenantAId;
        private readonly Dictionary<string, JobStatusQueryResult> _reads = [];
        private StoredJob? _tenantAJob;
        private StoredJob? _singleTenantJob;

        [SetUp]
        public async Task Setup()
        {
            _tenantAId = await CreateTenantAsync();
            long tenantBId = await CreateTenantAsync();
            TenantContext tenantA = new TenantContext.Multitenant(_tenantAId, "tenant-a");
            TenantContext tenantB = new TenantContext.Multitenant(tenantBId, "tenant-b");

            string tenantAJobId = JobIdOf(
                await Repository(tenantA).EnqueueJob(_command, null, CancellationToken.None)
            );
            string singleTenantJobId = JobIdOf(
                await Repository().EnqueueJob(_command, null, CancellationToken.None)
            );

            _reads["A reads A"] = await Repository(tenantA)
                .GetJobStatus(tenantAJobId, CancellationToken.None);
            _reads["B reads A"] = await Repository(tenantB)
                .GetJobStatus(tenantAJobId, CancellationToken.None);
            _reads["no tenant reads A"] = await Repository()
                .GetJobStatus(tenantAJobId, CancellationToken.None);
            _reads["A reads no tenant"] = await Repository(tenantA)
                .GetJobStatus(singleTenantJobId, CancellationToken.None);
            _reads["no tenant reads no tenant"] = await Repository()
                .GetJobStatus(singleTenantJobId, CancellationToken.None);

            _tenantAJob = await StoredJobAsync(tenantAJobId);
            _singleTenantJob = await StoredJobAsync(singleTenantJobId);
        }

        [Test]
        public void It_stores_the_tenant_from_the_tenant_context()
        {
            _tenantAJob!.TenantId.Should().Be(_tenantAId);
            _singleTenantJob!.TenantId.Should().BeNull();
        }

        [Test]
        public void It_returns_a_job_to_its_own_tenant()
        {
            _reads["A reads A"].Should().BeOfType<JobStatusQueryResult.Success>();
            _reads["no tenant reads no tenant"].Should().BeOfType<JobStatusQueryResult.Success>();
        }

        [Test]
        public void It_reports_another_tenants_job_as_not_found()
        {
            _reads["B reads A"].Should().BeOfType<JobStatusQueryResult.FailureNotFound>();
            _reads["no tenant reads A"].Should().BeOfType<JobStatusQueryResult.FailureNotFound>();
            _reads["A reads no tenant"].Should().BeOfType<JobStatusQueryResult.FailureNotFound>();
        }
    }

    [TestFixture]
    public class Given_job_ids_that_differ_from_a_stored_one : JobRepositoryTestBase
    {
        // A fixed identifier containing letters, so its upper-case variant always differs from it.
        private readonly string _jobId = "0f3a9c1e7b5d4e6f8a0b1c2d3e4f5a6b";
        private StoredJob? _stored;
        private JobStatusQueryResult _exact = null!;
        private readonly Dictionary<string, JobStatusQueryResult> _variants = [];

        [SetUp]
        public async Task Setup()
        {
            (await TryInsertJobAsync(jobId: _jobId)).Should().BeNull();
            _stored = await StoredJobAsync(_jobId);
            _exact = await Repository().GetJobStatus(_jobId, CancellationToken.None);

            foreach (
                (string name, string variant) in new[]
                {
                    ("upper case", _jobId.ToUpperInvariant()),
                    ("trailing space", _jobId + " "),
                    ("two trailing spaces", _jobId + "  "),
                    ("leading space", " " + _jobId),
                    ("prefix", _jobId[..^1]),
                    ("LIKE pattern", _jobId[..^1] + "%"),
                    ("empty", ""),
                }
            )
            {
                _variants[name] = await Repository().GetJobStatus(variant, CancellationToken.None);
            }
        }

        [Test]
        public void It_returns_the_five_public_fields_for_an_exact_match() =>
            _exact
                .Should()
                .BeOfType<JobStatusQueryResult.Success>()
                .Which.Job.Should()
                .BeEquivalentTo(
                    new JobStatusResponse
                    {
                        JobId = _jobId,
                        Status = JobStatuses.Pending,
                        CreatedAt = DateTime.SpecifyKind(_stored!.CreatedAt, DateTimeKind.Utc),
                        FinishedAt = null,
                        ErrorMessage = null,
                    }
                );

        [Test]
        public void It_returns_CreatedAt_as_UTC() =>
            ((JobStatusQueryResult.Success)_exact).Job.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);

        [Test]
        public void It_reports_every_inexact_identifier_as_not_found()
        {
            foreach ((string name, JobStatusQueryResult result) in _variants)
            {
                result.Should().BeOfType<JobStatusQueryResult.FailureNotFound>(name);
            }
        }
    }

    [TestFixture]
    public class Given_a_finished_job : JobRepositoryTestBase
    {
        private JobStatusQueryResult _read = null!;

        [SetUp]
        public async Task Setup()
        {
            string jobId = JobIdOf(await Repository().EnqueueJob(_command, null, CancellationToken.None));
            await Connection!.ExecuteAsync(
                """
                UPDATE dmscs.Job
                SET Status = N'Error', FinishedAt = '2026-09-23T10:15:30', NextAttemptAt = NULL,
                    ErrorMessage = N'The job failed.'
                WHERE JobId = @JobId;
                """,
                new { JobId = jobId }
            );
            _read = await Repository().GetJobStatus(jobId, CancellationToken.None);
        }

        [Test]
        public void It_returns_the_finish_time_as_UTC_and_the_stored_error_message() =>
            _read
                .Should()
                .BeOfType<JobStatusQueryResult.Success>()
                .Which.Job.Should()
                .BeEquivalentTo(
                    new
                    {
                        Status = JobStatuses.Error,
                        FinishedAt = new DateTime(2026, 9, 23, 10, 15, 30, DateTimeKind.Utc),
                        ErrorMessage = "The job failed.",
                    }
                );

        [Test]
        public void It_marks_the_finish_time_as_UTC() =>
            ((JobStatusQueryResult.Success)_read).Job.FinishedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [TestFixture]
    public class Given_database_errors : JobRepositoryTestBase
    {
        private const string PayloadLiteral = "payload-literal-5c1e";

        private JobEnqueueResult _tooLongType = null!;
        private JobEnqueueResult _arrayPayload = null!;
        private JobStatusQueryResult _missingDatabase = null!;

        [SetUp]
        public async Task Setup()
        {
            _tooLongType = await Repository()
                .EnqueueJob(_command with { JobType = new string('a', 101) }, null, CancellationToken.None);
            _arrayPayload = await Repository()
                .EnqueueJob(
                    _command with
                    {
                        PayloadJson = $"""["{PayloadLiteral}"]""",
                    },
                    null,
                    CancellationToken.None
                );

            SqlConnectionStringBuilder missing = new(MssqlTestConfiguration.DatabaseConnectionString)
            {
                InitialCatalog = $"missing_{Guid.NewGuid():N}",
            };
            _missingDatabase = await Repository(
                    Options.Create(
                        new DatabaseOptions
                        {
                            DatabaseConnection = missing.ConnectionString,
                            EncryptionKey = MssqlTestConfiguration.DatabaseOptions.Value.EncryptionKey,
                        }
                    ),
                    null
                )
                .GetJobStatus("any", CancellationToken.None);
        }

        [Test]
        public void It_reports_an_enqueue_failure_with_the_type_chain_and_error_number() =>
            _tooLongType
                .Should()
                .BeOfType<JobEnqueueResult.FailureUnknown>()
                .Which.Diagnostic.Should()
                .Be(new JobFailureDiagnostic("Microsoft.Data.SqlClient.SqlException", "2628", "EnqueueJob"));

        [Test]
        public void It_reports_a_rejected_payload_by_error_number_without_its_content()
        {
            JobFailureDiagnostic diagnostic = _arrayPayload
                .Should()
                .BeOfType<JobEnqueueResult.FailureUnknown>()
                .Subject.Diagnostic;
            diagnostic
                .Should()
                .Be(new JobFailureDiagnostic("Microsoft.Data.SqlClient.SqlException", "547", "EnqueueJob"));
            diagnostic.ToString().Should().NotContain(PayloadLiteral);
        }

        [Test]
        public void It_reports_a_read_failure_with_the_type_chain_and_error_number() =>
            _missingDatabase
                .Should()
                .BeOfType<JobStatusQueryResult.FailureUnknown>()
                .Which.Diagnostic.Should()
                .Be(
                    new JobFailureDiagnostic("Microsoft.Data.SqlClient.SqlException", "4060", "GetJobStatus")
                );
    }

    [TestFixture]
    public class Given_a_cancelled_token : JobRepositoryTestBase
    {
        private Exception? _enqueueFailure;
        private Exception? _readFailure;
        private long _jobCount;

        [SetUp]
        public async Task Setup()
        {
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync();

            try
            {
                await Repository().EnqueueJob(_command, null, cancelled.Token);
            }
            catch (Exception exception)
            {
                _enqueueFailure = exception;
            }

            try
            {
                await Repository().GetJobStatus("any", cancelled.Token);
            }
            catch (Exception exception)
            {
                _readFailure = exception;
            }

            _jobCount = await JobCountAsync();
        }

        [Test]
        public void It_rethrows_the_callers_cancellation_instead_of_reporting_a_failure()
        {
            _enqueueFailure.Should().BeAssignableTo<OperationCanceledException>();
            _readFailure.Should().BeAssignableTo<OperationCanceledException>();
        }

        [Test]
        public void It_stores_no_job() => _jobCount.Should().Be(0);
    }
}
