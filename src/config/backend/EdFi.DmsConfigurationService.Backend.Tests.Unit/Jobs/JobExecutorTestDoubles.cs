// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public sealed record ExecutorPayload([property: JobIdentifier(64)] string Code, int Number);

public sealed class ExecutorValidator : IJobPayloadValidator<ExecutorPayload>
{
    public IReadOnlyList<string> Validate(ExecutorPayload payload) =>
        payload.Number < 0 ? ["NumberOutOfRange"] : [];
}

/// <summary>What the test's handler does, and what it observed when it was resolved.</summary>
public sealed class HandlerScript
{
    public Func<JobExecutionContext, ExecutorPayload, CancellationToken, Task> Run { get; set; } =
        (_, _, _) => Task.CompletedTask;

    public int Resolutions { get; set; }

    public TenantContext? TenantAtResolution { get; set; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ExecutorHandler : IJobHandler<ExecutorPayload>
{
    private readonly HandlerScript _script;

    public ExecutorHandler(HandlerScript script, ITenantContextProvider tenant)
    {
        _script = script;
        script.Resolutions++;
        script.TenantAtResolution = tenant.Context;
    }

    public Task ExecuteAsync(
        JobExecutionContext context,
        ExecutorPayload payload,
        CancellationToken cancellationToken
    )
    {
        _script.Started.TrySetResult();
        return _script.Run(context, payload, cancellationToken);
    }
}

/// <summary>
/// A lease repository with scripted results. Renewals come from <see cref="NextRenewal"/>, so a test can hold one
/// pending. A simulated row lets the recovery-by-state cases observe what a later claim would find.
/// </summary>
public sealed class ScriptedLeaseRepository : IJobLeaseRepository
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public TaskCompletionSource RenewCalled { get; private set; } = NewSignal();

    public Func<Task<JobWriteResult>> NextRenewal { get; set; } = Succeed;

    public Func<string, Task<JobWriteResult>> OutcomeResult { get; set; } = _ => Succeed();

    /// <summary>When set, a completion is recorded in the row before its (scripted) result is returned.</summary>
    public bool CompletionCommits { get; set; }

    public SimulatedRow Row { get; } = new();

    public static Task<JobWriteResult> Succeed() =>
        Task.FromResult<JobWriteResult>(new JobWriteResult.Success(null, DateTime.UtcNow));

    public static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ResetRenewSignal() => RenewCalled = NewSignal();

    public IEnumerable<string> OutcomeWrites =>
        Calls.Where(call => call is not "Renew" and not "ClaimNext" and not "Exhaust");

    public Task<JobWriteResult> Renew(
        long id,
        string owner,
        long fencingToken,
        int leaseSeconds,
        CancellationToken cancellationToken
    )
    {
        Calls.Enqueue("Renew");
        LastLeaseSeconds = leaseSeconds;
        Task<JobWriteResult> renewal = NextRenewal();
        RenewCalled.TrySetResult();
        return renewal;
    }

    public int LastLeaseSeconds { get; private set; }

    public Task<JobWriteResult> Complete(
        long id,
        string owner,
        long fencingToken,
        CancellationToken cancellationToken
    )
    {
        Calls.Enqueue("Complete");
        if (CompletionCommits)
        {
            Row.Status = JobStatuses.Completed;
        }
        return OutcomeResult("Complete");
    }

    public Task<JobWriteResult> FailTransient(
        long id,
        string owner,
        long fencingToken,
        int backoffSeconds,
        CancellationToken cancellationToken
    )
    {
        Calls.Enqueue($"FailTransient:{backoffSeconds}");
        return OutcomeResult("FailTransient");
    }

    public Task<JobWriteResult> FailTerminal(
        long id,
        string owner,
        long fencingToken,
        JobErrorCode errorCode,
        CancellationToken cancellationToken
    )
    {
        Calls.Enqueue($"FailTerminal:{errorCode.Code}:{errorCode.Message}");
        return OutcomeResult("FailTerminal");
    }

    public Task<JobWriteResult> ReleaseToPending(
        long id,
        string owner,
        long fencingToken,
        CancellationToken cancellationToken
    )
    {
        Calls.Enqueue("ReleaseToPending");
        return OutcomeResult("ReleaseToPending");
    }

    /// <summary>A claim against the simulated row: an expired or pending row under the limit is claimed again.</summary>
    public Task<JobClaimResult> ClaimNext(
        string owner,
        int leaseSeconds,
        int maxAttempts,
        CancellationToken cancellationToken
    )
    {
        Calls.Enqueue("ClaimNext");
        bool claimable =
            (Row.Status == JobStatuses.Pending || (Row.Status == JobStatuses.InProgress && Row.LeaseExpired))
            && Row.AttemptCount < maxAttempts;
        if (!claimable)
        {
            return Task.FromResult<JobClaimResult>(new JobClaimResult.NoneAvailable());
        }

        bool reclaimed = Row.Status == JobStatuses.InProgress;
        Row.Status = JobStatuses.InProgress;
        Row.AttemptCount++;
        Row.FencingToken++;
        Row.LeaseExpired = false;
        return Task.FromResult<JobClaimResult>(
            new JobClaimResult.Claimed(
                ExecutorHarness.Job(Row.AttemptCount, Row.FencingToken, reclaimed: reclaimed)
            )
        );
    }

    public Task<JobExhaustResult> Exhaust(
        int maxAttempts,
        JobErrorCode errorCode,
        CancellationToken cancellationToken
    ) => Task.FromResult<JobExhaustResult>(new JobExhaustResult.Success(0));
}

/// <summary>The row a <see cref="ScriptedLeaseRepository"/> simulates, as a claimed job leaves it.</summary>
public sealed class SimulatedRow
{
    public string Status { get; set; } = JobStatuses.InProgress;
    public int AttemptCount { get; set; } = 1;
    public long FencingToken { get; set; } = 1;
    public bool LeaseExpired { get; set; }
}

/// <summary>
/// A fence that enters the execution gate as the real one does and checks the execution state before and after the
/// work; <see cref="DatabaseCalls"/> counts the fences that reached the database.
/// </summary>
public sealed class RecordingFenceFactory : IJobFenceFactory
{
    public int DatabaseCalls;

