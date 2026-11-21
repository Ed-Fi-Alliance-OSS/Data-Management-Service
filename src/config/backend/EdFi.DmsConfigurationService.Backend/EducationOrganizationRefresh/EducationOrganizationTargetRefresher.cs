// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;

/// <summary>Why a refresh no longer held its target lease.</summary>
public enum TargetLeaseLossReason
{
    /// <summary>The lease was still recorded for this execution, but it had expired by database time.</summary>
    Expired,

    /// <summary>Another execution held the lease, or it was released or published.</summary>
    Taken,

    /// <summary>A renewal failed, timed out, or ended with an unknown result, so ownership was not certain.</summary>
    RenewalUncertain,
}

/// <summary>How the refresh of one target ended.</summary>
public abstract record TargetRefreshOutcome
{
    private TargetRefreshOutcome() { }

    /// <summary>The read organizations were published as the target's active generation.</summary>
    public sealed record Refreshed(long Generation, int ItemCount) : TargetRefreshOutcome;

    /// <summary>The data store does not exist in the current tenant, or was deleted during the refresh.</summary>
    public sealed record Removed : TargetRefreshOutcome;

    /// <summary>Another execution held a live lease on the target; nothing was read.</summary>
    public sealed record Busy : TargetRefreshOutcome;

    /// <summary>The lease was lost before publication; the previous snapshot was kept.</summary>
    public sealed record LeaseLost(TargetLeaseLossReason Reason) : TargetRefreshOutcome;

    /// <summary>The projection read failed; the previous snapshot was kept.</summary>
    public sealed record Failed(EducationOrganizationProjectionFailure Failure) : TargetRefreshOutcome;

    /// <summary>A Configuration Service database operation failed; the previous snapshot was kept.</summary>
    public sealed record DbFailure(JobFailureDiagnostic Diagnostic) : TargetRefreshOutcome;
}

