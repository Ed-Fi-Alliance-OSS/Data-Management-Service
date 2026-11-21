// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using Dapper;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Jobs;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.Job;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Codes = EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh.EducationOrganizationRefreshJobErrorCodes;
using FailureCode = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionFailureCode;
using ProjectionCodes = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionJobErrorCodes;
using ReadResult = EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection.EducationOrganizationProjectionReadResult;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The education organization refresh through the real job runtime (DMS-1441 spec step 2.5): the real job lease
/// repository, executor, fence and snapshot repository, the registered refresh handler and target refresher, and a
/// signalled reader double. Lease expiry is driven by database column updates and the refresher's renewal ticks by
/// fake time; nothing sleeps for a fixed time.
/// </summary>
public class EducationOrganizationRefreshJobTests
{
    private const string OwnerA = "worker-a";
    private const string OwnerB = "worker-b";
    private const string ContractVersion = "educationOrganizationProjection.v1";
    private const string RefreshAll = """{"scope":1,"dataStoreId":0}""";

    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

    private static string RefreshOne(int dataStoreId) => $$"""{"scope":2,"dataStoreId":{{dataStoreId}}}""";

    /// <summary>A one-shot pause at a refresher seam: the first caller reports its lease and waits for release.</summary>
    public sealed class Pause
    {
        private readonly TaskCompletionSource<EducationOrganizationSnapshotLease> _reached = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _used;

        public async Task HoldAsync(
            EducationOrganizationSnapshotLease lease,
            CancellationToken cancellationToken
        )
        {
            if (Interlocked.Exchange(ref _used, 1) != 0)
            {
                return;
            }

            _reached.TrySetResult(lease);
            await _release.Task.WaitAsync(cancellationToken);
        }

        /// <summary>The paused execution's lease, once it has reached the seam.</summary>
        public Task<EducationOrganizationSnapshotLease> Reached => _reached.Task.WaitAsync(_wait);

        public void Release() => _release.TrySetResult();
    }

    /// <summary>
    /// The projection reader double. Every call is recorded with its data store; <see cref="OnRead"/> runs first,
    /// then a blocking reader waits for <see cref="Release"/> or its token, and <see cref="Answer"/> gives the result
    /// from the data store and its call number for that data store.
    /// </summary>
    public sealed class SignalledReader : IEducationOrganizationProjectionReader
    {
        private readonly ConcurrentQueue<int> _calls = new();
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _canceled = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Func<int, int, ReadResult> Answer { get; set; } =
            (dataStoreId, call) => Organizations(dataStoreId * 100 + call);

        public Func<int, int, CancellationToken, Task>? OnRead { get; set; }

        public bool Blocks { get; set; }

        public IReadOnlyList<int> Calls => [.. _calls];

        public Task Entered => _entered.Task.WaitAsync(_wait);

        public Task Canceled => _canceled.Task.WaitAsync(_wait);

        public int CallsFor(int dataStoreId) => _calls.Count(id => id == dataStoreId);

        public void Release() => _release.TrySetResult();

        public async Task<ReadResult> ReadAllAsync(
            EducationOrganizationProjectionReadRequest request,
            CancellationToken cancellationToken
        )
        {
            _calls.Enqueue(request.DataStoreId);
            int call = CallsFor(request.DataStoreId);
            if (OnRead is { } onRead)
            {
                await onRead(request.DataStoreId, call, cancellationToken);
            }

            _entered.TrySetResult();
            if (Blocks)
            {
                try
                {
                    await _release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _canceled.TrySetResult();
                    throw;
                }
            }

            return Answer(request.DataStoreId, call);
        }

        public static ReadResult Organizations(params long[] ids) =>
            new ReadResult.Success(
                [
                    .. ids.Select(id => new EducationOrganizationProjectionItem(
                        id,
                        $"Organization {id}",
                        null,
                        "edfi.School",
                        null
                    )),
                ],
                ContractVersion,
                1,
                0
            );

        public static ReadResult Fails(FailureCode code) =>
            new ReadResult.Failure(
                EducationOrganizationProjectionFailure.Create(code, EducationOrganizationProjectionStage.Page)
            );
    }

    public sealed record LogRecord(
        string Category,
        string? EventName,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> Fields
    )
    {
        public object? Field(string name) => Fields.FirstOrDefault(field => field.Key == name).Value;
    }

    /// <summary>Records every log entry of the harness's container, with its structured fields.</summary>
    public sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogRecord> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

        public IEnumerable<LogRecord> Named(string eventName) =>
            Entries.Where(entry => entry.EventName == eventName);

        public void Dispose() { }

