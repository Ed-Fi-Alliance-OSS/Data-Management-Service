// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The worker hosted service (spec D-6, §4.4, step 3.4). Each poll it exhausts over-limit jobs, then claims jobs up to
/// <see cref="JobOptions.MaxConcurrentJobs"/> and runs each through <see cref="JobExecutor"/>; it waits for the next
/// poll, or for an execution to finish when it is at capacity. A failure anywhere in the loop is logged by its type
/// chain and retried after <see cref="JobOptions.PollInterval"/>, never faulting the host.
/// </summary>
/// <remarks>
/// On host stop it claims nothing more, the stop signal cancels every execution, and it waits for all of them: an
/// execution that still owns its job releases it to <c>Pending</c> with its attempt kept, and one that is uncertain
/// writes nothing (§4.4). The host's shutdown timeout bounds that wait.
/// </remarks>
public sealed class JobWorkerService(
    IJobLeaseRepository leaseRepository,
    JobExecutor executor,
    IOptions<JobOptions> options,
    TimeProvider timeProvider,
    ILogger<JobWorkerService> logger
) : BackgroundService
{
    internal static readonly EventId JobsExhausted = new(1437_10, nameof(JobsExhausted));
    internal static readonly EventId WorkerPollFailed = new(1437_11, nameof(WorkerPollFailed));

    private readonly Dictionary<long, Task> _running = [];
    private long _nextExecution;

    /// <summary>The lease owner this replica claims as: unique per process, and never longer than 200 characters.</summary>
    public string Owner { get; } = JobLeaseOwner.Create();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        JobOptions settings = options.Value;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Only the type chain is kept; the exception object is never logged (D-16a).
                string? failedWith = null;
                try
                {
                    await PollAsync(settings, stoppingToken);
                    await WaitForNextPollAsync(settings, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    failedWith = JobDiagnostics.TypeChain(exception);
                }

                if (failedWith is not null)
                {
                    logger.LogError(
                        WorkerPollFailed,
                        "{Event} LeaseOwner={LeaseOwner} ExceptionTypeChain={ExceptionTypeChain}",
                        nameof(WorkerPollFailed),
                        JobDiagnostics.SafeIdentifier(Owner),
                        failedWith
                    );
                    await DelayAsync(settings.PollInterval, stoppingToken);
                }
            }
        }
        finally
        {
            // The stop signal has cancelled every execution; each releases or ends uncertain on its own.
            Task[] remaining;
            lock (_running)
            {
                remaining = [.. _running.Values];
            }

            await Task.WhenAll(remaining);
        }
    }

    /// <summary>One poll: an <c>Exhaust</c> sweep, then claims until the queue is empty or the worker is at capacity.</summary>
    private async Task PollAsync(JobOptions settings, CancellationToken stoppingToken)
    {
        switch (
            await leaseRepository.Exhaust(settings.MaxAttempts, JobErrorCode.AttemptsExhausted, stoppingToken)
        )
        {
            case JobExhaustResult.Success { ExhaustedCount: > 0 } exhausted:
                logger.LogWarning(
                    JobsExhausted,
                    "{Event} Count={Count} MaxAttempts={MaxAttempts}",
                    nameof(JobsExhausted),
                    exhausted.ExhaustedCount,
                    settings.MaxAttempts
                );
                break;
            case JobExhaustResult.FailureUnknown failure:
                // Rows over the limit stay where they are until a sweep succeeds; the next poll retries it. Claims
                // are unaffected: a row at the limit is never claimable.
                LogPollFailure(failure.Diagnostic);
                break;
        }

        while (RunningCount < settings.MaxConcurrentJobs && !stoppingToken.IsCancellationRequested)
        {
            JobClaimResult claim = await leaseRepository.ClaimNext(
                Owner,
                (int)settings.LeaseDuration.TotalSeconds,
                settings.MaxAttempts,
                stoppingToken
            );
            if (claim is not JobClaimResult.Claimed claimed)
            {
                if (claim is JobClaimResult.FailureUnknown failure)
                {
                    LogPollFailure(failure.Diagnostic);
                }

                return;
            }

            Start(claimed.Job, stoppingToken);
        }
    }

    /// <summary>A repository call the poll made returned a failure; only its safe diagnostic fields are logged.</summary>
    private void LogPollFailure(JobFailureDiagnostic diagnostic) =>
        logger.LogWarning(
            WorkerPollFailed,
            "{Event} LeaseOwner={LeaseOwner} ExceptionTypeChain={ExceptionTypeChain} ProviderErrorCode={ProviderErrorCode} Operation={Operation}",
            nameof(WorkerPollFailed),
            JobDiagnostics.SafeIdentifier(Owner),
            diagnostic.ExceptionTypeChain,
            diagnostic.ProviderErrorCode,
            diagnostic.Operation
        );

    private int RunningCount
    {
        get
        {
            lock (_running)
            {
                return _running.Count;
            }
        }
    }

    /// <summary>
    /// Starts one execution. It is registered before it can run, so it always leaves the running set when it ends.
    /// </summary>
    private void Start(ClaimedJob job, CancellationToken stoppingToken)
    {
        long key = Interlocked.Increment(ref _nextExecution);
        TaskCompletionSource registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task execution = RunAsync(key, registered.Task, job, stoppingToken);
        lock (_running)
        {
            _running[key] = execution;
        }

        registered.SetResult();
    }

    private async Task RunAsync(long key, Task registered, ClaimedJob job, CancellationToken stoppingToken)
    {
        await registered;
        string? failedWith = null;
        try
        {
            await executor.ExecuteAsync(job, stoppingToken);
        }
        catch (Exception exception)
        {
            // The executor classifies every handler outcome itself; reaching here means the executor failed.
            failedWith = JobDiagnostics.TypeChain(exception);
        }
        finally
        {
            lock (_running)
            {
                _running.Remove(key);
            }
        }

        if (failedWith is not null)
        {
            logger.LogError(
                WorkerPollFailed,
                "{Event} JobId={JobId} LeaseOwner={LeaseOwner} ExceptionTypeChain={ExceptionTypeChain}",
                nameof(WorkerPollFailed),
                JobDiagnostics.SafeIdentifier(job.JobId),
                JobDiagnostics.SafeIdentifier(Owner),
                failedWith
            );
        }
    }

    /// <summary>Waits a poll interval, or less when an execution finishes and frees a slot.</summary>
    private async Task WaitForNextPollAsync(JobOptions settings, CancellationToken stoppingToken)
    {
        Task[] running;
        lock (_running)
        {
            running = [.. _running.Values];
        }

        Task poll = DelayAsync(settings.PollInterval, stoppingToken);
        if (running.Length < settings.MaxConcurrentJobs)
        {
            await poll;
            return;
        }

        await Task.WhenAny(running.Append(poll));
        stoppingToken.ThrowIfCancellationRequested();
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken stoppingToken) =>
        Task.Delay(delay, timeProvider, stoppingToken);
}
