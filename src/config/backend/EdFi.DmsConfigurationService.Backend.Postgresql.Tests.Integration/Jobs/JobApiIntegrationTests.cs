// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;

/// <summary>
/// <c>GET /v3/jobs/{jobId}</c> through the whole CMS pipeline against a real database (spec step 4.2, §2 rows 2 and
/// 3): jobs are enqueued through <see cref="IJobEnqueuer"/> in the host's own services and driven through the real
/// lease repository, and the hosted job services stay off.
/// </summary>
public class JobApiIntegrationTests
{
    private const string JobType = "Api.Probe";
    private const string LeaseOwner = "api-probe";

    private static readonly JobEnqueueCommand _command = new(JobType, 1, """{"dataStoreId":7}""");
    private static readonly JobErrorCode _failure = new("Api.ProbeFailed", "The probe job failed.");

    public sealed record ApiPayload(long DataStoreId);

    public sealed class ApiValidator : IJobPayloadValidator<ApiPayload>
    {
        public IReadOnlyList<string> Validate(ApiPayload payload) => [];
    }

    public sealed class ApiHandler : IJobHandler<ApiPayload>
    {
        public Task ExecuteAsync(
            JobExecutionContext context,
            ApiPayload payload,
            CancellationToken cancellationToken
        ) => Task.CompletedTask;
    }

    private const string RangeCheckedJobType = "Api.RangeChecked";

