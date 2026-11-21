// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationRefresh;

public class EducationOrganizationRefreshScheduleReconcilerTests
{
    private const int Interval = 45;
    private const string QuerySecret = "tenant-query-secret-sentinel";

    private static readonly JobScheduleUpsertCommand _expectedCommand = new(
        "EducationOrganization.ScheduledRefresh",
        "EducationOrganization.Refresh",
        1,
        """{"scope":1,"dataStoreId":0}""",
        Interval,
        RunFirstOccurrenceImmediately: true
    );

    /// <summary>One upsert as the schedule service received it, with the tenant of the scope that resolved it.</summary>
    internal sealed record Upsert(
        TenantContext ResolvedWith,
        TenantContext CalledWith,
        JobScheduleUpsertCommand Command
    );

    /// <summary>A schedule service that records each upsert and fails it when the test says so.</summary>
    internal sealed class RecordingScheduleService(
        ITenantContextProvider tenant,
        ConcurrentQueue<Upsert> upserts,
        Func<TenantContext, JobScheduleServiceResult> outcome
    ) : IJobScheduleService
    {
        private readonly TenantContext _resolvedWith = tenant.Context;

        public Task<JobScheduleServiceResult> UpsertAsync(
            JobScheduleUpsertCommand command,
            CancellationToken cancellationToken
        )
        {
            upserts.Enqueue(new Upsert(_resolvedWith, tenant.Context, command));
            return Task.FromResult(outcome(tenant.Context));
        }
    }

