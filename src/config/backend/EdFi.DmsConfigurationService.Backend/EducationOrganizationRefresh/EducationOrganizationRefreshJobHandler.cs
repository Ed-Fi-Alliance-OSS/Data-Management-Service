// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Services;
using Microsoft.Extensions.Logging;
using Codes = EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh.EducationOrganizationRefreshJobErrorCodes;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;

/// <summary>
/// Runs <see cref="EducationOrganizationRefreshJobTypes.Refresh"/>: refreshes every selected
/// ordinary data store of the job's tenant, in identifier order, then decides the job's outcome from all of them.
/// </summary>
/// <remarks>
/// <para>
/// The executor installs the job's persisted tenant before it resolves this handler, so every dependency works in
/// that tenant; nothing here reads an HTTP context. Every selected data store is attempted, whatever happened to the
/// ones before it, and a data store that was published stays published whatever the job's outcome.
/// </para>
/// <para>
/// When every data store refreshed, or was deleted during a refresh of all of them, the job completes. Otherwise, when
/// any failure was transient and attempts remain, <see cref="EducationOrganizationRefreshRetryException"/> retries the
/// job, and the next attempt refreshes every selected data store again. Otherwise the job fails with the single code
/// of a refresh of one data store, or with the <see cref="Codes.Incomplete"/> composite listing each distinct problem
/// category in the order of the first data store that produced it.
/// </para>
/// <para>
/// Cancellation of the caller's token, and a lost or unavailable job fence, propagate from the data store being
/// refreshed and end the execution without a decision.
/// </para>
/// </remarks>
public sealed class EducationOrganizationRefreshJobHandler(
    IEducationOrganizationSnapshotRepository repository,
    EducationOrganizationTargetRefresher refresher,
    ILogger<EducationOrganizationRefreshJobHandler> logger
) : IJobHandler<EducationOrganizationRefreshPayload>
{
    internal static readonly EventId RefreshFinished = new(1441_08, "EducationOrganizationRefreshFinished");

    private static readonly Dictionary<string, string> _messages = Codes.All.ToDictionary(
        pair => pair.Key,
        pair => pair.Value,
        StringComparer.Ordinal
    );

    private static readonly JobErrorCode _dataStoreNotFound = Registered(Codes.DataStoreNotFound);
    private static readonly JobErrorCode _temporarilyUnavailable = Registered(Codes.TemporarilyUnavailable);
    private static readonly JobErrorCode _targetBusy = Registered(Codes.TargetBusy);
    private static readonly JobErrorCode _incomplete = new(Codes.Incomplete, Codes.IncompleteMessage);

    private ILogger Logger => logger;

    public async Task ExecuteAsync(
        JobExecutionContext context,
        EducationOrganizationRefreshPayload payload,
        CancellationToken cancellationToken
    )
    {
        bool one = payload.Scope == EducationOrganizationRefreshScope.One;
        Tally tally = new();

        switch (await repository.GetRefreshTargetsAsync(one ? payload.DataStoreId : null, cancellationToken))
        {
            case SnapshotRefreshTargetsResult.Success { Targets: var targets }:
                tally.Targets = targets.Count;
                if (one && targets.Count == 0)
                {
                    tally.Failures.Add(new Failure(_dataStoreNotFound, Transient: false));
                }

                foreach (EducationOrganizationRefreshTarget target in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    tally.Add(await refresher.RefreshAsync(context, target, cancellationToken), one);
                }
                break;
            case SnapshotRefreshTargetsResult.DbFailure { Diagnostic: var diagnostic }:
                tally.LookupFailure = diagnostic;
                tally.Failures.Add(new Failure(_temporarilyUnavailable, Transient: true));
                break;
            default:
                throw new InvalidOperationException("Unknown refresh target result.");
        }

        Decide(context, payload, tally);
    }

    /// <summary>
    /// Completes, retries or fails the job from every data store's outcome, after all of them were attempted.
    /// </summary>
    private void Decide(JobExecutionContext context, EducationOrganizationRefreshPayload payload, Tally tally)
    {
        if (tally.Failures.Count == 0)
        {
            Log(context, payload, tally, "Completed", null);
            return;
        }

        if (tally.Failures.Exists(failure => failure.Transient) && context.Attempt < context.MaxAttempts)
        {
            Log(context, payload, tally, "Retry", null);
            throw new EducationOrganizationRefreshRetryException();
        }

        if (payload.Scope == EducationOrganizationRefreshScope.One)
        {
            JobErrorCode single = tally.Failures[0].Code;
            Log(context, payload, tally, "Failed", single.Code);
            throw new JobPermanentException(single);
        }

        // Categories in the order of the first data store that produced each, which is identifier order.
        JobErrorCode[] categories =
        [
            .. tally.Failures.Select(failure => failure.Code).DistinctBy(code => code.Code),
        ];
        Log(context, payload, tally, "Failed", _incomplete.Code);
        throw new JobPermanentException(_incomplete, categories);
    }

    private void Log(
        JobExecutionContext context,
        EducationOrganizationRefreshPayload payload,
        Tally tally,
        string decision,
        string? errorCode
    ) =>
        Logger.Log(
            decision == "Completed" ? LogLevel.Information : LogLevel.Warning,
            RefreshFinished,
            "{Event} JobId={JobId} TenantId={TenantId} Scope={Scope} DataStoreId={DataStoreId} Attempt={Attempt} MaxAttempts={MaxAttempts} Targets={Targets} Refreshed={Refreshed} Removed={Removed} Failed={Failed} Decision={Decision} JobErrorCode={JobErrorCode} TargetLookupFailed={TargetLookupFailed} ExceptionTypeChain={ExceptionTypeChain} ProviderErrorCode={ProviderErrorCode} FailedOperation={FailedOperation}",
            RefreshFinished.Name,
            JobDiagnostics.SafeIdentifier(context.JobId),
            (context.TenantContext as TenantContext.Multitenant)?.TenantId,
            payload.Scope.ToString(),
            payload.DataStoreId,
            context.Attempt,
            context.MaxAttempts,
            tally.Targets,
            tally.Refreshed,
            tally.Removed,
            tally.Failures.Count,
            decision,
            errorCode,
            tally.LookupFailure is not null,
            tally.LookupFailure?.ExceptionTypeChain,
            tally.LookupFailure?.ProviderErrorCode,
            tally.LookupFailure?.Operation
        );

    private static JobErrorCode Registered(string code) => new(code, _messages[code]);

    /// <summary>A data store that did not refresh, with the code it contributes and whether a retry may fix it.</summary>
    private sealed record Failure(JobErrorCode Code, bool Transient);

    /// <summary>What the data stores of one execution did, in the order they were refreshed.</summary>
    private sealed class Tally
    {
        public int Targets { get; set; }

        public int Refreshed { get; private set; }

        public int Removed { get; private set; }

        public List<Failure> Failures { get; } = [];

        public JobFailureDiagnostic? LookupFailure { get; set; }

        public void Add(TargetRefreshOutcome outcome, bool one)
        {
            switch (outcome)
            {
                case TargetRefreshOutcome.Refreshed:
                    Refreshed++;
                    break;
                case TargetRefreshOutcome.Removed:
                    Removed++;
                    if (one)
                    {
                        Failures.Add(new Failure(_dataStoreNotFound, Transient: false));
                    }
                    break;
                case TargetRefreshOutcome.Busy:
                    Failures.Add(new Failure(_targetBusy, Transient: true));
                    break;
                case TargetRefreshOutcome.LeaseLost or TargetRefreshOutcome.DbFailure:
                    Failures.Add(new Failure(_temporarilyUnavailable, Transient: true));
                    break;
                case TargetRefreshOutcome.Failed { Failure: var failure }:
                    Failures.Add(
                        failure.ToJobErrorCode() is { } permanent
                            ? new Failure(permanent, Transient: false)
                            : new Failure(_temporarilyUnavailable, Transient: true)
                    );
                    break;
                default:
                    throw new InvalidOperationException("Unknown target refresh outcome.");
            }
        }
    }
}

/// <summary>
/// Retries a refresh in which a data store failed transiently while attempts remain. The
/// message is fixed text; the executor records the attempt as a transient failure.
/// </summary>
public sealed class EducationOrganizationRefreshRetryException()
    : Exception("A data store failed transiently; the education organization refresh will be retried.");