    public bool LeaseLost { get; set; }

    public IJobFence Create(ClaimedJob job, JobExecutionOwnership ownership) => new Fence(this, ownership);

    private sealed class Fence(RecordingFenceFactory factory, JobExecutionOwnership ownership) : IJobFence
    {
        public async Task ExecuteAsync(
            Func<DbTransaction, CancellationToken, Task> work,
            CancellationToken cancellationToken
        )
        {
            using IDisposable gate = await ownership.EnterAsync(cancellationToken);
            if (ownership.State != JobOwnershipState.Owned)
            {
                throw new JobLeaseLostException();
            }

            Interlocked.Increment(ref factory.DatabaseCalls);
            if (factory.LeaseLost)
            {
                throw new JobLeaseLostException();
            }

            await work(new StandInTransaction(), cancellationToken);
            if (ownership.State != JobOwnershipState.Owned)
            {
                throw new JobLeaseLostException();
            }
        }
    }
}

public sealed class ScriptedTenantRepository : ITenantRepository
{
    public Dictionary<long, string> Tenants { get; } = [];

    public bool Fails { get; set; }

    public Task<TenantGetResult> GetTenant(long id)
    {
        if (Fails)
        {
            return Task.FromResult<TenantGetResult>(new TenantGetResult.FailureUnknown("the lookup failed"));
        }

        return Task.FromResult<TenantGetResult>(
            Tenants.TryGetValue(id, out string? name)
                ? new TenantGetResult.Success(new TenantResponse { Id = id, Name = name })
                : new TenantGetResult.FailureNotFound()
        );
    }

    public Task<TenantInsertResult> InsertTenant(TenantInsertCommand command) =>
        throw new NotSupportedException();

    public Task<TenantQueryResult> QueryTenant(PagingQuery query) => throw new NotSupportedException();