        private sealed class RecordingLogger(RecordingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) =>
                provider.Entries.Enqueue(
                    new LogRecord(
                        category,
                        eventId.Name,
                        formatter(state, exception) + (exception is null ? "" : " " + exception),
                        state as IReadOnlyList<KeyValuePair<string, object?>> ?? []
                    )
                );
        }
    }

    /// <summary>
    /// One worker replica: a real executor over the real lease repository and fence factory, with the refresh
    /// registered by <see cref="EducationOrganizationRefreshServiceCollectionExtensions.AddEducationOrganizationRefresh"/>,
    /// the real snapshot repository, the reader double, and the refresher's seams. The executor runs on the system
    /// clock; the refresher's renewal loop runs on <see cref="Time"/>.
    /// </summary>
    public sealed class RefreshHarness : IDisposable
    {
        private readonly ServiceProvider _provider;

        public RefreshHarness(
            JobOptions settings,
            IJobFenceFactory? fences = null,
            JobDatabaseSessionHooks? snapshotHooks = null
        )
        {
            Settings = settings;
            Leases = new JobLeaseRepository(MssqlTestConfiguration.DatabaseOptions, settings.LeaseTimings);

            ServiceCollection services = new();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
            services.AddSingleton(Options.Create(settings));
            services.AddSingleton<TimeProvider>(Time);
            services.AddScoped<ITenantContextProvider, TenantContextProvider>();
            services.AddEducationOrganizationRefresh(new ConfigurationBuilder().Build());
            services.AddScoped<IEducationOrganizationSnapshotRepository>(
                provider => new EducationOrganizationSnapshotRepository(
                    MssqlTestConfiguration.DatabaseOptions,
                    provider.GetRequiredService<IOptions<JobOptions>>(),
                    provider.GetRequiredService<ITenantContextProvider>()
                )
                {
                    SessionHooks = snapshotHooks,
                }
            );
            services.AddSingleton<IEducationOrganizationProjectionReader>(Reader);
            services.AddTransient(provider =>
            {
                EducationOrganizationTargetRefresher refresher =
                    ActivatorUtilities.CreateInstance<EducationOrganizationTargetRefresher>(provider);
                refresher.AfterAcquire = AfterAcquire is { } afterAcquire ? afterAcquire.HoldAsync : null;
                refresher.AfterReclaim = AfterReclaim is { } afterReclaim ? afterReclaim.HoldAsync : null;
                refresher.AfterAdmission = AfterAdmission is { } afterAdmission
                    ? afterAdmission.HoldAsync
                    : null;
                return refresher;
            });
            _provider = services.BuildServiceProvider();

            Executor = new JobExecutor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Leases,
                fences
                    ?? new MssqlJobFenceFactory(
                        MssqlTestConfiguration.DatabaseOptions,
                        settings.LeaseTimings
                    ),
                _provider.GetRequiredService<IJobHandlerRegistry>(),
                _provider.GetRequiredService<IJobErrorCodeRegistry>(),
                Options.Create(settings),
                new JobRuntimeEnvironment(MultiTenancy: false),
                TimeProvider.System,
                Metrics,
                _provider.GetRequiredService<ILogger<JobExecutor>>()
            );
        }

        public JobOptions Settings { get; }
        public JobLeaseRepository Leases { get; }
        public SignalledReader Reader { get; } = new();
        public RecordingLoggerProvider Logs { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public JobMetrics Metrics { get; } = new();
        public JobExecutor Executor { get; }
        public IServiceProvider Services => _provider;

        public Pause? AfterAcquire { get; set; }
        public Pause? AfterReclaim { get; set; }
        public Pause? AfterAdmission { get; set; }

        public Task<JobExecutionResult> RunAsync(ClaimedJob job) =>
            Executor.ExecuteAsync(job, CancellationToken.None).WaitAsync(_wait);

        /// <summary>Fires the refresher's next renewal tick.</summary>
        public void Tick() => Time.Advance(Settings.RenewalInterval);

        /// <summary>The worker a deployment without data store management runs: managed work is never claimed.</summary>
        public JobWorkerService WorkerWithoutManagement() =>
            new(
                Leases,
                Executor,
                Options.Create(Settings),
                TimeProvider.System,
                NullLogger<JobWorkerService>.Instance,
                JobClaimPolicy.ExcludeManagedDataStores,
                new DmsDataStoreProvisionerReadiness()
            );

        public IEnumerable<LogRecord> LeaseLosses =>
            Logs.Named(EducationOrganizationTargetRefresher.TargetLeaseLost.Name!);

        public void Dispose()
        {
            Metrics.Dispose();
            _provider.Dispose();
        }
    }

    public sealed record RefreshJob(long Id, string JobId);

    public sealed record TargetState(long LastGeneration, long? ActiveGeneration, string? LeaseOwner);

    public abstract class RefreshJobTestBase : JobRuntimeIntegrationTests.RuntimeTestBase
    {
        /// <summary>A worker replica with short-lived settings; the fixture disposes it in its teardown.</summary>
        protected static RefreshHarness Harness(
            int maxAttempts = 5,
            IJobFenceFactory? fences = null,
            JobDatabaseSessionHooks? snapshotHooks = null
        )
        {
            JobOptions settings = WithoutRenewals();
            settings.MaxAttempts = maxAttempts;
            return new RefreshHarness(settings, fences, snapshotHooks);
        }

        protected async Task<int> InsertDataStoreAsync(string name) =>
            await Connection!.ExecuteScalarAsync<int>(
                """
                INSERT INTO dmscs.DataStore (Name, DataStoreType, Provider)
                OUTPUT inserted.Id
                VALUES (@Name, N'Shared', N'sqlserver');
                """,
                new { Name = name }
            );

        /// <summary>Enqueues a refresh the way the refresh routes do.</summary>
        protected static async Task<RefreshJob> EnqueueAsync(string payload)
        {
            JobEnqueueResult enqueued = await StatusRepository()
                .EnqueueJob(
                    new JobEnqueueCommand(
                        EducationOrganizationRefreshJobTypes.Refresh,
                        EducationOrganizationRefreshJobTypes.PayloadVersion,
                        payload
                    ),
                    null,
                    CancellationToken.None
                );
            string jobId = enqueued.Should().BeOfType<JobEnqueueResult.Success>().Subject.JobId;
            return new RefreshJob(await JobIdAsync(jobId), jobId);
        }

        protected static async Task<long> JobIdAsync(string jobId)
        {
            await using SqlConnection connection = new(MssqlTestConfiguration.DatabaseConnectionString);
            return await connection.ExecuteScalarAsync<long>(
                "SELECT Id FROM dmscs.Job WHERE JobId = @JobId;",
                new { JobId = jobId }
            );
        }

        protected static async Task<ClaimedJob> ClaimRefreshAsync(RefreshHarness harness, string owner) =>
            Claimed(
                await harness.Leases.ClaimNext(
                    owner,
                    (int)harness.Settings.LeaseDuration.TotalSeconds,
                    harness.Settings.MaxAttempts,
                    CancellationToken.None
                )
            );

        /// <summary>Makes a job scheduled for retry due now, as if its backoff had passed.</summary>
        protected Task MakeDueAsync(long id) =>
            Connection!.ExecuteAsync(
                "UPDATE dmscs.Job SET NextAttemptAt = DATEADD(second, -1, SYSUTCDATETIME()) WHERE Id = @Id;",
                new { Id = id }
            );

        /// <summary>Moves the target lease into the past by database time, as if it had expired unrenewed.</summary>
        protected Task ExpireTargetLeaseAsync(int dataStoreId) =>
            Connection!.ExecuteAsync(
                """
                UPDATE dmscs.EducationOrganizationSnapshotTarget
                SET LeaseExpiresAt = DATEADD(second, -1, SYSUTCDATETIME())
                WHERE DataStoreId = @Id;
                """,
                new { Id = dataStoreId }
            );

        protected Task<TargetState> TargetAsync(int dataStoreId) =>
            Connection!.QuerySingleAsync<TargetState>(
                """
                SELECT LastGeneration, ActiveGeneration, LeaseOwner
                FROM dmscs.EducationOrganizationSnapshotTarget WHERE DataStoreId = @Id;
                """,
                new { Id = dataStoreId }
            );

        /// <summary>The distinct generations present in the organization rows, ascending.</summary>
        protected async Task<List<long>> GenerationsAsync(int dataStoreId) =>
            (
                await Connection!.QueryAsync<long>(
                    """
                    SELECT DISTINCT Generation FROM dmscs.EducationOrganizationSnapshot
                    WHERE DataStoreId = @Id ORDER BY Generation;
                    """,
                    new { Id = dataStoreId }
                )
            ).ToList();

        /// <summary>The organization ids of the active generation, ascending.</summary>
        protected async Task<List<long>> ActiveIdsAsync(int dataStoreId) =>
            (
                await Connection!.QueryAsync<long>(
                    """
                    SELECT s.EducationOrganizationId
                    FROM dmscs.EducationOrganizationSnapshot AS s
                    INNER JOIN dmscs.EducationOrganizationSnapshotTarget AS t
                        ON t.DataStoreId = s.DataStoreId AND t.ActiveGeneration = s.Generation
                    WHERE s.DataStoreId = @Id
                    ORDER BY s.EducationOrganizationId;
                    """,
                    new { Id = dataStoreId }
                )
            ).ToList();

        /// <summary>The job as <c>GET /v3/jobs/{jobId}</c> reads it.</summary>
        protected static async Task<JobStatusResponse> StatusAsync(RefreshJob job) =>
            (await StatusRepository().GetJobStatus(job.JobId, CancellationToken.None))
                .Should()
                .BeOfType<JobStatusQueryResult.Success>()
                .Subject.Job;

        private static JobRepository StatusRepository() =>
            new(
                MssqlTestConfiguration.DatabaseOptions,
                new TestAuditContext(),
                new TenantContextProvider { Context = new TenantContext.NotMultitenant() }
            );

        protected static string MessageOf(string code) =>
            ProjectionCodes.All.Concat(Codes.All).Single(pair => pair.Key == code).Value;
    }

    /// <summary>
    /// Admission (§6.2): execution A pauses after acquiring the target, or after reclaiming it; its lease is expired
    /// through the database; B takes the target over and enters the reader. When A resumes, its next lease step finds
    /// the lease taken, so A never calls the reader and the job is retried; B then publishes.
    /// </summary>
    [TestFixture("AfterAcquire", "Reclaim")]
    [TestFixture("AfterReclaim", "Admission")]
    public class Given_a_paused_execution_whose_target_is_taken_over(string seam, string refusingStep)
        : RefreshJobTestBase
    {
        private RefreshHarness _a = null!;
        private RefreshHarness _b = null!;
        private EducationOrganizationSnapshotLease _leaseA = null!;
        private int _readsByAWhileBRead;
        private JobExecutionResult _resultA = null!;
        private JobExecutionResult _resultB = null!;
        private TargetState _target = null!;
        private int _store;

        [SetUp]
        public async Task Setup()
        {
            _store = await InsertDataStoreAsync("admission");
            _a = Harness();
            _b = Harness();
            Pause pause = new();
            if (seam == "AfterAcquire")
            {
                _a.AfterAcquire = pause;
            }
            else
            {
                _a.AfterReclaim = pause;
            }
            _b.Reader.Blocks = true;

            await EnqueueAsync(RefreshOne(_store));
            Task<JobExecutionResult> runningA = _a.RunAsync(await ClaimRefreshAsync(_a, OwnerA));
            _leaseA = await pause.Reached;

            await ExpireTargetLeaseAsync(_store);
            await EnqueueAsync(RefreshOne(_store));
            Task<JobExecutionResult> runningB = _b.RunAsync(await ClaimRefreshAsync(_b, OwnerB));
            await _b.Reader.Entered;

            pause.Release();
            _resultA = await runningA;
            _readsByAWhileBRead = _a.Reader.Calls.Count;

            _b.Reader.Release();
            _resultB = await runningB;
            _target = await TargetAsync(_store);
        }

        [TearDown]
        public void TearDown()
        {
            _a.Dispose();
            _b.Dispose();
        }

        [Test]
        public void It_never_lets_the_paused_execution_read() => _readsByAWhileBRead.Should().Be(0);

        [Test]
        public void It_reports_the_lease_taken_at_the_next_lease_step() =>
            _a
                .LeaseLosses.Should()
                .ContainSingle()
                .Which.Should()
                .Match<LogRecord>(entry =>
                    (string?)entry.Field("Operation") == refusingStep
                    && (string?)entry.Field("Reason") == nameof(TargetLeaseLossReason.Taken)
                );

        [Test]
        public void It_retries_the_paused_execution() =>
            _resultA.Outcome.Should().Be(JobExecutionOutcome.RetryScheduled);

        [Test]
        public void It_publishes_the_takeover()
        {
            _resultB.Outcome.Should().Be(JobExecutionOutcome.Completed);
            _target.ActiveGeneration.Should().Be(_leaseA.Generation + 1);
        }
    }

    /// <summary>
    /// Mid-read theft (§6.2): A is reading when its lease is taken over through the database and B enters the
    /// reader. A's next renewal tick finds the lease taken and cancels A's read; B then publishes. A stages nothing.
    /// </summary>
    [TestFixture]
    public class Given_a_lease_taken_over_while_an_execution_reads : RefreshJobTestBase
    {
        private RefreshHarness _a = null!;
        private RefreshHarness _b = null!;
        private JobExecutionResult _resultA = null!;
        private JobExecutionResult _resultB = null!;
        private TargetState _target = null!;
        private List<long> _generations = [];
        private int _store;

        [SetUp]
        public async Task Setup()
        {
            _store = await InsertDataStoreAsync("theft");
            _a = Harness();
            _b = Harness();
            _a.Reader.Blocks = true;
            _b.Reader.Blocks = true;

            await EnqueueAsync(RefreshOne(_store));
            Task<JobExecutionResult> runningA = _a.RunAsync(await ClaimRefreshAsync(_a, OwnerA));
            await _a.Reader.Entered;

            await ExpireTargetLeaseAsync(_store);
            await EnqueueAsync(RefreshOne(_store));
            Task<JobExecutionResult> runningB = _b.RunAsync(await ClaimRefreshAsync(_b, OwnerB));
            await _b.Reader.Entered;

            _a.Tick();
            await _a.Reader.Canceled;
            _resultA = await runningA;

            _b.Reader.Release();
            _resultB = await runningB;
            _target = await TargetAsync(_store);
            _generations = await GenerationsAsync(_store);
        }

        [TearDown]
        public void TearDown()
        {
            _a.Dispose();
            _b.Dispose();
        }

        [Test]
        public void It_cancels_the_read_at_the_next_renewal()
        {
            _resultA.Outcome.Should().Be(JobExecutionOutcome.RetryScheduled);
            _a.LeaseLosses.Should()
                .ContainSingle()
                .Which.Should()
                .Match<LogRecord>(entry =>
                    (string?)entry.Field("Operation") == "Read"
                    && (string?)entry.Field("Reason") == nameof(TargetLeaseLossReason.Taken)
                );
        }

        [Test]
        public void It_publishes_the_takeover_alone()
        {
            _resultB.Outcome.Should().Be(JobExecutionOutcome.Completed);
            _target.ActiveGeneration.Should().Be(2);
            _generations.Should().Equal(2);
        }
    }

    /// <summary>
    /// Pause after admission, the documented delay model (§6.2): A pauses between its admission renewal and the
    /// read; its lease expires and B publishes. A then enters the reader, which the failure model allows, but cannot
    /// stage or activate, and its next renewal tick cancels the read. Only B's generation is retained.
    /// </summary>
    [TestFixture]
    public class Given_an_execution_delayed_after_admission : RefreshJobTestBase
    {
        private RefreshHarness _a = null!;
        private RefreshHarness _b = null!;
        private EducationOrganizationSnapshotLease _leaseA = null!;
        private JobExecutionResult _resultA = null!;
        private JobExecutionResult _resultB = null!;
        private SnapshotTargetResult _forcedStage = null!;
        private SnapshotTargetResult _forcedActivation = null!;
        private TargetState _target = null!;
        private List<long> _generations = [];
        private int _store;

        [SetUp]
        public async Task Setup()
        {
            _store = await InsertDataStoreAsync("delayed");
            _a = Harness();
            _b = Harness();
            Pause pause = new();
            _a.AfterAdmission = pause;
            _a.Reader.Blocks = true;

            await EnqueueAsync(RefreshOne(_store));
            Task<JobExecutionResult> runningA = _a.RunAsync(await ClaimRefreshAsync(_a, OwnerA));
            _leaseA = await pause.Reached;

            await ExpireTargetLeaseAsync(_store);
            await EnqueueAsync(RefreshOne(_store));
            _resultB = await _b.RunAsync(await ClaimRefreshAsync(_b, OwnerB));

            pause.Release();
            await _a.Reader.Entered;

            EducationOrganizationSnapshotRepository repository = new(
                MssqlTestConfiguration.DatabaseOptions,
                Options.Create(_a.Settings),
                new TenantContextProvider { Context = new TenantContext.NotMultitenant() }
            );
            _forcedStage = await repository.StageAsync(
                _leaseA,
                [new EducationOrganizationProjectionItem(9, "Stale", null, "edfi.School", null)],
                ContractVersion,
                CancellationToken.None
            );
            _forcedActivation = await ActivateWithOwnFenceAsync(repository, _leaseA);

            _a.Tick();
            await _a.Reader.Canceled;
            _resultA = await runningA;
            _target = await TargetAsync(_store);
            _generations = await GenerationsAsync(_store);
        }

        [TearDown]
        public void TearDown()
        {
            _a.Dispose();
            _b.Dispose();
        }

        private async Task<SnapshotTargetResult> ActivateWithOwnFenceAsync(
            EducationOrganizationSnapshotRepository repository,
            EducationOrganizationSnapshotLease lease
        )
        {
            await EnqueueAsync(RefreshAll);
            ClaimedJob claim = await ClaimRefreshAsync(_a, "forcing-worker");
            SnapshotTargetResult? result = null;
            await FenceFactory()
                .Create(claim, new JobExecutionOwnership())
                .ExecuteAsync(
                    async (transaction, token) =>
                        result = await repository.ActivateAsync(transaction, lease, ContractVersion, token),
                    CancellationToken.None
                );
            return result!;
        }

        [Test]
        public void It_refuses_a_forced_stage_and_activation_by_the_delayed_execution()
        {
            _forcedStage.Should().BeOfType<SnapshotTargetResult.LeaseLost>();
            _forcedActivation.Should().BeOfType<SnapshotTargetResult.LeaseLost>();
        }

        [Test]
        public void It_cancels_the_delayed_read_at_the_next_renewal()
        {
            _resultA.Outcome.Should().Be(JobExecutionOutcome.RetryScheduled);
            _a.LeaseLosses.Should().ContainSingle().Which.Field("Operation").Should().Be("Read");
        }

        [Test]
        public void It_keeps_only_the_takeovers_generation()
        {
            _resultB.Outcome.Should().Be(JobExecutionOutcome.Completed);
            _target.ActiveGeneration.Should().Be(_leaseA.Generation + 1);
            _generations.Should().Equal(_leaseA.Generation + 1);
        }
    }

    /// <summary>
    /// Job sources: a manual, a scheduled and a recovered execution refresh one data store in turn, while a
    /// different data store refreshes in parallel with the first. Each publishes the next generation.
    /// </summary>
    [TestFixture]
    public class Given_refresh_jobs_from_every_source : RefreshJobTestBase
    {
        private RefreshHarness _harness = null!;
        private JobExecutionResult[] _results = [];
        private ClaimedJob _recovered = null!;
        private long? _sourceSchedule;
        private long _scheduleId;
        private TargetState _first = null!;
        private TargetState _second = null!;
        private List<long> _firstIds = [];
        private List<long> _secondIds = [];
        private int _store;
        private int _other;

        [SetUp]
        public async Task Setup()
        {
            _store = await InsertDataStoreAsync("sources");
            _other = await InsertDataStoreAsync("parallel");
            _harness = Harness();

            // The first reads of both data stores wait for each other, so they are in flight together.
            TaskCompletionSource storeReading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource otherReading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _harness.Reader.OnRead = async (dataStoreId, call, token) =>
            {
                if (call != 1)
                {
                    return;
                }
                if (dataStoreId == _store)
                {
                    storeReading.TrySetResult();
                    await otherReading.Task.WaitAsync(_wait, token);
                }
                else
                {
                    otherReading.TrySetResult();
                    await storeReading.Task.WaitAsync(_wait, token);
                }
            };

            await EnqueueAsync(RefreshOne(_store));
            ClaimedJob manual = await ClaimRefreshAsync(_harness, OwnerA);
            await EnqueueAsync(RefreshOne(_other));
            ClaimedJob parallel = await ClaimRefreshAsync(_harness, OwnerB);
            JobExecutionResult[] first = await Task.WhenAll(
                _harness.RunAsync(manual),
                _harness.RunAsync(parallel)
            );

            _scheduleId = await Connection!.ExecuteScalarAsync<long>(
                """
                INSERT INTO dmscs.JobSchedule (
                    ScheduleType, JobType, PayloadVersion, Payload, IntervalMinutes, Enabled, NextRunAt)
                OUTPUT inserted.Id
                VALUES (N'EducationOrganization.ScheduledRefresh', @JobType, 1, @Payload, 60, 1,
                    DATEADD(second, -1, SYSUTCDATETIME()));
                """,
                new { JobType = EducationOrganizationRefreshJobTypes.Refresh, Payload = RefreshOne(_store) }
            );
            JobScheduleMaterializeResult materialized = await new JobScheduleRepository(
                MssqlTestConfiguration.DatabaseOptions,
                new TestAuditContext(),
                new TenantContextProvider { Context = new TenantContext.NotMultitenant() }
            ).MaterializeNextDue(
                "dispatcher",
                60,
                Guid.NewGuid().ToString("N"),
                JobClaimPolicy.All,
                CancellationToken.None
            );
            string scheduledJobId = materialized
                .Should()
                .BeOfType<JobScheduleMaterializeResult.Materialized>()
                .Subject.JobId;
            JobExecutionResult scheduled = await _harness.RunAsync(await ClaimRefreshAsync(_harness, OwnerA));
            _sourceSchedule = await Connection!.ExecuteScalarAsync<long?>(
                "SELECT SourceScheduleId FROM dmscs.Job WHERE JobId = @JobId;",
                new { JobId = scheduledJobId }
            );

            // The recovered execution: a claim whose worker crashed, reclaimed after its lease expired.
            RefreshJob crashed = await EnqueueAsync(RefreshOne(_store));
            await ClaimRefreshAsync(_harness, "crashed-worker");
            await ExpireLeaseAsync(crashed.Id);
            _recovered = await ClaimRefreshAsync(_harness, OwnerB);
            JobExecutionResult recovered = await _harness.RunAsync(_recovered);

            _results = [.. first, scheduled, recovered];
            _first = await TargetAsync(_store);
            _second = await TargetAsync(_other);
            _firstIds = await ActiveIdsAsync(_store);
            _secondIds = await ActiveIdsAsync(_other);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_completes_every_execution() =>
            _results.Should().OnlyContain(result => result.Outcome == JobExecutionOutcome.Completed);

        [Test]
        public void It_runs_the_scheduled_occurrence_and_the_recovered_claim()
        {
            _sourceSchedule.Should().Be(_scheduleId);
            _recovered.Reclaimed.Should().BeTrue();
            _recovered.AttemptCount.Should().Be(2);
        }

        [Test]
        public void It_publishes_a_new_generation_for_each_source()
        {
            _harness.Reader.CallsFor(_store).Should().Be(3);
            _first.Should().Be(new TargetState(3, 3, null));
            _firstIds.Should().Equal(_store * 100 + 3);
        }

        [Test]
        public void It_publishes_the_parallel_data_store()
        {
            _harness.Reader.CallsFor(_other).Should().Be(1);
            _second.Should().Be(new TargetState(1, 1, null));
            _secondIds.Should().Equal(_other * 100 + 1);
        }
    }

    /// <summary>
    /// Retry and interleaving (§6.5): refresh-all A publishes the first data store, fails transiently on the second
    /// and is retried; meanwhile refresh-one B publishes the first data store again. A's retry re-reads every data
    /// store, including the one it published, and publishes in lease order.
    /// </summary>
    [TestFixture]
    public class Given_a_retry_after_another_job_published : RefreshJobTestBase
    {
        private RefreshHarness _harness = null!;
        private JobExecutionResult _firstAttempt = null!;
        private JobExecutionResult _other = null!;
        private JobExecutionResult _retry = null!;
        private TargetState _store = null!;
        private List<long> _storeIds = [];
        private int _first;
        private int _second;

        [SetUp]
        public async Task Setup()
        {
            _first = await InsertDataStoreAsync("retry-first");
            _second = await InsertDataStoreAsync("retry-second");
            _harness = Harness();
            _harness.Reader.Answer = (dataStoreId, call) =>
                dataStoreId == _second && call == 1
                    ? SignalledReader.Fails(FailureCode.Timeout)
                    : SignalledReader.Organizations(dataStoreId * 100 + call);

            RefreshJob a = await EnqueueAsync(RefreshAll);
            _firstAttempt = await _harness.RunAsync(await ClaimRefreshAsync(_harness, OwnerA));

            await EnqueueAsync(RefreshOne(_first));
            _other = await _harness.RunAsync(await ClaimRefreshAsync(_harness, OwnerB));

            await MakeDueAsync(a.Id);
            _retry = await _harness.RunAsync(await ClaimRefreshAsync(_harness, OwnerA));
            _store = await TargetAsync(_first);
            _storeIds = await ActiveIdsAsync(_first);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_retries_then_completes()
        {
            _firstAttempt.Outcome.Should().Be(JobExecutionOutcome.RetryScheduled);
            _other.Outcome.Should().Be(JobExecutionOutcome.Completed);
            _retry.Outcome.Should().Be(JobExecutionOutcome.Completed);
        }

        [Test]
        public void It_rereads_every_data_store_on_the_retry() =>
            _harness.Reader.Calls.Should().Equal(_first, _second, _first, _first, _second);

        [Test]
        public void It_publishes_in_lease_order()
        {
            _harness
                .Logs.Named(EducationOrganizationTargetRefresher.TargetRefreshed.Name!)
                .Where(entry => (int?)entry.Field("DataStoreId") == _first)
                .Select(entry => (long?)entry.Field("Generation"))
                .Should()
                .Equal(1L, 2L, 3L);
            _store.Should().Be(new TargetState(3, 3, null));
            _storeIds.Should().Equal(_first * 100 + 3);
        }
    }

    /// <summary>
    /// Lost acknowledgement (§6.5): the activation's fence commit takes effect but its acknowledgement is lost, so
    /// the execution ends uncertain without an outcome write. After the job lease expires, the next attempt re-reads
    /// and republishes.
    /// </summary>
    [TestFixture]
    public class Given_an_activation_whose_commit_acknowledgement_is_lost : RefreshJobTestBase
    {
        private RefreshHarness _harness = null!;
        private JobRuntimeIntegrationTests.CommitFault _fault = null!;
        private JobExecutionResult _uncertain = null!;
        private TargetState _afterFault = null!;
        private string _statusAfterFault = "";
        private ClaimedJob _reclaimed = null!;
        private JobExecutionResult _recovery = null!;
        private TargetState _target = null!;
        private List<long> _ids = [];
        private JobStatusResponse _status = null!;
        private int _store;

        [SetUp]
        public async Task Setup()
        {
            _store = await InsertDataStoreAsync("lost-ack");
            JobOptions settings = WithoutRenewals();
            _fault = JobRuntimeIntegrationTests.CommitFault.AfterCommit();
            _harness = Harness(fences: HookedFenceFactory(settings, _fault.Hooks));

            // Armed by the first read, so the next fence commit is the first activation's.
            _harness.Reader.OnRead = (_, call, _) =>
            {
                if (call == 1)
                {
                    _fault.Arm();
                }
                return Task.CompletedTask;
            };

            RefreshJob job = await EnqueueAsync(RefreshOne(_store));
            _uncertain = await _harness.RunAsync(await ClaimRefreshAsync(_harness, OwnerA));
            _afterFault = await TargetAsync(_store);
            _statusAfterFault = (await StatusAsync(job)).Status;

            await ExpireLeaseAsync(job.Id);
            _reclaimed = await ClaimRefreshAsync(_harness, OwnerB);
            _recovery = await _harness.RunAsync(_reclaimed);
            _target = await TargetAsync(_store);
            _ids = await ActiveIdsAsync(_store);
            _status = await StatusAsync(job);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_ends_the_first_execution_uncertain_with_the_activation_committed()
        {
            _fault.Fired.Should().BeTrue();
            _uncertain
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.OwnershipUncertain, "FenceCommitUnknown"));
            _afterFault.Should().Be(new TargetState(1, 1, null));
            _statusAfterFault.Should().Be(JobStatuses.InProgress);
        }

        [Test]
        public void It_rereads_and_republishes_on_the_next_attempt()
        {
            _reclaimed.AttemptCount.Should().Be(2);
            _harness.Reader.CallsFor(_store).Should().Be(2);
            _recovery.Outcome.Should().Be(JobExecutionOutcome.Completed);
            _target.Should().Be(new TargetState(2, 2, null));
            _ids.Should().Equal(_store * 100 + 2);
            _status.Status.Should().Be(JobStatuses.Completed);
        }
    }

    /// <summary>
    /// Exhaustion without a handler run (§6.5, stated limitation): the last attempt's worker crashes after its claim,
    /// so the sweep records <c>AttemptsExhausted</c> and no composite. The handler never runs again, and the data
    /// store published by the first attempt stays published.
    /// </summary>
    [TestFixture]
    public class Given_a_refresh_exhausted_without_a_handler_run : RefreshJobTestBase
    {
        private RefreshHarness _harness = null!;
        private JobExecutionResult _firstAttempt = null!;
        private int _readsBeforeExhaustion;
        private JobExhaustResult _sweep = null!;
        private JobStatusResponse _status = null!;
        private List<long> _publishedIds = [];
        private int _published;
        private int _failing;

        [SetUp]
        public async Task Setup()
        {
            _published = await InsertDataStoreAsync("exhaust-published");
            _failing = await InsertDataStoreAsync("exhaust-failing");
            _harness = Harness(maxAttempts: 2);
            _harness.Reader.Answer = (dataStoreId, call) =>
                dataStoreId == _failing
                    ? SignalledReader.Fails(FailureCode.ServiceUnavailable)
                    : SignalledReader.Organizations(dataStoreId * 100 + call);

            RefreshJob job = await EnqueueAsync(RefreshAll);
            _firstAttempt = await _harness.RunAsync(await ClaimRefreshAsync(_harness, OwnerA));
            _readsBeforeExhaustion = _harness.Reader.Calls.Count;

            await MakeDueAsync(job.Id);
            await ClaimRefreshAsync(_harness, "crashed-worker");
            await ExpireLeaseAsync(job.Id);
            _sweep = await _harness.Leases.Exhaust(
                _harness.Settings.MaxAttempts,
                JobErrorCode.AttemptsExhausted,
                JobClaimPolicy.All,
                CancellationToken.None
            );
            _status = await StatusAsync(job);
            _publishedIds = await ActiveIdsAsync(_published);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_records_attempts_exhausted_without_a_composite()
        {
            _firstAttempt.Outcome.Should().Be(JobExecutionOutcome.RetryScheduled);
            _sweep.Should().BeOfType<JobExhaustResult.Success>().Which.ExhaustedCount.Should().Be(1);
            _status.Status.Should().Be(JobStatuses.Error);
            _status.ErrorMessage.Should().Be(JobErrorCode.AttemptsExhausted.Message);
        }

        [Test]
        public void It_runs_the_handler_no_more() =>
            _harness.Reader.Calls.Should().HaveCount(_readsBeforeExhaustion).And.HaveCount(2);

        [Test]
        public void It_keeps_the_earlier_publication() => _publishedIds.Should().Equal(_published * 100 + 1);
    }

    /// <summary>
    /// Mixed failures through retry (§6.5, §6.6): of three data stores, one refreshes, one fails permanently and one
    /// fails transiently on every attempt. The final attempt fails with the composite that the job status reports,
    /// listing both categories in data store order with their registered text; the refreshed data store is
    /// published and the failing ones keep their previous snapshots.
    /// </summary>
    [TestFixture]
    public class Given_mixed_failures_through_the_final_attempt : RefreshJobTestBase
    {
        private RefreshHarness _harness = null!;
        private JobExecutionResult _planting = null!;
        private JobExecutionResult _firstAttempt = null!;
        private JobExecutionResult _finalAttempt = null!;
        private JobStatusResponse _status = null!;
        private List<long> _refreshedIds = [];
        private List<long> _transientIds = [];
        private TargetState _permanent = null!;
        private int _refreshed;
        private int _permanentStore;
        private int _transient;

        [SetUp]
        public async Task Setup()
        {
            _refreshed = await InsertDataStoreAsync("mixed-refreshed");
            _permanentStore = await InsertDataStoreAsync("mixed-permanent");
            _transient = await InsertDataStoreAsync("mixed-transient");
            _harness = Harness(maxAttempts: 2);
            _harness.Reader.Answer = Answer;

            // The transient data store's earlier snapshot, which the failed refresh must keep.
            await EnqueueAsync(RefreshOne(_transient));
            _planting = await _harness.RunAsync(await ClaimRefreshAsync(_harness, OwnerA));

            RefreshJob job = await EnqueueAsync(RefreshAll);
            _firstAttempt = await _harness.RunAsync(await ClaimRefreshAsync(_harness, OwnerA));
            await MakeDueAsync(job.Id);
            _finalAttempt = await _harness.RunAsync(await ClaimRefreshAsync(_harness, OwnerA));

            _status = await StatusAsync(job);
            _refreshedIds = await ActiveIdsAsync(_refreshed);
            _transientIds = await ActiveIdsAsync(_transient);
            _permanent = await TargetAsync(_permanentStore);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        /// <summary>The permanent data store always fails; the transient one fails after its first read.</summary>
        private ReadResult Answer(int dataStoreId, int call)
        {
            if (dataStoreId == _permanentStore)
            {
                return SignalledReader.Fails(FailureCode.TargetSchemaIncompatible);
            }

            return dataStoreId == _transient && call > 1
                ? SignalledReader.Fails(FailureCode.Timeout)
                : SignalledReader.Organizations(dataStoreId * 100 + call);
        }

        [Test]
        public void It_retries_then_fails_with_the_composite()
        {
            _planting.Outcome.Should().Be(JobExecutionOutcome.Completed);
            _firstAttempt.Outcome.Should().Be(JobExecutionOutcome.RetryScheduled);
            _finalAttempt
                .Should()
                .Be(new JobExecutionResult(JobExecutionOutcome.Failed, ErrorCode: Codes.Incomplete));
        }

        [Test]
        public void It_attempts_every_data_store_on_every_attempt() =>
            _harness
                .Reader.Calls.Should()
                .Equal(
                    _transient,
                    _refreshed,
                    _permanentStore,
                    _transient,
                    _refreshed,
                    _permanentStore,
                    _transient
                );

        [Test]
        public void It_reports_the_composite_from_registered_text()
        {
            _status.Status.Should().Be(JobStatuses.Error);
            _status
                .ErrorMessage.Should()
                .Be(
                    $"{Codes.IncompleteMessage} Problem categories: {ProjectionCodes.TargetSchemaIncompatible}, "
                        + $"{Codes.TemporarilyUnavailable}. {MessageOf(ProjectionCodes.TargetSchemaIncompatible)} "
                        + MessageOf(Codes.TemporarilyUnavailable)
                );
        }

        [Test]
        public void It_publishes_the_refreshed_data_store_and_keeps_the_others()
        {
            _refreshedIds.Should().Equal(_refreshed * 100 + 2);
            _transientIds.Should().Equal(_transient * 100 + 1);
            _permanent.ActiveGeneration.Should().BeNull();
        }
    }

    /// <summary>A prune that fails after activation (§6.2): the job completes with the new snapshot published.</summary>
    [TestFixture]
    public class Given_a_prune_that_fails_after_activation : RefreshJobTestBase
    {
        private RefreshHarness _harness = null!;
        private JobExecutionResult[] _results = [];
        private JobStatusResponse _status = null!;
        private TargetState _target = null!;
        private List<long> _ids = [];
        private List<long> _generations = [];
        private int _store;

        [SetUp]
        public async Task Setup()
        {
            _store = await InsertDataStoreAsync("prune");
            _harness = Harness(
                snapshotHooks: new JobDatabaseSessionHooks
                {
                    BeforeOperation = (operation, _) =>
                        operation == "DeleteSuperseded"
                            ? throw new IOException("Injected: the prune failed.")
                            : Task.CompletedTask,
                }
            );

            await EnqueueAsync(RefreshOne(_store));
            JobExecutionResult first = await _harness.RunAsync(await ClaimRefreshAsync(_harness, OwnerA));
            RefreshJob job = await EnqueueAsync(RefreshOne(_store));
            JobExecutionResult second = await _harness.RunAsync(await ClaimRefreshAsync(_harness, OwnerA));

            _results = [first, second];
            _status = await StatusAsync(job);
            _target = await TargetAsync(_store);
            _ids = await ActiveIdsAsync(_store);
            _generations = await GenerationsAsync(_store);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_completes_the_job()
        {
            _results.Should().OnlyContain(result => result.Outcome == JobExecutionOutcome.Completed);
            _status.Status.Should().Be(JobStatuses.Completed);
        }

        [Test]
        public void It_publishes_the_new_snapshot()
        {
            _target.ActiveGeneration.Should().Be(2);
            _ids.Should().Equal(_store * 100 + 2);
        }

        [Test]
        public void It_logs_the_failed_prune_and_leaves_the_superseded_rows()
        {
            _harness
                .Logs.Named(EducationOrganizationTargetRefresher.SnapshotPruneFailed.Name!)
                .Should()
                .HaveCount(2);
            _generations.Should().Equal(1, 2);
        }
    }

    /// <summary>
    /// Fence refusal after staging, repeated (§6.2): three refreshes each stage a generation and then find their job
    /// fence refused because the job lease was taken. The published snapshot is kept every time, and each
    /// acquisition reclaims what the previous one staged, so at most two generations are ever retained.
    /// </summary>
    [TestFixture]
    public class Given_repeated_fence_refusals_after_staging : RefreshJobTestBase
    {
        private RefreshHarness _harness = null!;
        private JobExecutionResult _planting = null!;
        private List<JobExecutionResult> _refused = [];
        private List<List<long>> _retained = [];
        private List<TargetState> _targets = [];
        private List<List<long>> _activeIds = [];
        private int _store;

        [SetUp]
        public async Task Setup()
        {
            _refused = [];
            _retained = [];
            _targets = [];
            _activeIds = [];
            _store = await InsertDataStoreAsync("refusal");
            _harness = Harness();

            // After the first read, every read takes the running job's lease, so its fence refuses the activation.
            _harness.Reader.OnRead = async (_, call, _) =>
            {
                if (call > 1)
                {
                    await Connection!.ExecuteAsync(
                        """
                        UPDATE dmscs.Job SET FencingToken = FencingToken + 1, LeaseOwner = N'thief'
                        WHERE Status = N'InProgress';
                        """
                    );
                }
            };

            await EnqueueAsync(RefreshOne(_store));
            _planting = await _harness.RunAsync(await ClaimRefreshAsync(_harness, OwnerA));

            for (int refusal = 0; refusal < 3; refusal++)
            {
                await EnqueueAsync(RefreshOne(_store));
                _refused.Add(await _harness.RunAsync(await ClaimRefreshAsync(_harness, OwnerA)));
                _retained.Add(await GenerationsAsync(_store));
                _targets.Add(await TargetAsync(_store));
                _activeIds.Add(await ActiveIdsAsync(_store));
            }
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_ends_each_refused_execution_uncertain()
        {
            _planting.Outcome.Should().Be(JobExecutionOutcome.Completed);
            _refused.Should().OnlyContain(result => result.Outcome == JobExecutionOutcome.OwnershipUncertain);
        }

        [Test]
        public void It_keeps_the_published_snapshot() =>
            _activeIds.Should().AllSatisfy(ids => ids.Should().Equal(_store * 100 + 1));

        [Test]
        public void It_retains_the_published_and_the_latest_staged_generation_only() =>
            _retained
                .Should()
                .BeEquivalentTo(
                    new List<List<long>>
                    {
                        new() { 1, 2 },
                        new() { 1, 3 },
                        new() { 1, 4 },
                    },
                    options => options.WithStrictOrdering()
                );

        [Test]
        public void It_releases_the_target_after_each_refusal() =>
            _targets
                .Should()
                .Equal(new TargetState(2, 1, null), new TargetState(3, 1, null), new TargetState(4, 1, null));
    }

    /// <summary>
    /// A deployment without data store management (§6.5): the worker's claim policy excludes managed work only, so
    /// it claims and runs the refresh. Nothing in the container provides an HTTP context.
    /// </summary>
    [TestFixture]
    public class Given_a_worker_without_data_store_management : RefreshJobTestBase
    {
        private RefreshHarness _harness = null!;
        private JobStatusResponse _status = null!;
        private List<long> _ids = [];
        private int _store;

        [SetUp]
        public async Task Setup()
        {
            _store = await InsertDataStoreAsync("unmanaged");
            _harness = Harness();
            RefreshJob job = await EnqueueAsync(RefreshOne(_store));

            using (JobWorkerService worker = _harness.WorkerWithoutManagement())
            {
                await worker.StartAsync(CancellationToken.None);
                await EventuallyAsync(
                    async () => (await StatusAsync(job)).Status == JobStatuses.Completed,
                    "the refresh completed"
                );
                await worker.StopAsync(CancellationToken.None).WaitAsync(_wait);
            }

            _status = await StatusAsync(job);
            _ids = await ActiveIdsAsync(_store);
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_runs_the_refresh()
        {
            _status.Status.Should().Be(JobStatuses.Completed);
            _ids.Should().Equal(_store * 100 + 1);
        }

        [Test]
        public void It_runs_without_an_http_context() =>
            _harness.Services.GetService<IHttpContextAccessor>().Should().BeNull();
    }

    /// <summary>
    /// Persisted-column sentinel scan (§6.13): a data store whose stored connection string holds sentinels (and
    /// cannot be decrypted), and a reader failure whose exception text holds every sentinel kind. No column of the
    /// job, target or organization tables, and no log entry, contains any of them.
    /// </summary>
    [TestFixture]
    public class Given_refreshes_that_meet_secret_values : RefreshJobTestBase
    {
        private static readonly string[] _sentinels =
        [
            "SentinelClientSecret-7c1e",
            "SentinelToken-5a9d",
            "SentinelConnection-3f2b",
            "SentinelCursor-9e4a",
            "SentinelPageBody-2d6c",
        ];

        private RefreshHarness _harness = null!;
        private List<string> _persisted = [];
        private JobStatusResponse _published = null!;
        private JobStatusResponse _failed = null!;
        private int _store;

        [SetUp]
        public async Task Setup()
        {
            _store = await InsertDataStoreAsync("sentinel-published");
            await Connection!.ExecuteAsync(
                "UPDATE dmscs.DataStore SET ConnectionString = CONVERT(varbinary(max), @Value) WHERE Id = @Id;",
                new { Id = _store, Value = $"Host=db;Password={_sentinels[2]}" }
            );
            int throwing = await InsertDataStoreAsync("sentinel-throwing");
            _harness = Harness(maxAttempts: 1);
            _harness.Reader.OnRead = (dataStoreId, _, _) =>
                dataStoreId == throwing
                    ? throw new InvalidOperationException(
                        $"secret={_sentinels[0]} token={_sentinels[1]} cursor={_sentinels[3]} body={_sentinels[4]}"
                    )
                    : Task.CompletedTask;

            RefreshJob published = await EnqueueAsync(RefreshOne(_store));
            await _harness.RunAsync(await ClaimRefreshAsync(_harness, OwnerA));
            RefreshJob failed = await EnqueueAsync(RefreshOne(throwing));
            await _harness.RunAsync(await ClaimRefreshAsync(_harness, OwnerA));

            _published = await StatusAsync(published);
            _failed = await StatusAsync(failed);
            _persisted = (
                await Connection!.QueryAsync<string>(
                    """
                    SELECT ISNULL((SELECT * FROM dmscs.Job FOR JSON PATH), N'')
                    UNION ALL SELECT ISNULL((SELECT * FROM dmscs.EducationOrganizationSnapshotTarget FOR JSON PATH), N'')
                    UNION ALL SELECT ISNULL((SELECT * FROM dmscs.EducationOrganizationSnapshot FOR JSON PATH), N'');
                    """
                )
            ).ToList();
        }

        [TearDown]
        public void TearDown() => _harness.Dispose();

        [Test]
        public void It_refreshed_one_data_store_and_failed_the_other()
        {
            _published.Status.Should().Be(JobStatuses.Completed);
            _failed.Status.Should().Be(JobStatuses.Error);
            _persisted.Should().Contain(row => row.Contains("Organization " + (_store * 100 + 1)));
        }

        [Test]
        public void It_persists_no_sentinel() =>
            _persisted.Should().NotContain(row => _sentinels.Any(row.Contains));

        [Test]
        public void It_logs_no_sentinel()
        {
            _harness.Logs.Entries.Should().NotBeEmpty();
            _harness
                .Logs.Entries.Should()
                .NotContain(entry =>
                    _sentinels.Any(entry.Message.Contains)
                    || entry.Fields.Any(field =>
                        _sentinels.Any(sentinel => $"{field.Value}".Contains(sentinel))
                    )
                );
        }
    }
}
