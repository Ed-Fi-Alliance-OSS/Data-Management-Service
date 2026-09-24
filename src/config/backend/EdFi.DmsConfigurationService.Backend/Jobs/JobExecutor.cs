// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>How one execution ended, as the executor recorded it.</summary>
public enum JobExecutionOutcome
{
    Completed,
    RetryScheduled,
    Failed,
    ReleasedOnShutdown,

    /// <summary>The execution lost certainty about its ownership and wrote nothing more (spec D-7a, §4.4).</summary>
    OwnershipUncertain,

    /// <summary>The outcome write found another owner or an expired lease (D-4).</summary>
    LateWriteRejected,
}

/// <summary>
/// The result of one execution: its outcome, the uncertainty reason when there is one, and the registered error code
/// when the job failed terminally.
/// </summary>
public sealed record JobExecutionResult(
    JobExecutionOutcome Outcome,
    string? Reason = null,
    string? ErrorCode = null
);

/// <summary>
/// Runs one claimed job (spec §4.3, D-7, D-7a, D-11, D-12, D-13): a scope with the job's tenant, the registry and
/// payload checks, the handler with a renewal loop and a fence bound to one <see cref="JobExecutionOwnership"/>, and
/// exactly one outcome write while ownership is still certain.
/// </summary>
/// <remarks>
/// Any unsuccessful renewal makes the execution <see cref="JobOwnershipState.Uncertain"/> and cancels it; from then on
/// nothing is written, the row is left to lease expiry, and recovery follows what the database actually holds (§4.4).
/// An outcome write whose result is unknown is never retried and never followed by a re-read. Logs carry identifiers
/// made safe for logging, database times, the exception type chain, and provider error codes; never a payload, an
/// exception message, or a stack trace (D-16a).
/// </remarks>
public sealed class JobExecutor(
    IServiceScopeFactory scopeFactory,
    IJobLeaseRepository leaseRepository,
    IJobFenceFactory fenceFactory,
    IJobHandlerRegistry registry,
    IJobErrorCodeRegistry errorCodes,
    IOptions<JobOptions> options,
    JobRuntimeEnvironment environment,
    TimeProvider timeProvider,
    JobMetrics metrics,
    ILogger<JobExecutor> logger
)
{
    internal static readonly EventId JobClaimed = new(1437_01, nameof(JobClaimed));
    internal static readonly EventId JobReclaimed = new(1437_02, nameof(JobReclaimed));
    internal static readonly EventId JobCompleted = new(1437_03, nameof(JobCompleted));
    internal static readonly EventId JobRetryScheduled = new(1437_04, nameof(JobRetryScheduled));
    internal static readonly EventId JobFailed = new(1437_05, nameof(JobFailed));
    internal static readonly EventId OwnershipUncertainExit = new(1437_06, nameof(OwnershipUncertainExit));
    internal static readonly EventId LateWriteRejected = new(1437_07, nameof(LateWriteRejected));
    internal static readonly EventId WriteOutcomeUnknown = new(1437_08, nameof(WriteOutcomeUnknown));
    internal static readonly EventId JobReleasedOnShutdown = new(1437_09, nameof(JobReleasedOnShutdown));

    private JobOptions Settings => options.Value;

    private TimeProvider Clock => timeProvider;

    private IJobLeaseRepository Leases => leaseRepository;

    /// <summary>
    /// Runs <paramref name="job"/> to one outcome. <paramref name="stoppingToken"/> is the host's stop signal: when it
    /// fires while the execution still owns the job, the job is released back to <c>Pending</c> with its attempt kept.
    /// </summary>
    public async Task<JobExecutionResult> ExecuteAsync(ClaimedJob job, CancellationToken stoppingToken)
    {
        long started = timeProvider.GetTimestamp();
        LogClaim(job);
        metrics.JobClaimed(job.JobType, job.Reclaimed, QueueDelayMilliseconds(job));

        JobExecutionOwnership ownership = new();
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        using CancellationTokenSource execution = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken
        );

        Decision decision = await PrepareAsync(job, scope.ServiceProvider);
        if (decision is Decision.Run run)
        {
            RenewalLoop renewal = new(this, job, ownership, execution);
            renewal.Start();
            try
            {
                decision = await RunHandlerAsync(
                    job,
                    run,
                    scope.ServiceProvider,
                    ownership,
                    execution.Token,
                    stoppingToken
                );
            }
            finally
            {
                // An in-flight renewal completes, within RenewalTimeout, before the outcome is decided (D-7a).
                await renewal.StopAsync();
            }
        }

        JobExecutionResult result = await FinalizeAsync(job, ownership, decision, started);
        metrics.JobFinished(job.JobType, result.Outcome, DurationMilliseconds(started));
        if (result.Outcome == JobExecutionOutcome.OwnershipUncertain)
        {
            metrics.OwnershipUncertain(job.JobType, result.Reason ?? "Unknown");
        }

        return result;
    }

    /// <summary>Steps 1 and 2 of §4.3: tenant, registry, and payload, before any handler is resolved.</summary>
    private async Task<Decision> PrepareAsync(ClaimedJob job, IServiceProvider services)
    {
        TenantContext tenant;
        try
        {
            if (await ResolveTenantAsync(job, services) is not { } resolved)
            {
                return new Decision.Terminal(JobErrorCode.TenantUnavailable, null);
            }

            tenant = resolved;
        }
        catch (Exception exception)
        {
            return new Decision.Transient(JobDiagnostics.From(exception, "ResolveTenant"));
        }

        services.GetRequiredService<ITenantContextProvider>().Context = tenant;

        if (!registry.TryGet(job.JobType, out JobHandlerRegistration? registration))
        {
            return new Decision.Terminal(JobErrorCode.UnsupportedJobType, null);
        }

        if (!registration.PayloadVersions.Contains(job.PayloadVersion))
        {
            return new Decision.Terminal(JobErrorCode.UnsupportedPayloadVersion, null);
        }

        return registration.CheckPayload(services, job.PayloadJson) switch
        {
            JobPayloadCheck.Valid valid => new Decision.Run(registration, valid.Payload, tenant),
            _ => new Decision.Terminal(JobErrorCode.InvalidPayload, null),
        };
    }

    /// <summary>
    /// D-11: a job without a tenant runs single-tenant only when the service is not multi-tenant; a job with a tenant
    /// runs only when the service is multi-tenant and the tenant exists. Returns null for every other case.
    /// </summary>
    private async Task<TenantContext?> ResolveTenantAsync(ClaimedJob job, IServiceProvider services)
    {
        switch (job.TenantId, environment.MultiTenancy)
        {
            case (null, false):
                return new TenantContext.NotMultitenant();
            case ({ } tenantId, true):
                return await services.GetRequiredService<ITenantRepository>().GetTenant(tenantId) switch
                {
                    TenantGetResult.Success found => new TenantContext.Multitenant(
                        found.TenantResponse.Id,
                        found.TenantResponse.Name
                    ),
                    TenantGetResult.FailureNotFound => null,
                    _ => throw new InvalidOperationException("The tenant lookup failed."),
                };
            default:
                return null;
        }
    }

    /// <summary>Steps 3 and 4 of §4.3, and the D-7 classification of how the handler ended.</summary>
    private async Task<Decision> RunHandlerAsync(
        ClaimedJob job,
        Decision.Run run,
        IServiceProvider services,
        JobExecutionOwnership ownership,
        CancellationToken executionToken,
        CancellationToken stoppingToken
    )
    {
        JobExecutionContext context = new(
            job.JobId,
            run.Tenant,
            job.AttemptCount,
            Settings.MaxAttempts,
            fenceFactory.Create(job, ownership)
        );

        try
        {
            await run.Registration.Execute(services, context, run.Payload, executionToken);
            return new Decision.Complete();
        }
        catch (JobPermanentException permanent)
        {
            JobErrorCode errorCode = errorCodes.TryGet(permanent.ErrorCode.Code, out JobErrorCode? registered)
                ? registered
                : JobErrorCode.HandlerFailed;
            return new Decision.Terminal(errorCode, JobDiagnostics.From(permanent, "Handler"));
        }
        catch (JobLeaseLostException lost)
        {
            ownership.TryMarkUncertain("FenceLeaseLost");
            return new Decision.Transient(JobDiagnostics.From(lost, "Fence"));
        }
        catch (OperationCanceledException canceled)
            when (stoppingToken.IsCancellationRequested && ownership.State == JobOwnershipState.Owned)
        {
            return new Decision.Release(JobDiagnostics.From(canceled, "Handler"));
        }
        catch (Exception exception)
        {
            return new Decision.Transient(JobDiagnostics.From(exception, "Handler"));
        }
    }

    /// <summary>
    /// Step 5 of §4.3 (D-7a): under the gate, exactly one outcome write while the execution is still
    /// <see cref="JobOwnershipState.Owned"/>, and none once it is uncertain.
    /// </summary>
    private async Task<JobExecutionResult> FinalizeAsync(
        ClaimedJob job,
        JobExecutionOwnership ownership,
        Decision decision,
        long started
    )
    {
        using IDisposable gate = await ownership.EnterAsync(CancellationToken.None);
        if (!ownership.TryBeginFinalizing())
        {
            LogUncertainExit(job, ownership.Reason, started, decision.Diagnostic);
            return new JobExecutionResult(JobExecutionOutcome.OwnershipUncertain, ownership.Reason);
        }

        (Func<Task<JobWriteResult>> write, JobExecutionResult ifWritten, EventId written) = OutcomeWrite(
            job,
            decision
        );

        JobWriteResult result;
        try
        {
            result = await write();
        }
        catch (Exception exception)
        {
            result = new JobWriteResult.ResultUnknown(JobDiagnostics.From(exception, "OutcomeWrite"));
        }

        switch (result)
        {
            case JobWriteResult.Success:
                ownership.TryMarkFinalized();
                LogOutcome(written, job, ifWritten, started, decision.Diagnostic);
                return ifWritten;
            case JobWriteResult.OwnershipLost:
                ownership.TryMarkUncertain(nameof(LateWriteRejected));
                LogOutcome(LateWriteRejected, job, ifWritten, started, decision.Diagnostic);
                return new JobExecutionResult(
                    JobExecutionOutcome.LateWriteRejected,
                    nameof(LateWriteRejected)
                );
            case JobWriteResult.ResultUnknown unknown:
                ownership.TryMarkUncertain(nameof(WriteOutcomeUnknown));
                LogWriteUnknown(job, nameof(WriteOutcomeUnknown), started, unknown.Diagnostic);
                return new JobExecutionResult(
                    JobExecutionOutcome.OwnershipUncertain,
                    nameof(WriteOutcomeUnknown)
                );
            case JobWriteResult.FailureUnknown failed:
                // Nothing was written: the row keeps its lease until expiry and is then reclaimed or exhausted.
                ownership.TryMarkUncertain("WriteFailed");
                LogWriteUnknown(job, "WriteFailed", started, failed.Diagnostic);
                return new JobExecutionResult(JobExecutionOutcome.OwnershipUncertain, "WriteFailed");
            default:
                throw new InvalidOperationException("The outcome write returned an unknown result.");
        }
    }

    /// <summary>The one D-7 outcome write for a decision, the result it records, and the event it logs.</summary>
    private (Func<Task<JobWriteResult>> Write, JobExecutionResult Result, EventId Event) OutcomeWrite(
        ClaimedJob job,
        Decision decision
    )
    {
        string owner = job.LeaseOwner;
        long token = job.FencingToken;
        return decision switch
        {
            Decision.Complete => (
                () => leaseRepository.Complete(job.Id, owner, token, CancellationToken.None),
                new JobExecutionResult(JobExecutionOutcome.Completed),
                JobCompleted
            ),
            Decision.Terminal terminal => Terminal(terminal.ErrorCode),
            Decision.Transient when job.AttemptCount >= Settings.MaxAttempts => Terminal(
                JobErrorCode.AttemptsExhausted
            ),
            Decision.Transient => (
                () =>
                    leaseRepository.FailTransient(
                        job.Id,
                        owner,
                        token,
                        RetryBackoff.SecondsFor(
                            job.AttemptCount,
                            Settings.RetryBackoffBase,
                            Settings.RetryBackoffMaximum
                        ),
                        CancellationToken.None
                    ),
                new JobExecutionResult(JobExecutionOutcome.RetryScheduled),
                JobRetryScheduled
            ),
            Decision.Release => (
                () => leaseRepository.ReleaseToPending(job.Id, owner, token, CancellationToken.None),
                new JobExecutionResult(JobExecutionOutcome.ReleasedOnShutdown),
                JobReleasedOnShutdown
            ),
            _ => throw new InvalidOperationException(
                "The execution reached finalization without a decision."
            ),
        };

        (Func<Task<JobWriteResult>>, JobExecutionResult, EventId) Terminal(JobErrorCode errorCode) =>
            (
                () => leaseRepository.FailTerminal(job.Id, owner, token, errorCode, CancellationToken.None),
                new JobExecutionResult(JobExecutionOutcome.Failed, ErrorCode: errorCode.Code),
                JobFailed
            );
    }

    private static double QueueDelayMilliseconds(ClaimedJob job) =>
        Math.Max(0, (job.DatabaseUtcNow - job.NextAttemptAt).TotalMilliseconds);

    private double DurationMilliseconds(long started) =>
        timeProvider.GetElapsedTime(started).TotalMilliseconds;

    private void LogClaim(ClaimedJob job) =>
        logger.LogInformation(
            job.Reclaimed ? JobReclaimed : JobClaimed,
            "{Event} JobId={JobId} JobType={JobType} TenantId={TenantId} Attempt={Attempt} QueueDelayMs={QueueDelayMs} LeaseOwner={LeaseOwner} FencingToken={FencingToken}",
            job.Reclaimed ? nameof(JobReclaimed) : nameof(JobClaimed),
            JobDiagnostics.SafeIdentifier(job.JobId),
            JobDiagnostics.SafeIdentifier(job.JobType),
            job.TenantId,
            job.AttemptCount,
            QueueDelayMilliseconds(job),
            JobDiagnostics.SafeIdentifier(job.LeaseOwner),
            job.FencingToken
        );

    private void LogOutcome(
        EventId eventId,
        ClaimedJob job,
        JobExecutionResult result,
        long started,
        JobFailureDiagnostic? diagnostic
    ) =>
        logger.Log(
            eventId == JobCompleted || eventId == JobReleasedOnShutdown
                ? LogLevel.Information
                : LogLevel.Warning,
            eventId,
            "{Event} JobId={JobId} JobType={JobType} TenantId={TenantId} Attempt={Attempt} DurationMs={DurationMs} Outcome={Outcome} LeaseOwner={LeaseOwner} FencingToken={FencingToken} JobErrorCode={JobErrorCode} ExceptionTypeChain={ExceptionTypeChain} ProviderErrorCode={ProviderErrorCode} Operation={Operation}",
            eventId.Name,
            JobDiagnostics.SafeIdentifier(job.JobId),
            JobDiagnostics.SafeIdentifier(job.JobType),
            job.TenantId,
            job.AttemptCount,
            DurationMilliseconds(started),
            result.Outcome,
            JobDiagnostics.SafeIdentifier(job.LeaseOwner),
            job.FencingToken,
            result.ErrorCode,
            diagnostic?.ExceptionTypeChain,
            diagnostic?.ProviderErrorCode,
            diagnostic?.Operation
        );

    private void LogUncertainExit(
        ClaimedJob job,
        string? reason,
        long started,
        JobFailureDiagnostic? diagnostic
    ) =>
        logger.LogWarning(
            OwnershipUncertainExit,
            "{Event} JobId={JobId} JobType={JobType} TenantId={TenantId} Attempt={Attempt} DurationMs={DurationMs} Outcome={Outcome} Reason={Reason} LeaseOwner={LeaseOwner} FencingToken={FencingToken} ExceptionTypeChain={ExceptionTypeChain} ProviderErrorCode={ProviderErrorCode} Operation={Operation}",
            nameof(OwnershipUncertainExit),
            JobDiagnostics.SafeIdentifier(job.JobId),
            JobDiagnostics.SafeIdentifier(job.JobType),
            job.TenantId,
            job.AttemptCount,
            DurationMilliseconds(started),
            JobExecutionOutcome.OwnershipUncertain,
            reason,
            JobDiagnostics.SafeIdentifier(job.LeaseOwner),
            job.FencingToken,
            diagnostic?.ExceptionTypeChain,
            diagnostic?.ProviderErrorCode,
            diagnostic?.Operation
        );

    private void LogWriteUnknown(
        ClaimedJob job,
        string reason,
        long started,
        JobFailureDiagnostic diagnostic
    ) =>
        logger.LogWarning(
            WriteOutcomeUnknown,
            "{Event} JobId={JobId} JobType={JobType} TenantId={TenantId} Attempt={Attempt} DurationMs={DurationMs} Outcome={Outcome} Reason={Reason} LeaseOwner={LeaseOwner} FencingToken={FencingToken} ExceptionTypeChain={ExceptionTypeChain} ProviderErrorCode={ProviderErrorCode} Operation={Operation}",
            nameof(WriteOutcomeUnknown),
            JobDiagnostics.SafeIdentifier(job.JobId),
            JobDiagnostics.SafeIdentifier(job.JobType),
            job.TenantId,
            job.AttemptCount,
            DurationMilliseconds(started),
            JobExecutionOutcome.OwnershipUncertain,
            reason,
            JobDiagnostics.SafeIdentifier(job.LeaseOwner),
            job.FencingToken,
            diagnostic.ExceptionTypeChain,
            diagnostic.ProviderErrorCode,
            diagnostic.Operation
        );

    /// <summary>What the execution decided before finalization, with the diagnostic of any failure.</summary>
    private abstract record Decision(JobFailureDiagnostic? Diagnostic)
    {
        public sealed record Run(JobHandlerRegistration Registration, object Payload, TenantContext Tenant)
            : Decision((JobFailureDiagnostic?)null);

        public sealed record Complete() : Decision((JobFailureDiagnostic?)null);

        public sealed record Terminal(JobErrorCode ErrorCode, JobFailureDiagnostic? Failure)
            : Decision(Failure);

        public sealed record Transient(JobFailureDiagnostic Failure) : Decision(Failure);

        public sealed record Release(JobFailureDiagnostic Failure) : Decision(Failure);
    }

    /// <summary>
    /// The D-7a renewal loop: every <see cref="JobOptions.RenewalInterval"/> it enters the gate ahead of fences and
    /// renews the lease; any unsuccessful renewal makes the execution uncertain and cancels it. Stopping it lets an
    /// in-flight renewal finish, and only a failed renewal changes the state.
    /// </summary>
    private sealed class RenewalLoop(
        JobExecutor executor,
        ClaimedJob job,
        JobExecutionOwnership ownership,
        CancellationTokenSource execution
    )
    {
        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;

        public void Start() => _loop = RunAsync();

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            await _loop;
            _stop.Dispose();
        }

        private async Task RunAsync()
        {
            JobOptions settings = executor.Settings;
            while (true)
            {
                try
                {
                    await Task.Delay(settings.RenewalInterval, executor.Clock, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                IDisposable gate;
                try
                {
                    gate = await ownership.EnterForRenewalAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                using (gate)
                {
                    if (ownership.State != JobOwnershipState.Owned)
                    {
                        return;
                    }

                    if (await RenewOnceAsync(settings) is not { } failure)
                    {
                        continue;
                    }

                    // Under the gate, so no fence can start or commit between the failure and the state change.
                    ownership.TryMarkUncertain(failure);
                }

                // The gate is released before the consumer's cancellation callbacks run: a callback may wait for a
                // fence, which needs the gate. The state is already uncertain, so such a fence is rejected before it
                // reaches the database.
                try
                {
                    await execution.CancelAsync();
                }
                catch (AggregateException)
                {
                    // A handler's cancellation callback failed; the execution is cancelled all the same.
                }

                return;
            }
        }

        /// <summary>One renewal, bounded by <see cref="JobOptions.RenewalTimeout"/>. Returns the failure reason, or null.</summary>
        private async Task<string?> RenewOnceAsync(JobOptions settings)
        {
            Task<JobWriteResult> renewal;
            try
            {
                // Inside the boundary: a repository that throws before returning a task is a failed renewal too.
                renewal = executor.Leases.Renew(
                    job.Id,
                    job.LeaseOwner,
                    job.FencingToken,
                    (int)settings.LeaseDuration.TotalSeconds,
                    CancellationToken.None
                );
            }
            catch (Exception)
            {
                return "RenewalException";
            }

            try
            {
                return await renewal.WaitAsync(settings.RenewalTimeout, executor.Clock) switch
                {
                    JobWriteResult.Success => null,
                    JobWriteResult.OwnershipLost => "RenewalOwnershipLost",
                    JobWriteResult.ResultUnknown => "RenewalResultUnknown",
                    _ => "RenewalFailureUnknown",
                };
            }
            catch (TimeoutException)
            {
                // The abandoned renewal still ends within its own deadline; observe it so it is never unobserved.
                _ = renewal.ContinueWith(
                    static abandoned => abandoned.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default
                );
                return "RenewalTimeout";
            }
            catch (Exception)
            {
                return "RenewalException";
            }
        }
    }
}
