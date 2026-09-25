// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The schedule dispatcher hosted service (spec D-8, step 3.5). Each poll it materializes due schedules, one bounded
/// transaction each, until none is due, then waits <see cref="JobOptions.PollInterval"/>. A materialization that ends
/// in <c>OwnershipLost</c> or <c>FailureUnknown</c> ends the poll early; its schedule is left as the rolled-back
/// transaction left it and is picked up again by a later poll. Failures are logged by their safe diagnostic fields and
/// never fault the host.
/// </summary>
public sealed class JobScheduleDispatcherService(
    IServiceScopeFactory scopeFactory,
    JobMetrics metrics,
    IOptions<JobOptions> options,
    TimeProvider timeProvider,
    ILogger<JobScheduleDispatcherService> logger
) : BackgroundService
{
    internal static readonly EventId ScheduleOccurrenceEnqueued = new(
        1437_20,
        nameof(ScheduleOccurrenceEnqueued)
    );
    internal static readonly EventId ScheduleOccurrenceAlreadyEnqueued = new(
        1437_21,
        nameof(ScheduleOccurrenceAlreadyEnqueued)
    );
    internal static readonly EventId ScheduleMaterializationFailed = new(
        1437_22,
        nameof(ScheduleMaterializationFailed)
    );

    /// <summary>
    /// The lease a materialization takes on its schedule. The lease is never persisted: the materialization commits
    /// with the lease cleared or rolls back. It only has to outlast the transaction's own deadline, so it is twice that
    /// deadline.
    /// </summary>
    public static int LeaseSeconds { get; } =
        (int)(JobScheduleTimings.MaterializationTimeout.TotalSeconds * 2);

    /// <summary>The lease owner this replica materializes as.</summary>
    public string Owner { get; } = JobLeaseOwner.Create();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        JobOptions settings = options.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Only the type chain is kept; the exception object is never logged (D-16a).
            string? failedWith = null;
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                failedWith = JobDiagnostics.TypeChain(exception);
            }

            if (failedWith is not null)
            {
                LogFailure(null, failedWith, null, "MaterializeNextDue");
            }

            try
            {
                await Task.Delay(settings.PollInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>One poll: materializations until none is due, or until one does not succeed.</summary>
    private async Task PollAsync(CancellationToken stoppingToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IJobScheduleRepository repository =
            scope.ServiceProvider.GetRequiredService<IJobScheduleRepository>();

        while (!stoppingToken.IsCancellationRequested)
        {
            JobScheduleMaterializeResult result = await repository.MaterializeNextDue(
                Owner,
                LeaseSeconds,
                Guid.NewGuid().ToString("N"),
                stoppingToken
            );

            switch (result)
            {
                case JobScheduleMaterializeResult.Materialized materialized:
                    metrics.OccurrenceEnqueued(materialized.ScheduleType);
                    logger.LogInformation(
                        ScheduleOccurrenceEnqueued,
                        "{Event} ScheduleId={ScheduleId} ScheduleType={ScheduleType} JobId={JobId} Occurrence={Occurrence} NextRunAt={NextRunAt}",
                        nameof(ScheduleOccurrenceEnqueued),
                        materialized.ScheduleId,
                        JobDiagnostics.SafeIdentifier(materialized.ScheduleType),
                        JobDiagnostics.SafeIdentifier(materialized.JobId),
                        materialized.Occurrence,
                        materialized.NewNextRunAt
                    );
                    break;
                case JobScheduleMaterializeResult.AlreadyEnqueued already:
                    // Only reachable through defensive recovery (D-8): the occurrence existed and the schedule advanced.
                    logger.LogWarning(
                        ScheduleOccurrenceAlreadyEnqueued,
                        "{Event} ScheduleId={ScheduleId} ScheduleType={ScheduleType} Occurrence={Occurrence} NextRunAt={NextRunAt}",
                        nameof(ScheduleOccurrenceAlreadyEnqueued),
                        already.ScheduleId,
                        JobDiagnostics.SafeIdentifier(already.ScheduleType),
                        already.Occurrence,
                        already.NewNextRunAt
                    );
                    break;
                case JobScheduleMaterializeResult.NoneDue:
                    return;
                case JobScheduleMaterializeResult.OwnershipLost:
                    LogFailure("OwnershipLost", null, null, "MaterializeNextDue");
                    return;
                case JobScheduleMaterializeResult.FailureUnknown failure:
                    LogFailure(
                        "FailureUnknown",
                        failure.Diagnostic.ExceptionTypeChain,
                        failure.Diagnostic.ProviderErrorCode,
                        failure.Diagnostic.Operation
                    );
                    return;
                default:
                    throw new InvalidOperationException(
                        "The schedule repository returned an unknown result."
                    );
            }
        }
    }

    private void LogFailure(
        string? outcome,
        string? exceptionTypeChain,
        string? providerErrorCode,
        string operation
    ) =>
        logger.LogWarning(
            ScheduleMaterializationFailed,
            "{Event} LeaseOwner={LeaseOwner} Outcome={Outcome} ExceptionTypeChain={ExceptionTypeChain} ProviderErrorCode={ProviderErrorCode} Operation={Operation}",
            nameof(ScheduleMaterializationFailed),
            JobDiagnostics.SafeIdentifier(Owner),
            outcome,
            exceptionTypeChain,
            providerErrorCode,
            operation
        );
}