    /// <summary>A contract-valid payload whose constructor rejects an identifier out of range.</summary>
    public sealed class RangeCheckedPayload
    {
        public RangeCheckedPayload(long dataStoreId)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dataStoreId);
            DataStoreId = dataStoreId;
        }

        public long DataStoreId { get; }
    }

    public sealed class RangeCheckedValidator : IJobPayloadValidator<RangeCheckedPayload>
    {
        public IReadOnlyList<string> Validate(RangeCheckedPayload payload) => [];
    }

    /// <summary>How many times the range-checked handler ran.</summary>
    public sealed class HandlerInvocations
    {
        public int Count;
    }

    public sealed class RangeCheckedHandler(HandlerInvocations invocations) : IJobHandler<RangeCheckedPayload>
    {
        public Task ExecuteAsync(
            JobExecutionContext context,
            RangeCheckedPayload payload,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref invocations.Count);
            return Task.CompletedTask;
        }
    }

    /// <summary>A job's stored times, read on the fixture's own connection.</summary>
    public sealed class StoredTimes
    {
        public DateTime CreatedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
    }

    /// <summary>A status read: the response and its body.</summary>
    public sealed record StatusRead(HttpStatusCode StatusCode, string? MediaType, string Content)
    {
        public JsonObject Body => JsonNode.Parse(Content)!.AsObject();
    }

    public abstract class JobApiTestBase : JobSchemaTestBase
    {
        private WebApplicationFactory<Program>? _factory;

        [TearDown]
        public async Task StopHost()
        {
            if (_factory is not null)
            {
                await _factory.DisposeAsync();
                _factory = null;
            }
        }

        /// <summary>Starts the CMS host on this fixture's database, with the probe job type registered.</summary>
        protected void StartHost(bool multiTenancy = false)
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("AppSettings:Datastore", "postgresql");
                builder.UseSetting("AppSettings:MultiTenancy", multiTenancy.ToString());
                builder.UseSetting(
                    "DatabaseSettings:DatabaseConnection",
                    Configuration.DatabaseOptions.Value.DatabaseConnection
                );
                builder.ConfigureServices(services =>
                {
                    services.AddTestAuthentication();
                    services.AddJobHandler<ApiHandler, ApiPayload, ApiValidator>(JobType, 1);
                    services.AddSingleton<HandlerInvocations>();
                    services.AddJobHandler<RangeCheckedHandler, RangeCheckedPayload, RangeCheckedValidator>(
                        RangeCheckedJobType,
                        1
                    );
                });
            });
            _factory.CreateClient().Dispose();
        }

        protected IServiceProvider Services => _factory!.Services;

        /// <summary>Enqueues through the host's enqueuer inside a caller transaction, then commits or rolls back.</summary>
        protected async Task<string> EnqueueAsync(TenantContext? tenant = null, bool commit = true)
        {
            using IServiceScope scope = Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextProvider>().Context =
                tenant ?? new TenantContext.NotMultitenant();

            await using ICmsTransaction transaction = await scope
                .ServiceProvider.GetRequiredService<ICmsTransactionFactory>()
                .BeginAsync(CancellationToken.None);
            JobEnqueueResult result = await scope
                .ServiceProvider.GetRequiredService<IJobEnqueuer>()
                .EnqueueAsync(_command, transaction.Transaction, CancellationToken.None);
            string jobId = result.Should().BeOfType<JobEnqueueResult.Success>().Subject.JobId;

            if (commit)
            {
                await transaction.CommitAsync(CancellationToken.None);
            }
            else
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            return jobId;
        }

        protected async Task<ClaimedJob> ClaimAsync()
        {
            JobClaimResult claim = await Services
                .GetRequiredService<IJobLeaseRepository>()
                .ClaimNext(LeaseOwner, 300, 5, CancellationToken.None);
            return claim.Should().BeOfType<JobClaimResult.Claimed>().Subject.Job;
        }

        protected async Task WriteOutcomeAsync(
            Func<IJobLeaseRepository, ClaimedJob, Task<JobWriteResult>> write
        )
        {
            ClaimedJob job = await ClaimAsync();
            JobWriteResult result = await write(Services.GetRequiredService<IJobLeaseRepository>(), job);
            result.Should().BeOfType<JobWriteResult.Success>();
        }

        protected async Task<StatusRead> GetStatusAsync(
            string jobId,
            string? scope = null,
            string? tenantName = null
        )
        {
            using HttpClient client = _factory!.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Scope", scope ?? AuthorizationScopes.AdminScope.Name);
            if (tenantName is not null)
            {
                client.DefaultRequestHeaders.Add("Tenant", tenantName);
            }

            using HttpResponseMessage response = await client.GetAsync(
                $"/v3/jobs/{Uri.EscapeDataString(jobId)}"
            );
            return new StatusRead(
                response.StatusCode,
                response.Content.Headers.ContentType?.MediaType,
                await response.Content.ReadAsStringAsync()
            );
        }

        protected async Task<(long Id, string Name)> CreateNamedTenantAsync()
        {
            long id = await CreateTenantAsync();
            string? name = await Connection!.ExecuteScalarAsync<string>(
                """SELECT "Name" FROM "dmscs"."Tenant" WHERE "Id" = @Id;""",
                new { Id = id }
            );
            return (id, name!);
        }

        protected async Task<StoredTimes> StoredTimesAsync(string jobId) =>
            await Connection!.QuerySingleAsync<StoredTimes>(
                """SELECT "CreatedAt", "FinishedAt" FROM "dmscs"."Job" WHERE "JobId" = @JobId;""",
                new { JobId = jobId }
            );

        /// <summary>A timestamp from the body: a UTC <c>Z</c> string that parses to the stored value.</summary>
        protected static void ShouldBeStoredUtc(JsonNode? value, DateTime stored)
        {
            string text = value!.GetValue<string>();
            text.Should().EndWith("Z");
            DateTime
                .Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                .Should()
                .Be(DateTime.SpecifyKind(stored, DateTimeKind.Utc));
        }
    }

    [TestFixture]
    public class Given_a_job_enqueued_in_a_committed_caller_transaction : JobApiTestBase
    {
        private string _jobId = "";
        private StatusRead _read = null!;
        private StoredTimes _stored = null!;

        [SetUp]
        public async Task Setup()
        {
            StartHost();
            _jobId = await EnqueueAsync();
            _read = await GetStatusAsync(_jobId);
            _stored = await StoredTimesAsync(_jobId);
        }

        [Test]
        public void It_returns_200_immediately_after_enqueue_commit() =>
            _read.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_returns_json() => _read.MediaType.Should().Be("application/json");

        [Test]
        public void It_returns_exactly_the_five_contract_properties() =>
            _read
                .Body.Select(property => property.Key)
                .Should()
                .BeEquivalentTo("jobId", "status", "createdAt", "finishedAt", "errorMessage");

        [Test]
        public void It_returns_the_pending_job()
        {
            _read.Body["jobId"]!.GetValue<string>().Should().Be(_jobId);
            _read.Body["status"]!.GetValue<string>().Should().Be("Pending");
        }

        [Test]
        public void It_returns_the_stored_creation_time_in_utc() =>
            ShouldBeStoredUtc(_read.Body["createdAt"], _stored.CreatedAt);

        [Test]
        public void It_returns_null_finish_time_and_error_message()
        {
            _read.Content.Should().Contain("\"finishedAt\":null");
            _read.Content.Should().Contain("\"errorMessage\":null");
        }
    }

    [TestFixture]
    public class Given_a_job_enqueued_in_a_rolled_back_caller_transaction : JobApiTestBase
    {
        private StatusRead _read = null!;

        [SetUp]
        public async Task Setup()
        {
            StartHost();
            string jobId = await EnqueueAsync(commit: false);
            _read = await GetStatusAsync(jobId);
        }

        [Test]
        public void It_returns_404() => _read.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestFixture]
    public class Given_an_in_progress_job : JobApiTestBase
    {
        private StatusRead _read = null!;

        [SetUp]
        public async Task Setup()
        {
            StartHost();
            string jobId = await EnqueueAsync();
            await ClaimAsync();
            _read = await GetStatusAsync(jobId);
        }

        [Test]
        public void It_returns_in_progress() =>
            _read.Body["status"]!.GetValue<string>().Should().Be("InProgress");

        [Test]
        public void It_returns_null_finish_time_and_error_message()
        {
            _read.Content.Should().Contain("\"finishedAt\":null");
            _read.Content.Should().Contain("\"errorMessage\":null");
        }
    }

    [TestFixture]
    public class Given_a_completed_job : JobApiTestBase
    {
        private StatusRead _read = null!;
        private StoredTimes _stored = null!;

        [SetUp]
        public async Task Setup()
        {
            StartHost();
            string jobId = await EnqueueAsync();
            await WriteOutcomeAsync(
                (leases, job) =>
                    leases.Complete(job.Id, job.LeaseOwner, job.FencingToken, CancellationToken.None)
            );
            _read = await GetStatusAsync(jobId);
            _stored = await StoredTimesAsync(jobId);
        }

        [Test]
        public void It_returns_completed() =>
            _read.Body["status"]!.GetValue<string>().Should().Be("Completed");

        [Test]
        public void It_returns_the_stored_finish_time_in_utc() =>
            ShouldBeStoredUtc(_read.Body["finishedAt"], _stored.FinishedAt!.Value);

        [Test]
        public void It_returns_a_null_error_message() =>
            _read.Content.Should().Contain("\"errorMessage\":null");
    }

    [TestFixture]
    public class Given_a_failed_job : JobApiTestBase
    {
        private StatusRead _read = null!;
        private StoredTimes _stored = null!;

        [SetUp]
        public async Task Setup()
        {
            StartHost();
            string jobId = await EnqueueAsync();
            await WriteOutcomeAsync(
                (leases, job) =>
                    leases.FailTerminal(
                        job.Id,
                        job.LeaseOwner,
                        job.FencingToken,
                        _failure,
                        CancellationToken.None
                    )
            );
            _read = await GetStatusAsync(jobId);
            _stored = await StoredTimesAsync(jobId);
        }

        [Test]
        public void It_returns_error() => _read.Body["status"]!.GetValue<string>().Should().Be("Error");

        [Test]
        public void It_returns_the_stored_finish_time_in_utc() =>
            ShouldBeStoredUtc(_read.Body["finishedAt"], _stored.FinishedAt!.Value);

        [Test]
        public void It_returns_the_registered_error_message() =>
            _read.Body["errorMessage"]!.GetValue<string>().Should().Be(_failure.Message);
    }

    [TestFixture]
    public class Given_a_stored_payload_its_constructor_rejects : JobApiTestBase
    {
        private JobExecutionResult _result = null!;
        private StatusRead _read = null!;

        [SetUp]
        public async Task Setup()
        {
            StartHost();
            string jobId = Guid.NewGuid().ToString("N");
            await Connection!.ExecuteAsync(
                """
                INSERT INTO "dmscs"."Job" ("JobId", "JobType", "PayloadVersion", "Payload", "Status", "NextAttemptAt")
                VALUES (@JobId, @JobType, 1, '{"dataStoreId":-1}', 'Pending', (now() AT TIME ZONE 'UTC'));
                """,
                new { JobId = jobId, JobType = RangeCheckedJobType }
            );
            ClaimedJob job = await ClaimAsync();
            _result = await Services
                .GetRequiredService<JobExecutor>()
                .ExecuteAsync(job, CancellationToken.None);
            _read = await GetStatusAsync(jobId);
        }

        [Test]
        public void It_fails_the_job_terminally_as_an_invalid_payload() =>
            _result
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.Failed, ErrorCode: "InvalidPayload"));

        [Test]
        public void It_never_invokes_the_handler() =>
            Services.GetRequiredService<HandlerInvocations>().Count.Should().Be(0);

        [Test]
        public void It_polls_as_error_with_the_registered_message()
        {
            _read.Body["status"]!.GetValue<string>().Should().Be("Error");
            _read.Body["finishedAt"]!.GetValue<string>().Should().EndWith("Z");
            _read.Body["errorMessage"]!.GetValue<string>().Should().Be(JobErrorCode.InvalidPayload.Message);
        }

        [Test]
        public void It_exposes_nothing_of_the_constructor_exception()
        {
            _read.Content.Should().NotContain("ArgumentOutOfRange");
            _read.Content.Should().NotContain("non-zero");
            _read.Content.Should().NotContain("dataStoreId");
        }
    }

    [TestFixture]
    public class Given_a_job_read_with_the_read_only_scope : JobApiTestBase
    {
        private string _jobId = "";
        private StatusRead _read = null!;

        [SetUp]
        public async Task Setup()
        {
            StartHost();
            _jobId = await EnqueueAsync();
            _read = await GetStatusAsync(_jobId, AuthorizationScopes.ReadOnlyScope.Name);
        }

        [Test]
        public void It_returns_200() => _read.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_returns_the_job() => _read.Body["jobId"]!.GetValue<string>().Should().Be(_jobId);
    }

    [TestFixture]
    public class Given_identifier_variants_of_an_existing_job : JobApiTestBase
    {
        private readonly Dictionary<string, HttpStatusCode> _statuses = [];

        [SetUp]
        public async Task Setup()
        {
            _statuses.Clear();
            StartHost();
            string jobId = await EnqueueAsync();

            _statuses["exact"] = (await GetStatusAsync(jobId)).StatusCode;
            _statuses["upper case"] = (await GetStatusAsync(jobId.ToUpperInvariant())).StatusCode;
            _statuses["trailing space"] = (await GetStatusAsync(jobId + " ")).StatusCode;
            _statuses["prefix"] = (await GetStatusAsync(jobId[..^1])).StatusCode;
            _statuses["suffix"] = (await GetStatusAsync(jobId + "0")).StatusCode;
        }

        [Test]
        public void It_finds_the_exact_identifier() => _statuses["exact"].Should().Be(HttpStatusCode.OK);

        [TestCase("upper case")]
        [TestCase("trailing space")]
        [TestCase("prefix")]
        [TestCase("suffix")]
        public void It_returns_404_for_any_other_identifier(string variant) =>
            _statuses[variant].Should().Be(HttpStatusCode.NotFound);
    }

    [TestFixture]
    public class Given_jobs_of_two_tenants_and_without_a_tenant : JobApiTestBase
    {
        private StatusRead _ownRead = null!;
        private StatusRead _otherTenantRead = null!;
        private StatusRead _absentRead = null!;
        private StatusRead _untenantedRead = null!;
        private string _tenantAJobId = "";

        [SetUp]
        public async Task Setup()
        {
            (long tenantAId, string tenantAName) = await CreateNamedTenantAsync();
            (_, string tenantBName) = await CreateNamedTenantAsync();
            StartHost(multiTenancy: true);

            _tenantAJobId = await EnqueueAsync(new TenantContext.Multitenant(tenantAId, tenantAName));
            string untenantedJobId = await EnqueueAsync();

            _ownRead = await GetStatusAsync(_tenantAJobId, tenantName: tenantAName);
            _otherTenantRead = await GetStatusAsync(_tenantAJobId, tenantName: tenantBName);
            _absentRead = await GetStatusAsync(Guid.NewGuid().ToString("N"), tenantName: tenantBName);
            _untenantedRead = await GetStatusAsync(untenantedJobId, tenantName: tenantAName);
        }

        [Test]
        public void It_returns_the_job_to_its_own_tenant()
        {
            _ownRead.StatusCode.Should().Be(HttpStatusCode.OK);
            _ownRead.Body["jobId"]!.GetValue<string>().Should().Be(_tenantAJobId);
        }

        [Test]
        public void It_returns_404_for_other_tenants_job() =>
            _otherTenantRead.StatusCode.Should().Be(HttpStatusCode.NotFound);

        [Test]
        public void It_answers_the_other_tenant_exactly_as_for_an_absent_job()
        {
            _otherTenantRead.MediaType.Should().Be("application/problem+json");
            _otherTenantRead.Body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:not-found");
            _otherTenantRead.Body["detail"]!
                .GetValue<string>()
                .Should()
                .Be(_absentRead.Body["detail"]!.GetValue<string>());
            _absentRead.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Test]
        public void It_returns_404_to_a_tenant_for_a_job_without_a_tenant() =>
            _untenantedRead.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