    public Task<TenantGetByNameResult> GetTenantByName(string name) => throw new NotSupportedException();
}

/// <summary>Counts the scopes the executor creates and disposes.</summary>
public sealed class TrackingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
{
    public int Created;
    public int Disposed;

    public IServiceScope CreateScope()
    {
        Interlocked.Increment(ref Created);
        return new TrackedScope(this, inner.CreateScope());
    }

    private sealed class TrackedScope(TrackingScopeFactory factory, IServiceScope scope)
        : IServiceScope,
            IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => scope.ServiceProvider;

        public void Dispose()
        {
            Interlocked.Increment(ref factory.Disposed);
            scope.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref factory.Disposed);
            if (scope is IAsyncDisposable asyncScope)
            {
                await asyncScope.DisposeAsync();
            }
            else
            {
                scope.Dispose();
            }
        }
    }
}

public sealed record LogEntry(
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> Fields
)
{
    public object? Field(string name) => Fields.Single(field => field.Key == name).Value;
}

/// <summary>Records every entry, with its structured fields.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        IReadOnlyList<KeyValuePair<string, object?>> fields =
            state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
        Entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), fields));
        if (exception is not null)
        {
            throw new InvalidOperationException("The job runtime must never log an exception object.");
        }
    }
}

/// <summary>A <see cref="JobExecutor"/> with scripted collaborators and a fake clock.</summary>
public sealed class ExecutorHarness : IDisposable
{
    public const string JobType = "Test.Execute";
    public const int MaxAttempts = 5;

    private readonly ServiceProvider _provider;

    public ExecutorHarness(bool multiTenancy = false)
    {
        ServiceCollection services = new();
        services.AddSingleton(Script);
        services.AddScoped<ITenantContextProvider, TenantContextProvider>();
        services.AddSingleton<ITenantRepository>(Tenants);
        services.AddJobHandler<ExecutorHandler, ExecutorPayload, ExecutorValidator>(JobType, 1, 2);
        services.AddJobErrorCode("ConsumerRejected", "The consumer rejected the job.");
        _provider = services.BuildServiceProvider();
        Scopes = new TrackingScopeFactory(_provider.GetRequiredService<IServiceScopeFactory>());
        Executor = new JobExecutor(
            Scopes,
            Leases,
            Fences,
            _provider.GetRequiredService<IJobHandlerRegistry>(),
            _provider.GetRequiredService<IJobErrorCodeRegistry>(),
            Options.Create(Settings),
            new JobRuntimeEnvironment(multiTenancy),
            Time,
            Logger
        );
    }

    public HandlerScript Script { get; } = new();
    public ScriptedLeaseRepository Leases { get; } = new();
    public RecordingFenceFactory Fences { get; } = new();
    public ScriptedTenantRepository Tenants { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
    public CapturingLogger<JobExecutor> Logger { get; } = new();
    public JobOptions Settings { get; } = new() { MaxAttempts = MaxAttempts };
    public TrackingScopeFactory Scopes { get; }
    public JobExecutor Executor { get; }

    public static ClaimedJob Job(
        int attemptCount = 1,
        long fencingToken = 1,
        string jobType = JobType,
        short payloadVersion = 1,
        string payloadJson = """{"code":"a","number":1}""",
        long? tenantId = null,
        string jobId = "job-11",
        bool reclaimed = false
    )
    {
        DateTime now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        return new ClaimedJob(
            11,
            jobId,
            tenantId,
            jobType,
            payloadVersion,
            payloadJson,
            attemptCount,
            fencingToken,
            "worker-a",
            now.AddMinutes(5),
            now.AddSeconds(-30),
            now.AddSeconds(-2),
            now,
            reclaimed
        );
    }

    /// <summary>Advances the clock by one renewal interval and waits until the renewal it triggers has started.</summary>
    public async Task TriggerRenewalAsync()
    {
        Leases.ResetRenewSignal();
        Time.Advance(Settings.RenewalInterval);
        await Leases.RenewCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public void Dispose() => _provider.Dispose();
}