    /// <summary>A fake clock that signals each timer created on it, so a test advances only a pending delay.</summary>
    internal sealed class SignallingTimeProvider : FakeTimeProvider
    {
        public SemaphoreSlim TimersCreated { get; } = new(0);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            TimersCreated.Release();
            return timer;
        }
    }

    /// <summary>The reconciler with fakes for tenants, schedules and the schedule service, in one tenancy mode.</summary>
    internal sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Harness(bool multitenant, TimeProvider? timeProvider = null)
        {
            ScheduleRepository = () => Schedules;
            ServiceCollection services = new();
            services.AddLogging(logging => logging.AddProvider(Logs));
            services.AddScoped<ITenantContextProvider, TenantContextProvider>();
            services.AddSingleton(Tenants);
            services.AddTransient(_ => ScheduleRepository());
            services.AddTransient<IJobScheduleService>(provider => new RecordingScheduleService(
                provider.GetRequiredService<ITenantContextProvider>(),
                Upserts,
                context => UpsertOutcome(context)
            ));
            _provider = services.BuildServiceProvider();
            Reconciler = new EducationOrganizationRefreshScheduleReconciler(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(
                    new EducationOrganizationRefreshOptions { EdOrgsRefreshIntervalInMins = Interval }
                ),
                new JobRuntimeEnvironment(multitenant),
                timeProvider ?? TimeProvider.System,
                _provider.GetRequiredService<ILogger<EducationOrganizationRefreshScheduleReconciler>>()
            );
            A.CallTo(() => Tenants.QueryTenant(A<PagingQuery>._))
                .ReturnsLazily((PagingQuery query) => Task.FromResult(TenantPage(query)));
            A.CallTo(() => Tenants.GetTenant(A<long>._))
                .ReturnsLazily((long id) => Task.FromResult(TenantLookup(id)));
            A.CallTo(() => Schedules.ListByType(A<string>._, A<CancellationToken>._))
                .ReturnsLazily(() => Task.FromResult(Listed()));
            A.CallTo(() => Schedules.DisableById(A<long>._, A<CancellationToken>._))
                .ReturnsLazily((long id, CancellationToken _) => Task.FromResult(DisableOutcome(id)));
        }

        public RecordingLoggerProvider Logs { get; } = new();
        public ITenantRepository Tenants { get; } = A.Fake<ITenantRepository>();
        public IJobScheduleRepository Schedules { get; } = A.Fake<IJobScheduleRepository>();
        public ConcurrentQueue<Upsert> Upserts { get; } = new();
        public EducationOrganizationRefreshScheduleReconciler Reconciler { get; }

        /// <summary>The tenants, in identifier order, that the tenant query pages through.</summary>
        public List<TenantResponse> AllTenants { get; } = [];

        public Func<PagingQuery, TenantQueryResult?> PageOverride { get; set; } = _ => null;
        public Func<long, TenantGetResult> TenantLookup { get; set; } =
            id => new TenantGetResult.Success(new TenantResponse { Id = id, Name = $"tenant-{id}" });
        public Func<JobScheduleListResult> Listed { get; set; } = () => new JobScheduleListResult.Success([]);
        public Func<long, JobScheduleDisableResult> DisableOutcome { get; set; } =
            _ => new JobScheduleDisableResult.Success();
        public Func<TenantContext, JobScheduleServiceResult> UpsertOutcome { get; set; } =
            _ => new JobScheduleServiceResult.Success(1);
        public Func<IJobScheduleRepository> ScheduleRepository { get; set; }

        private TenantQueryResult TenantPage(PagingQuery query) =>
            PageOverride(query)
            ?? new TenantQueryResult.Success([
                .. AllTenants.Skip(query.Offset!.Value).Take(query.Limit!.Value),
            ]);

        public void AddTenants(params long[] ids) =>
            AllTenants.AddRange(ids.Select(id => new TenantResponse { Id = id, Name = $"tenant-{id}" }));

        public static JobScheduleSummary Schedule(long id, long? tenantId, bool enabled = true) =>
            new(id, tenantId, enabled, Interval, new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc));

        public IEnumerable<long> DisabledIds() =>
            Fake.GetCalls(Schedules)
                .Where(call => call.Method.Name == nameof(IJobScheduleRepository.DisableById))
                .Select(call => call.GetArgument<long>(0));

        public IEnumerable<RecordedLog> Failures() =>
            Logs.Records.Where(record =>
                record.State.Any(pair =>
                    pair.Key == "Event"
                    && Equals(pair.Value, "EducationOrganizationScheduleReconciliationFailed")
                )
            );

        public static string? Field(RecordedLog record, string name) =>
            record.State.FirstOrDefault(pair => pair.Key == name).Value?.ToString();

        public void Dispose() => _provider.Dispose();
    }

    private static Harness NewHarness(bool multitenant, TimeProvider? timeProvider = null) =>
        new(multitenant, timeProvider);

    [TestFixture]
    public class Given_multi_tenant_mode
    {
        private Harness _harness = null!;

        [SetUp]
        public void SetUp() => _harness = NewHarness(multitenant: true);

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public async Task It_upserts_each_tenant_schedule_in_a_scope_that_carries_the_tenant()
        {
            _harness.AddTenants(3, 7, 12);

            var summary = await _harness.Reconciler.ReconcileAsync(CancellationToken.None);

            _harness
                .Upserts.Select(upsert => upsert.ResolvedWith)
                .Should()
                .Equal(
                    new TenantContext.Multitenant(3, "tenant-3"),
                    new TenantContext.Multitenant(7, "tenant-7"),
                    new TenantContext.Multitenant(12, "tenant-12")
                );
            _harness.Upserts.Should().OnlyContain(upsert => upsert.CalledWith == upsert.ResolvedWith);
            _harness.Upserts.Select(upsert => upsert.Command).Should().AllBeEquivalentTo(_expectedCommand);
            summary.Upserted.Should().Be(3);
            summary.DisablePhaseSkipped.Should().BeFalse();
        }

        [TestCase(250, new[] { 0, 100, 200 })]
        [TestCase(100, new[] { 0, 100 })]
        [TestCase(0, new[] { 0 })]
        public async Task It_pages_through_every_tenant_in_pages_of_one_hundred(int count, int[] offsets)
        {
            _harness.AddTenants([.. Enumerable.Range(1, count).Select(id => (long)id)]);

            await _harness.Reconciler.ReconcileAsync(CancellationToken.None);

            Fake.GetCalls(_harness.Tenants)
                .Where(call => call.Method.Name == nameof(ITenantRepository.QueryTenant))
                .Select(call => call.GetArgument<PagingQuery>(0)!)
                .Select(query => (query.Offset, query.Limit, query.OrderBy))
                .Should()
                .Equal(offsets.Select(offset => ((int?)offset, (int?)100, (string?)null)));
            _harness
                .Upserts.Select(upsert => ((TenantContext.Multitenant)upsert.ResolvedWith).TenantId)
                .Should()
                .Equal(Enumerable.Range(1, count).Select(id => (long)id));
        }

        [TestCase(false), TestCase(true)]
        public async Task It_keeps_the_upserts_and_disables_nothing_when_a_tenant_page_cannot_be_read(
            bool throws
        )
        {
            _harness.AddTenants([.. Enumerable.Range(1, 150).Select(id => (long)id)]);
            _harness.PageOverride = query =>
            {
                if (query.Offset != 100)
                {
                    return null;
                }
                return throws
                    ? throw new InvalidOperationException(QuerySecret)
                    : new TenantQueryResult.FailureUnknown(QuerySecret);
            };
            _harness.Listed = () => new JobScheduleListResult.Success([Harness.Schedule(1, null)]);

            var summary = await _harness.Reconciler.ReconcileAsync(CancellationToken.None);

            _harness.Upserts.Should().HaveCount(100);
            summary.DisablePhaseSkipped.Should().BeTrue();
            A.CallTo(() => _harness.Schedules.ListByType(A<string>._, A<CancellationToken>._))
                .MustNotHaveHappened();
            _harness.DisabledIds().Should().BeEmpty();
            var failure = _harness.Failures().Should().ContainSingle().Subject;
            Harness.Field(failure, "Operation").Should().Be("QueryTenant");
            Harness
                .Field(failure, "ExceptionTypeChain")
                .Should()
                .Be(throws ? "System.InvalidOperationException" : null);
            _harness.Logs.Records.Should().NotContain(record => record.AllText.Contains(QuerySecret));
        }

        [Test]
        public async Task It_decides_each_enabled_schedule_by_a_fresh_tenant_lookup()
        {
            _harness.AddTenants(1, 3);
            _harness.Listed = () =>
                new JobScheduleListResult.Success([
                    Harness.Schedule(10, null),
                    Harness.Schedule(11, 1),
                    Harness.Schedule(12, 2),
                    Harness.Schedule(13, 3),
                    Harness.Schedule(14, 4),
                    // Created after this run's enumeration, by another replica, for a tenant that now exists.
                    Harness.Schedule(15, 5),
                    Harness.Schedule(16, null, enabled: false),
                    Harness.Schedule(17, 2, enabled: false),
                ]);
            _harness.TenantLookup = id =>
                id switch
                {
                    2 => new TenantGetResult.FailureNotFound(),
                    3 => new TenantGetResult.FailureUnknown(QuerySecret),
                    4 => throw new TimeoutException(QuerySecret),
                    _ => new TenantGetResult.Success(new TenantResponse { Id = id, Name = $"tenant-{id}" }),
                };

            var summary = await _harness.Reconciler.ReconcileAsync(CancellationToken.None);

            _harness.DisabledIds().Should().Equal(10, 12);
            Fake.GetCalls(_harness.Tenants)
                .Where(call => call.Method.Name == nameof(ITenantRepository.GetTenant))
                .Select(call => call.GetArgument<long>(0))
                .Should()
                .Equal(1, 2, 3, 4, 5);
            summary.Disabled.Should().Be(2);
            summary.Kept.Should().Be(4);
            _harness
                .Failures()
                .Select(failure => (Harness.Field(failure, "Operation"), Harness.Field(failure, "TenantId")))
                .Should()
                .Equal(("GetTenant", "3"), ("GetTenant", "4"));
            _harness.Logs.Records.Should().NotContain(record => record.AllText.Contains(QuerySecret));
        }

        [Test]
        public async Task It_continues_past_failed_upserts_and_still_decides_the_schedules()
        {
            _harness.AddTenants(1, 2, 3);
            _harness.UpsertOutcome = context =>
                ((TenantContext.Multitenant)context).TenantId switch
                {
                    1 => new JobScheduleServiceResult.FailureUnknown(
                        new JobFailureDiagnostic("Npgsql.NpgsqlException", "40P01", "UpsertSchedule")
                    ),
                    2 => throw new InvalidOperationException(QuerySecret),
                    _ => new JobScheduleServiceResult.Success(3),
                };
            _harness.Listed = () => new JobScheduleListResult.Success([Harness.Schedule(20, null)]);

            var summary = await _harness.Reconciler.ReconcileAsync(CancellationToken.None);

            _harness.Upserts.Should().HaveCount(3);
            summary.Upserted.Should().Be(1);
            summary.UpsertFailed.Should().Be(2);
            _harness.DisabledIds().Should().Equal(20);
            _harness
                .Failures()
                .Select(failure =>
                    (
                        Harness.Field(failure, "Operation"),
                        Harness.Field(failure, "TenantId"),
                        Harness.Field(failure, "ProviderErrorCode")
                    )
                )
                .Should()
                .Equal(("UpsertSchedule", "1", "40P01"), ("UpsertSchedule", "2", null));
            _harness.Logs.Records.Should().NotContain(record => record.AllText.Contains(QuerySecret));
        }

        [TestCase(false), TestCase(true)]
        public async Task It_disables_nothing_when_the_schedules_cannot_be_listed(bool throws)
        {
            _harness.AddTenants(1);
            _harness.Listed = () =>
                throws
                    ? throw new InvalidOperationException(QuerySecret)
                    : new JobScheduleListResult.FailureUnknown(
                        new JobFailureDiagnostic("Npgsql.NpgsqlException", "57014", "ListByType")
                    );

            var summary = await _harness.Reconciler.ReconcileAsync(CancellationToken.None);

            summary.Upserted.Should().Be(1);
            summary.DisablePhaseSkipped.Should().BeTrue();
            _harness.DisabledIds().Should().BeEmpty();
            Harness.Field(_harness.Failures().Single(), "Operation").Should().Be("ListSchedules");
        }

        [Test]
        public async Task It_counts_a_failed_disable_and_decides_the_next_schedule()
        {
            _harness.Listed = () =>
                new JobScheduleListResult.Success([Harness.Schedule(30, null), Harness.Schedule(31, null)]);
            _harness.DisableOutcome = id =>
                id == 30
                    ? new JobScheduleDisableResult.FailureUnknown(
                        new JobFailureDiagnostic("Npgsql.NpgsqlException", "55P03", "DisableById")
                    )
                    : new JobScheduleDisableResult.Success();

            var summary = await _harness.Reconciler.ReconcileAsync(CancellationToken.None);

            _harness.DisabledIds().Should().Equal(30, 31);
            summary.Disabled.Should().Be(1);
            summary.DisableFailed.Should().Be(1);
            Harness.Field(_harness.Failures().Single(), "Operation").Should().Be("DisableSchedule");
        }
    }

    [TestFixture]
    public class Given_single_tenant_mode
    {
        private Harness _harness = null!;

        [SetUp]
        public void SetUp() => _harness = NewHarness(multitenant: false);

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public async Task It_upserts_one_schedule_without_a_tenant_and_reads_no_tenant()
        {
            _harness.AddTenants(1, 2);

            var summary = await _harness.Reconciler.ReconcileAsync(CancellationToken.None);

            var upsert = _harness.Upserts.Should().ContainSingle().Subject;
            upsert.ResolvedWith.Should().BeOfType<TenantContext.NotMultitenant>();
            upsert.Command.Should().Be(_expectedCommand);
            summary.Upserted.Should().Be(1);
            A.CallTo(() => _harness.Tenants.QueryTenant(A<PagingQuery>._)).MustNotHaveHappened();
        }

        [TestCase(true), TestCase(false)]
        public async Task It_disables_every_tenant_schedule_and_keeps_its_own(bool upsertSucceeds)
        {
            if (!upsertSucceeds)
            {
                _harness.UpsertOutcome = _ => new JobScheduleServiceResult.FailureUnknown(
                    new JobFailureDiagnostic("System.TimeoutException", null, "UpsertSchedule")
                );
            }
            _harness.Listed = () =>
                new JobScheduleListResult.Success([
                    Harness.Schedule(40, null),
                    Harness.Schedule(41, 1),
                    Harness.Schedule(42, 2),
                    Harness.Schedule(43, 3, enabled: false),
                ]);

            var summary = await _harness.Reconciler.ReconcileAsync(CancellationToken.None);

            _harness.DisabledIds().Should().Equal(41, 42);
            summary.Kept.Should().Be(1);
            A.CallTo(() => _harness.Tenants.GetTenant(A<long>._)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    public class Given_the_reconciler_running_as_a_hosted_service
    {
        private static readonly TimeSpan _wait = TimeSpan.FromSeconds(10);

        private SignallingTimeProvider _time = null!;
        private Harness _harness = null!;
        private SemaphoreSlim _runs = null!;

        [SetUp]
        public void SetUp()
        {
            _time = new SignallingTimeProvider();
            _runs = new SemaphoreSlim(0);
            _harness = NewHarness(multitenant: true, _time);
            _harness.AddTenants(1);
            _harness.Listed = () =>
            {
                _runs.Release();
                return new JobScheduleListResult.Success([]);
            };
        }

        [TearDown]
        public void TearDown()
        {
            _harness.Dispose();
            _runs.Dispose();
            _time.TimersCreated.Dispose();
        }

        [Test]
        public async Task It_runs_at_start_then_after_each_period()
        {
            await _harness.Reconciler.StartAsync(CancellationToken.None);
            try
            {
                (await _runs.WaitAsync(_wait)).Should().BeTrue("the first run starts with the host");
                for (int run = 2; run <= 3; run++)
                {
                    (await _time.TimersCreated.WaitAsync(_wait)).Should().BeTrue();
                    _runs.CurrentCount.Should().Be(0, "no run starts before its period elapses");
                    _time.Advance(EducationOrganizationRefreshScheduleReconciler.Period);
                    (await _runs.WaitAsync(_wait)).Should().BeTrue($"run {run} follows the period");
                }
                _harness.Upserts.Should().HaveCount(3);
            }
            finally
            {
                await _harness.Reconciler.StopAsync(CancellationToken.None);
            }
            _harness.Reconciler.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
        }

        [Test]
        public async Task It_logs_a_run_that_throws_and_runs_again_after_the_period()
        {
            int resolutions = 0;
            _harness.ScheduleRepository = () =>
                Interlocked.Increment(ref resolutions) == 1
                    ? throw new InvalidOperationException(QuerySecret)
                    : _harness.Schedules;

            await _harness.Reconciler.StartAsync(CancellationToken.None);
            try
            {
                (await _time.TimersCreated.WaitAsync(_wait)).Should().BeTrue();
                var failure = _harness.Failures().Should().ContainSingle().Subject;
                Harness.Field(failure, "Operation").Should().Be("Reconcile");
                Harness.Field(failure, "ExceptionTypeChain").Should().Be("System.InvalidOperationException");
                _time.Advance(EducationOrganizationRefreshScheduleReconciler.Period);
                (await _runs.WaitAsync(_wait)).Should().BeTrue("the next run follows the period");
            }
            finally
            {
                await _harness.Reconciler.StopAsync(CancellationToken.None);
            }
            _harness.Logs.Records.Should().NotContain(record => record.AllText.Contains(QuerySecret));
        }
    }

    [TestFixture]
    public class Given_the_refresh_registration
    {
        /// <summary>The reconciler registrations for these settings; a null value leaves the setting out.</summary>
        private static IEnumerable<ServiceDescriptor> Reconcilers(string? scheduler, string? management)
        {
            Dictionary<string, string?> values = [];
            if (scheduler is not null)
            {
                values["JobSettings:SchedulerEnabled"] = scheduler;
            }
            if (management is not null)
            {
                values["AppSettings:EnableDataStoreManagement"] = management;
            }
            ServiceCollection services = new();
            services.AddEducationOrganizationRefresh(
                new ConfigurationBuilder().AddInMemoryCollection(values).Build()
            );
            return services.Where(descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(EducationOrganizationRefreshScheduleReconciler)
            );
        }

        [TestCase(null, null)]
        [TestCase("true", null)]
        [TestCase("true", "false")]
        [TestCase("true", "true")]
        [TestCase(null, "false")]
        public void It_adds_the_reconciler_when_the_scheduler_is_on_whatever_management_is(
            string? scheduler,
            string? management
        ) => Reconcilers(scheduler, management).Should().ContainSingle();

        [TestCase(null), TestCase("false"), TestCase("true")]
        public void It_adds_no_reconciler_when_the_scheduler_is_off(string? management) =>
            Reconcilers("false", management).Should().BeEmpty();
    }
}