/// <summary>
/// Refreshes the education organization snapshot of one ordinary data store under its target lease (DMS-1441 spec
/// §6.2): acquire, reclaim, admission renewal, read with a renewal loop, pre-stage renewal, stage, activation inside
/// the job fence, then a best-effort prune.
/// </summary>
/// <remarks>
/// <para>
/// The admission renewal is the last operation before the reader is called; nothing is awaited between them. While
/// the read runs, a loop renews the lease every <c>JobSettings:RenewalInterval</c>; a renewal that fails, times out
/// after <c>RenewalInterval / 2</c>, or ends with an unknown result cancels only this target's read, and the outcome
/// is <see cref="TargetRefreshOutcome.LeaseLost"/>. Cancellation of the caller's token propagates. Once the read
/// returns, the loop is stopped and awaited, so no renewal overlaps staging.
/// </para>
/// <para>
/// Every exit after a successful acquisition that did not publish releases the lease, best effort, with
/// <see cref="CancellationToken.None"/> and a 5 second bound. Prune and release failures are logged and never change
/// the outcome. Logs carry identifiers, counts, sanitized values and diagnostics only: never contexts, organization
/// values, the execution token, or an exception.
/// </para>
/// </remarks>
public sealed class EducationOrganizationTargetRefresher(
    IEducationOrganizationSnapshotRepository repository,
    IEducationOrganizationProjectionReader reader,
    IOptions<JobOptions> jobOptions,
    TimeProvider timeProvider,
    ILogger<EducationOrganizationTargetRefresher> logger
)
{
    internal static readonly EventId TargetRefreshed = new(1441_01, "EducationOrganizationTargetRefreshed");
    internal static readonly EventId TargetBusy = new(1441_02, "EducationOrganizationTargetBusy");
    internal static readonly EventId TargetLeaseLost = new(1441_03, "EducationOrganizationTargetLeaseLost");
    internal static readonly EventId TargetRemoved = new(1441_04, "EducationOrganizationTargetRemoved");
    internal static readonly EventId TargetRefreshFailed = new(
        1441_05,
        "EducationOrganizationTargetRefreshFailed"
    );
    internal static readonly EventId SnapshotPruneFailed = new(
        1441_06,
        "EducationOrganizationSnapshotPruneFailed"
    );
    internal static readonly EventId LeaseReleaseFailed = new(
        1441_07,
        "EducationOrganizationLeaseReleaseFailed"
    );

    /// <summary>The bound of the best-effort release.</summary>
    internal static TimeSpan ReleaseTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Test-only pause after acquisition, before reclaim. Null in production, so nothing is awaited.</summary>
    internal Func<EducationOrganizationSnapshotLease, CancellationToken, Task>? AfterAcquire { get; set; }

    /// <summary>Test-only pause after reclaim, before admission. Null in production, so nothing is awaited.</summary>
    internal Func<EducationOrganizationSnapshotLease, CancellationToken, Task>? AfterReclaim { get; set; }

    /// <summary>
    /// Test-only pause after the admission renewal, before the read: it models an execution delay under the
    /// documented failure model. Null in production, so nothing is awaited between admission and the read.
    /// </summary>
    internal Func<EducationOrganizationSnapshotLease, CancellationToken, Task>? AfterAdmission { get; set; }

    private JobOptions Settings => jobOptions.Value;

    private TimeProvider Clock => timeProvider;

    private ILogger Logger => logger;

    /// <summary>
    /// Refreshes <paramref name="target"/> in the tenant of <paramref name="context"/>, activating inside its fence.
    /// Throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled, and
    /// lets <see cref="JobLeaseLostException"/> and <see cref="JobFenceUnavailableException"/> from the fence
    /// propagate, each after the best-effort release.
    /// </summary>
    public async Task<TargetRefreshOutcome> RefreshAsync(
        JobExecutionContext context,
        EducationOrganizationRefreshTarget target,
        CancellationToken cancellationToken
    )
    {
        TenantContext.Multitenant? tenant = context.TenantContext as TenantContext.Multitenant;
        TargetLog log = new(this, tenant?.TenantId, target.DataStoreId);
        string executionToken = Guid.NewGuid().ToString("N");

        SnapshotAcquireResult acquired = await repository.AcquireAsync(
            target.DataStoreId,
            executionToken,
            cancellationToken
        );
        EducationOrganizationSnapshotLease lease;
        switch (acquired)
        {
            case SnapshotAcquireResult.Acquired { Lease: var acquiredLease }:
                lease = acquiredLease;
                break;
            case SnapshotAcquireResult.Busy:
                return log.Report(new TargetRefreshOutcome.Busy(), "Acquire");
            case SnapshotAcquireResult.DataStoreNotFound:
                return log.Report(new TargetRefreshOutcome.Removed(), "Acquire");
            case SnapshotAcquireResult.DbFailure { Diagnostic: var diagnostic }:
                return log.Report(new TargetRefreshOutcome.DbFailure(diagnostic), "Acquire");
            default:
                throw new InvalidOperationException("Unknown acquisition result.");
        }

        TargetRefreshOutcome? outcome = null;
        try
        {
            outcome = await PublishAsync(
                context.Fence,
                new EducationOrganizationProjectionReadRequest(
                    tenant?.TenantName,
                    target.DataStoreId,
                    target.Contexts
                ),
                lease,
                log,
                cancellationToken
            );
        }
        finally
        {
            if (outcome is not TargetRefreshOutcome.Refreshed)
            {
                await ReleaseAsync(lease, log);
            }
        }

        if (outcome is TargetRefreshOutcome.Refreshed)
        {
            await PruneAsync(target.DataStoreId, log, cancellationToken);
        }
        return outcome;
    }

    /// <summary>Every step from reclaim to activation, under the acquired lease.</summary>
    private async Task<TargetRefreshOutcome> PublishAsync(
        IJobFence fence,
        EducationOrganizationProjectionReadRequest request,
        EducationOrganizationSnapshotLease lease,
        TargetLog log,
        CancellationToken cancellationToken
    )
    {
        if (AfterAcquire is { } afterAcquire)
        {
            await afterAcquire(lease, cancellationToken);
        }

        if (Stopped(await repository.ReclaimAsync(lease, cancellationToken)) is { } reclaimStop)
        {
            return log.Report(reclaimStop, "Reclaim");
        }

        if (AfterReclaim is { } afterReclaim)
        {
            await afterReclaim(lease, cancellationToken);
        }

        if (await RenewAsync(lease, cancellationToken) is { } admissionStop)
        {
            return log.Report(admissionStop, "Admission");
        }

        if (AfterAdmission is { } afterAdmission)
        {
            await afterAdmission(lease, cancellationToken);
        }

        // From the admission renewal to the reader call nothing is awaited: starting the loop only arms its timer.
        EducationOrganizationProjectionReadResult? read = null;
        TargetRefreshOutcome? loss;
        using (
            CancellationTokenSource targetCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            )
        )
        {
            RenewalLoop loop = new(this, lease, targetCancellation);
            loop.Start();
            try
            {
                read = await reader.ReadAllAsync(request, targetCancellation.Token);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested && targetCancellation.IsCancellationRequested
                )
            {
                // Only the renewal loop cancels the target's token without the caller's: the lease was lost.
            }
            finally
            {
                await loop.StopAsync();
            }
            loss = loop.Loss;
        }

        if (loss is not null || read is null)
        {
            return log.Report(
                loss ?? new TargetRefreshOutcome.LeaseLost(TargetLeaseLossReason.RenewalUncertain),
                "Read"
            );
        }

        if (read is EducationOrganizationProjectionReadResult.Failure { Detail: var failure })
        {
            log.ReadFailed(failure);
            return new TargetRefreshOutcome.Failed(failure);
        }
        var success = (EducationOrganizationProjectionReadResult.Success)read;

        if (await RenewAsync(lease, cancellationToken) is { } preStageStop)
        {
            return log.Report(preStageStop, "PreStage");
        }

        SnapshotTargetResult staged = await repository.StageAsync(
            lease,
            success.Items,
            success.ContractVersion,
            cancellationToken
        );
        if (Stopped(staged) is { } stageStop)
        {
            return log.Report(stageStop, "Stage");
        }

        SnapshotTargetResult? activation = null;
        try
        {
            await fence.ExecuteAsync(
                async (transaction, token) =>
                    activation = await repository.ActivateAsync(
                        transaction,
                        lease,
                        success.ContractVersion,
                        token
                    ),
                cancellationToken
            );
        }
        catch (SnapshotStoreException exception)
        {
            return log.Report(new TargetRefreshOutcome.DbFailure(exception.Diagnostic), "Activate");
        }

        if (
            Stopped(
                activation ?? throw new InvalidOperationException("The fence did not run the activation.")
            ) is
            { } activateStop
        )
        {
            return log.Report(activateStop, "Activate");
        }

        log.Refreshed(lease.Generation, success);
        return new TargetRefreshOutcome.Refreshed(lease.Generation, success.Items.Count);
    }

    /// <summary>The outcome that stops the refresh after a reclaim, stage or activation, or null on success.</summary>
    private static TargetRefreshOutcome? Stopped(SnapshotTargetResult result) =>
        result switch
        {
            SnapshotTargetResult.Success => null,
            SnapshotTargetResult.LeaseLost { Reason: var reason } => new TargetRefreshOutcome.LeaseLost(
                LossReason(reason)
            ),
            SnapshotTargetResult.DataStoreRemoved => new TargetRefreshOutcome.Removed(),
            SnapshotTargetResult.DbFailure { Diagnostic: var diagnostic } =>
                new TargetRefreshOutcome.DbFailure(diagnostic),
            _ => throw new InvalidOperationException("Unknown target result."),
        };

    private static TargetLeaseLossReason LossReason(SnapshotLeaseLossReason reason) =>
        reason == SnapshotLeaseLossReason.Expired
            ? TargetLeaseLossReason.Expired
            : TargetLeaseLossReason.Taken;

    /// <summary>
    /// One strict renewal (§6.2 step 4), bounded by <c>RenewalInterval / 2</c>. Returns null when renewed, otherwise
    /// the outcome that stops the refresh: a failure, timeout or unknown result is
    /// <see cref="TargetLeaseLossReason.RenewalUncertain"/>. Cancellation of <paramref name="cancellationToken"/>
    /// stops the wait and propagates; the renewal itself always runs to its own database bound.
    /// </summary>
    private async Task<TargetRefreshOutcome?> RenewAsync(
        EducationOrganizationSnapshotLease lease,
        CancellationToken cancellationToken
    )
    {
        Task<SnapshotTargetResult> renewal;
        try
        {
            renewal = repository.RenewAsync(lease, CancellationToken.None);
        }
        catch (Exception)
        {
            return Uncertain();
        }

        try
        {
            return await renewal.WaitAsync(Settings.RenewalTimeout, Clock, cancellationToken) switch
            {
                SnapshotTargetResult.Success => null,
                SnapshotTargetResult.LeaseLost { Reason: var reason } => new TargetRefreshOutcome.LeaseLost(
                    LossReason(reason)
                ),
                SnapshotTargetResult.DataStoreRemoved => new TargetRefreshOutcome.Removed(),
                _ => Uncertain(),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Observe(renewal);
            throw;
        }
        catch (TimeoutException)
        {
            Observe(renewal);
            return Uncertain();
        }
        catch (Exception)
        {
            return Uncertain();
        }

        static TargetRefreshOutcome Uncertain() =>
            new TargetRefreshOutcome.LeaseLost(TargetLeaseLossReason.RenewalUncertain);
    }

    /// <summary>Best effort; a failure is logged and never changes the outcome.</summary>
    private async Task PruneAsync(int dataStoreId, TargetLog log, CancellationToken cancellationToken)
    {
        try
        {
            if (
                await repository.PruneAsync(dataStoreId, cancellationToken) is SnapshotTargetResult.DbFailure
                {
                    Diagnostic: var diagnostic
                }
            )
            {
                log.MaintenanceFailed(SnapshotPruneFailed, diagnostic);
            }
        }
        catch (Exception exception)
        {
            log.MaintenanceFailed(SnapshotPruneFailed, JobDiagnostics.From(exception, "Prune"));
        }
    }

    /// <summary>
    /// Best effort, with <see cref="CancellationToken.None"/> and bounded by <see cref="ReleaseTimeout"/>; a failure
    /// is logged and never changes the outcome.
    /// </summary>
    private async Task ReleaseAsync(EducationOrganizationSnapshotLease lease, TargetLog log)
    {
        Task<SnapshotTargetResult> release;
        try
        {
            release = repository.ReleaseAsync(lease, CancellationToken.None);
        }
        catch (Exception exception)
        {
            log.MaintenanceFailed(LeaseReleaseFailed, JobDiagnostics.From(exception, "Release"));
            return;
        }

        try
        {
            if (
                await release.WaitAsync(ReleaseTimeout, Clock) is SnapshotTargetResult.DbFailure
                {
                    Diagnostic: var diagnostic
                }
            )
            {
                log.MaintenanceFailed(LeaseReleaseFailed, diagnostic);
            }
        }
        catch (TimeoutException exception)
        {
            Observe(release);
            log.MaintenanceFailed(LeaseReleaseFailed, JobDiagnostics.From(exception, "Release"));
        }
        catch (Exception exception)
        {
            log.MaintenanceFailed(LeaseReleaseFailed, JobDiagnostics.From(exception, "Release"));
        }
    }

    /// <summary>An abandoned operation still ends within its own database bound; observe it so it is never unobserved.</summary>
    private static void Observe(Task abandoned) =>
        _ = abandoned.ContinueWith(
            static task => task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default
        );

    /// <summary>
    /// Renews the lease every <c>RenewalInterval</c> while the read runs. The first renewal that does not succeed is
    /// recorded as <see cref="Loss"/> and cancels the target's read. Stopping lets an in-flight renewal finish.
    /// </summary>
    private sealed class RenewalLoop(
        EducationOrganizationTargetRefresher refresher,
        EducationOrganizationSnapshotLease lease,
        CancellationTokenSource targetCancellation
    )
    {
        // Armed at construction, so a tick that falls due while a renewal runs is kept for the next wait.
        private readonly PeriodicTimer _timer = new(refresher.Settings.RenewalInterval, refresher.Clock);
        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;

        /// <summary>The outcome of the first renewal that did not succeed; read only after <see cref="StopAsync"/>.</summary>
        public TargetRefreshOutcome? Loss { get; private set; }

        public void Start() => _loop = RunAsync();

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            await _loop;
            _timer.Dispose();
            _stop.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                while (await _timer.WaitForNextTickAsync(_stop.Token))
                {
                    // A tick that fell due before the stop must not start another renewal.
                    if (_stop.IsCancellationRequested)
                    {
                        return;
                    }

                    if (await refresher.RenewAsync(lease, CancellationToken.None) is { } loss)
                    {
                        Loss = loss;
                        await targetCancellation.CancelAsync();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                // Stopped while waiting for the next tick.
            }
        }
    }

    /// <summary>The log entries of one target refresh.</summary>
    private sealed class TargetLog(
        EducationOrganizationTargetRefresher refresher,
        long? tenantId,
        int dataStoreId
    )
    {
        private ILogger Logger => refresher.Logger;

        /// <summary>Logs a non-published outcome of a lease or database step, and returns it.</summary>
        public TargetRefreshOutcome Report(TargetRefreshOutcome outcome, string operation)
        {
            switch (outcome)
            {
                case TargetRefreshOutcome.Busy:
                    Logger.LogInformation(
                        TargetBusy,
                        "{Event} TenantId={TenantId} DataStoreId={DataStoreId}",
                        TargetBusy.Name,
                        tenantId,
                        dataStoreId
                    );
                    break;
                case TargetRefreshOutcome.Removed:
                    Logger.LogInformation(
                        TargetRemoved,
                        "{Event} TenantId={TenantId} DataStoreId={DataStoreId} Operation={Operation}",
                        TargetRemoved.Name,
                        tenantId,
                        dataStoreId,
                        operation
                    );
                    break;
                case TargetRefreshOutcome.LeaseLost { Reason: var reason }:
                    Logger.LogWarning(
                        TargetLeaseLost,
                        "{Event} TenantId={TenantId} DataStoreId={DataStoreId} Operation={Operation} Reason={Reason}",
                        TargetLeaseLost.Name,
                        tenantId,
                        dataStoreId,
                        operation,
                        reason.ToString()
                    );
                    break;
                case TargetRefreshOutcome.DbFailure { Diagnostic: var diagnostic }:
                    Logger.LogWarning(
                        TargetRefreshFailed,
                        "{Event} TenantId={TenantId} DataStoreId={DataStoreId} Category={Category} Operation={Operation} ExceptionTypeChain={ExceptionTypeChain} ProviderErrorCode={ProviderErrorCode} FailedOperation={FailedOperation}",
                        TargetRefreshFailed.Name,
                        tenantId,
                        dataStoreId,
                        nameof(EducationOrganizationProjectionFailureCategory.Transient),
                        operation,
                        diagnostic.ExceptionTypeChain,
                        diagnostic.ProviderErrorCode,
                        diagnostic.Operation
                    );
                    break;
            }
            return outcome;
        }

        public void ReadFailed(EducationOrganizationProjectionFailure failure) =>
            Logger.LogWarning(
                TargetRefreshFailed,
                "{Event} TenantId={TenantId} DataStoreId={DataStoreId} Category={Category} Code={Code} Stage={Stage} HttpStatus={HttpStatus} ProblemType={ProblemType} CorrelationId={CorrelationId} PagesRead={PagesRead}",
                TargetRefreshFailed.Name,
                tenantId,
                dataStoreId,
                failure.Category.ToString(),
                failure.Code.ToString(),
                failure.Stage.ToString(),
                failure.HttpStatus,
                failure.ProblemType,
                failure.CorrelationId,
                failure.PagesRead
            );

        public void Refreshed(long generation, EducationOrganizationProjectionReadResult.Success read) =>
            Logger.LogInformation(
                TargetRefreshed,
                "{Event} TenantId={TenantId} DataStoreId={DataStoreId} Generation={Generation} ItemCount={ItemCount} ContractVersion={ContractVersion} PageCount={PageCount}",
                TargetRefreshed.Name,
                tenantId,
                dataStoreId,
                generation,
                read.Items.Count,
                JobDiagnostics.SafeIdentifier(read.ContractVersion),
                read.PageCount
            );

        public void MaintenanceFailed(EventId eventId, JobFailureDiagnostic diagnostic) =>
            Logger.LogWarning(
                eventId,
                "{Event} TenantId={TenantId} DataStoreId={DataStoreId} ExceptionTypeChain={ExceptionTypeChain} ProviderErrorCode={ProviderErrorCode} FailedOperation={FailedOperation}",
                eventId.Name,
                tenantId,
                dataStoreId,
                diagnostic.ExceptionTypeChain,
                diagnostic.ProviderErrorCode,
                diagnostic.Operation
            );
    }
}
