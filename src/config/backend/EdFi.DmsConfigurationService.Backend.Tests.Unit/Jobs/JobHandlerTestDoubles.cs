// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

/// <summary>A payload that satisfies the contract: one identifier and one number.</summary>
public sealed record RefreshPayload([property: JobIdentifier(64)] string DataStoreCode, int DataStoreId);

public sealed class RefreshHandler : IJobHandler<RefreshPayload>
{
    public Task ExecuteAsync(
        JobExecutionContext context,
        RefreshPayload payload,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;
}

/// <summary>The type's own rule: the data store id must be positive.</summary>
public sealed class RefreshValidator : IJobPayloadValidator<RefreshPayload>
{
    public const string DataStoreIdOutOfRange = nameof(DataStoreIdOutOfRange);

    public IReadOnlyList<string> Validate(RefreshPayload payload) =>
        payload.DataStoreId < 1 ? [DataStoreIdOutOfRange] : [];
}

/// <summary>A payload the contract rejects: a <see cref="DateTime"/> is not an allowed member type.</summary>
public sealed record TimestampedPayload(DateTime When);

public sealed class TimestampedHandler : IJobHandler<TimestampedPayload>
{
    public Task ExecuteAsync(
        JobExecutionContext context,
        TimestampedPayload payload,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;
}

public sealed class TimestampedValidator : IJobPayloadValidator<TimestampedPayload>
{
    public IReadOnlyList<string> Validate(TimestampedPayload payload) => [];
}

/// <summary>Records every enqueue it receives and answers with <see cref="Result"/>.</summary>
public sealed class RecordingJobRepository : IJobRepository
{
    public List<(JobEnqueueCommand Command, DbTransaction? Transaction)> Enqueued { get; } = [];

    public JobEnqueueResult Result { get; set; } = new JobEnqueueResult.Success("job-1");

    public Task<JobEnqueueResult> EnqueueJob(
        JobEnqueueCommand command,
        DbTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        Enqueued.Add((command, transaction));
        return Task.FromResult(Result);
    }

    public Task<JobStatusQueryResult> GetJobStatus(string jobId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>Records every upsert it receives and answers with <see cref="Result"/>.</summary>
public sealed class RecordingScheduleRepository : IJobScheduleRepository
{
    public List<JobScheduleUpsertCommand> Upserted { get; } = [];

    public JobScheduleUpsertResult Result { get; set; } = new JobScheduleUpsertResult.Success(42);

    public Task<JobScheduleUpsertResult> Upsert(
        JobScheduleUpsertCommand command,
        CancellationToken cancellationToken
    )
    {
        Upserted.Add(command);
        return Task.FromResult(Result);
    }

    public Task<JobScheduleDisableResult> Disable(string scheduleType, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<JobScheduleDisableResult> DisableById(long id, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<JobScheduleListResult> ListByType(string scheduleType, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<JobScheduleMaterializeResult> MaterializeNextDue(
        string owner,
        int leaseSeconds,
        string newJobId,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException();
}

/// <summary>A transaction object the enqueuer should pass through untouched.</summary>
public sealed class StandInTransaction : DbTransaction
{
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    protected override DbConnection? DbConnection => null;

    public override void Commit() => throw new NotSupportedException();

    public override void Rollback() => throw new NotSupportedException();
}

internal static class JobServices
{
    public const string RefreshJobType = "DataStore.RefreshEducationOrganizations";

    /// <summary>
    /// A provider with the refresh handler registered for payload versions 1 and 2 and recording repositories, built
    /// with <c>ValidateOnBuild</c> so every registered service is known to resolve.
    /// </summary>
    public static ServiceProvider WithRefreshHandler(Action<IServiceCollection>? configure = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<RecordingJobRepository>();
        services.AddSingleton<IJobRepository>(provider =>
            provider.GetRequiredService<RecordingJobRepository>()
        );
        services.AddSingleton<RecordingScheduleRepository>();
        services.AddSingleton<IJobScheduleRepository>(provider =>
            provider.GetRequiredService<RecordingScheduleRepository>()
        );
        services.AddJobHandler<RefreshHandler, RefreshPayload, RefreshValidator>(RefreshJobType, 1, 2);
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
