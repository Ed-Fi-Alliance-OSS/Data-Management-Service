// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The orchestrator (4.1) over the engine and catalog ports. Every call checks readiness first and
/// refuses with <c>NotReady</c> before touching either port (D-2); then the boundary is validated
/// (<c>InvalidRequest</c>, 6.1 step 2); for a create the artifact is resolved and compared with the pin
/// before any server contact and before the lock (6.2, 6.3 step 1); then the target lock is acquired
/// (6.1 step 4) and every later engine call runs under a token linked to the caller, the holder's
/// <c>LostToken</c>, and <c>CommandTimeout</c> (6.10). Loss of the holder ends the operation as
/// <c>LockLost</c>, and it is enforced at every read boundary, not only through the token: a read is not
/// dispatched once loss is declared, and a read that completes normally, or throws anything, while loss
/// is declared still ends the operation there (6.10). Caller cancellation propagates after the lock is
/// released; the lock is released on every path. This step delivers the shell: the preamble through the target inspection and the result
/// assembly. The stages that mutate (staging and publication, reconcile validation, owned deletion) arrive
/// with Phases 2 to 4 and are not implemented here: the shell throws <see cref="NotImplementedException"/>
/// where they begin, after the lock is released, and is not registered as the provisioner until they exist.
/// </summary>
public sealed class DmsDataStoreProvisioningService(
    IDmsDataStoreProvisionerReadiness readiness,
    IDmsTargetEngine engine,
    ITemplateArtifactCatalog catalog,
    IOptions<DmsDataStoreProvisioningOptions> options,
    ProtectedDatabaseNames protectedNames,
    TimeProvider timeProvider,
    ILogger<DmsDataStoreProvisioningService> logger
) : IDmsDataStoreProvisioner
{
    public const string CreateOperationName = "Create";
    public const string DeleteOperationName = "Delete";

    // Fixed step names carried in DmsProvisioningDiagnostic.Operation.
    public const string BoundaryStep = "Boundary";
    public const string ResolveArtifactStep = "ResolveArtifact";
    public const string AcquireTargetLockStep = "AcquireTargetLock";
    public const string ReadServerFactsStep = "ReadServerFacts";
    public const string InspectTargetStep = "InspectTarget";

    internal static readonly EventId OperationFailed = new(1438_10, nameof(OperationFailed));
    internal static readonly EventId LockLost = new(1438_11, nameof(LockLost));

    public async Task<DmsArtifactResolution> ResolveArtifactAsync(
        DmsDatabaseTemplate template,
        CancellationToken cancellationToken
    )
    {
        if (Refusal() is { } refusal)
        {
            return new DmsArtifactResolution.Failure(refusal, DmsCleanupReport.Clean);
        }

        if (ValidateTemplate(template) is { } invalid)
        {
            return new DmsArtifactResolution.Failure(invalid, DmsCleanupReport.Clean);
        }

        return await catalog.ResolveAsync(template, cancellationToken);
    }

    public async Task<DmsDataStoreCreateResult> CreateAsync(
        DmsDataStoreCreateRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refusal() is { } refusal)
        {
            return new DmsDataStoreCreateResult.Failure(refusal, DmsCleanupReport.Clean);
        }

        DmsProvisioningFailure? invalid =
            ValidateTemplate(request.Template)
            ?? ValidateTargetName(request.TargetName)
            ?? ValidateSourceIdentity(request.ExpectedSourceIdentity)
            ?? ValidatePin(request.PinnedArtifact);
        if (invalid is not null)
        {
            return new DmsDataStoreCreateResult.Failure(invalid, DmsCleanupReport.Clean);
        }

        // 6.2 runs before the lock so no lock is held during file work (6.3 step 1); nothing to clean up.
        DmsArtifactResolution resolution = await catalog.ResolveAsync(request.Template, cancellationToken);
        DmsCleanupReport resolutionCleanup;
        switch (resolution)
        {
            case DmsArtifactResolution.Failure failed:
                return new DmsDataStoreCreateResult.Failure(failed.Detail, failed.Cleanup);
            case DmsArtifactResolution.Resolved resolved when resolved.Pin != request.PinnedArtifact:
                return new DmsDataStoreCreateResult.Failure(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.ArtifactChangedSinceCreate,
                        DmsProvisioningDiagnostic.ForOperation(ResolveArtifactStep)
                    ),
                    resolved.Cleanup
                );
            case DmsArtifactResolution.Resolved resolved:
                // What resolution could not delete stays in every later result of this create (6.11).
                resolutionCleanup = resolved.Cleanup;
                break;
            default:
                throw new InvalidOperationException("Unknown artifact resolution.");
        }

        return await RunUnderTargetLockAsync<DmsDataStoreCreateResult>(
            request.TargetName,
            CreateOperationName,
            resolutionCleanup,
            async scope =>
            {
                DmsLiveTargetState state = await PreambleAsync(scope, request.TargetName);
                throw state is DmsLiveTargetState.Present
                    ? NotYetImplemented("reconcile (6.5)", "Step 3.7")
                    : NotYetImplemented("staging and publication (6.3, 6.4)", "Steps 3.5 and 3.6");
            },
            static (failure, cleanup) => new DmsDataStoreCreateResult.Failure(failure, cleanup),
            cancellationToken
        );
    }

    public async Task<DmsDataStoreDeleteResult> DeleteAsync(
        DmsDataStoreDeleteRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refusal() is { } refusal)
        {
            return new DmsDataStoreDeleteResult.Failure(refusal, DmsCleanupReport.Clean);
        }

        DmsProvisioningFailure? invalid =
            ValidateTargetName(request.TargetName) ?? ValidateSourceIdentity(request.ExpectedSourceIdentity);
        if (invalid is not null)
        {
            return new DmsDataStoreDeleteResult.Failure(invalid, DmsCleanupReport.Clean);
        }

        return await RunUnderTargetLockAsync<DmsDataStoreDeleteResult>(
            request.TargetName,
            DeleteOperationName,
            DmsCleanupReport.Clean,
            async scope =>
            {
                DmsLiveTargetState state = await PreambleAsync(scope, request.TargetName);
                if (state is DmsLiveTargetState.Absent)
                {
                    return new DmsDataStoreDeleteResult.AlreadyAbsent();
                }

                throw NotYetImplemented("owned deletion (6.6)", "Step 3.8");
            },
            static (failure, cleanup) => new DmsDataStoreDeleteResult.Failure(failure, cleanup),
            cancellationToken
        );
    }

    /// <summary>The common preamble under the lock (6.1 steps 5 and 6).</summary>
    private async Task<DmsLiveTargetState> PreambleAsync(LockScope scope, DmsTargetDatabaseName target)
    {
        // The facts feed the engine checks of later stages (6.3 step 2, 6.4 step 2).
        await scope.ReadAsync(ReadServerFactsStep, engine.ReadServerFactsAsync);
        DmsLiveTargetState state = await scope.ReadAsync(
            InspectTargetStep,
            token => engine.InspectAsync(target, token)
        );
        return state switch
        {
            DmsLiveTargetState.Protected => throw StepFailed(
                DmsProvisioningFailureCode.ProtectedTarget,
                InspectTargetStep
            ),
            DmsLiveTargetState.Fenced => throw StepFailed(
                DmsProvisioningFailureCode.TargetFenced,
                InspectTargetStep
            ),
            _ => state,
        };
    }

    /// <summary>
    /// Acquires the target lock (6.1 step 4), runs <paramref name="body"/> under it, and assembles the
    /// result: a step failure becomes <paramref name="failure"/>; loss of the holder becomes
    /// <c>LockLost</c>; caller cancellation and unexpected exceptions propagate. The holder is disposed,
    /// releasing the lock, on every path. <paramref name="priorCleanup"/> is what the steps before the lock
    /// could not clean up; every failure result carries it.
    /// </summary>
    private async Task<TResult> RunUnderTargetLockAsync<TResult>(
        DmsTargetDatabaseName target,
        string operationName,
        DmsCleanupReport priorCleanup,
        Func<LockScope, Task<TResult>> body,
        Func<DmsProvisioningFailure, DmsCleanupReport, TResult> failure,
        CancellationToken cancellationToken
    )
    {
        DmsDataStoreProvisioningOptions settings = options.Value;
        DmsOperationId operation = DmsOperationId.New();
        TResult Failed(DmsProvisioningFailure detail) =>
            Fail(failure, operation, operationName, detail, priorCleanup);

        // Cancellation during acquisition propagates: nothing was acquired and nothing was created.
        DmsTargetLockResult lockResult = await engine.AcquireTargetLockAsync(
            target,
            operation,
            cancellationToken
        );
        IDmsTargetLockHolder holder;
        switch (lockResult)
        {
            case DmsTargetLockResult.Acquired acquired:
                holder = acquired.Holder;
                break;
            case DmsTargetLockResult.Timeout:
                return Failed(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.LockTimeout,
                        DmsProvisioningDiagnostic.ForOperation(AcquireTargetLockStep)
                    )
                );
            case DmsTargetLockResult.Failure lockFailure:
                return Failed(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.LockUnavailable,
                        lockFailure.Diagnostic
                    )
                );
            default:
                throw new InvalidOperationException("Unknown target lock result.");
        }

        LockScope scope = new(holder, settings.CommandTimeout, timeProvider, cancellationToken);
        await using (holder)
        {
            try
            {
                return await body(scope);
            }
            catch (DmsProvisioningFailedException stepFailure)
            {
                return Failed(stepFailure.Failure);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested && holder.LostToken.IsCancellationRequested)
            {
                // Logged below, once the holder is disposed: the cancellation carries no detail worth keeping.
            }
        }

        logger.LogError(
            LockLost,
            "DMS data store provisioning {Operation} {OperationId} lost its target lock holder session during {Step}; the read in flight raised {ExceptionTypeChain}",
            operationName,
            operation.Value,
            scope.CurrentStep,
            scope.LossAccompanyingTypeChain ?? "nothing"
        );
        return failure(scope.LockLostFailure(), priorCleanup);
    }

    private TResult Fail<TResult>(
        Func<DmsProvisioningFailure, DmsCleanupReport, TResult> failure,
        DmsOperationId operation,
        string operationName,
        DmsProvisioningFailure detail,
        DmsCleanupReport cleanup
    )
    {
        logger.LogWarning(
            OperationFailed,
            "DMS data store provisioning {Operation} {OperationId} failed at {Step} with {Code} ({Category})",
            operationName,
            operation.Value,
            detail.Diagnostic.Operation,
            detail.Code,
            detail.Category
        );

        // The shell itself creates no resource; the report is what the steps before the lock left behind (6.11).
        return failure(detail, cleanup);
    }

    private DmsProvisioningFailure? Refusal() =>
        readiness.Current is DmsProvisionerReadiness.Ready
            ? null
            : DisabledDmsDataStoreProvisioner.RefusalFor(readiness.Current);

    private static DmsProvisioningFailure? ValidateTemplate(DmsDatabaseTemplate? template) =>
        template is null ? Boundary(DmsProvisioningFailureCode.TemplateNotAllowlisted) : null;

    /// <summary>
    /// The name type guarantees the grammar, the reserved names, and the reserved prefixes, but it was
    /// validated against whatever protected set its creator held; the provisioner's own set is the authority.
    /// </summary>
    private DmsProvisioningFailure? ValidateTargetName(DmsTargetDatabaseName target) =>
        target == default
        || DmsTargetDatabaseName.TryCreate(target.Value, protectedNames, out _)
            != DmsTargetNameValidation.Valid
            ? Boundary(DmsProvisioningFailureCode.InvalidTargetName)
            : null;

    private static DmsProvisioningFailure? ValidateSourceIdentity(Guid expectedSourceIdentity) =>
        expectedSourceIdentity == Guid.Empty
            ? Boundary(DmsProvisioningFailureCode.EmptySourceIdentity)
            : null;

    private static DmsProvisioningFailure? ValidatePin(DmsTrustedArtifactPin? pin) =>
        pin is null ? Boundary(DmsProvisioningFailureCode.ArtifactPinMissing) : null;

    private static DmsProvisioningFailure Boundary(DmsProvisioningFailureCode code) =>
        DmsProvisioningFailure.Create(code, DmsProvisioningDiagnostic.ForOperation(BoundaryStep));

    private static DmsProvisioningFailedException StepFailed(DmsProvisioningFailureCode code, string step) =>
        new(DmsProvisioningFailure.Create(code, DmsProvisioningDiagnostic.ForOperation(step)));

    private static NotImplementedException NotYetImplemented(string stage, string step) =>
        new(
            $"The {stage} stage of the DMS data store provisioner arrives with {step}; this build contains the orchestrator shell only."
        );

    /// <summary>
    /// One operation's time under the lock: links every engine call to the caller token, the holder's
    /// <c>LostToken</c>, and the per-command deadline (6.10), and classifies what a read throws. Caller
    /// cancellation and holder loss propagate as <see cref="OperationCanceledException"/> for the boundary
    /// to tell apart; a deadline is <c>TimedOut</c>; anything else a read throws is <c>Unknown</c> with its
    /// type chain, because the provider-neutral orchestrator cannot classify driver exceptions. Loss is
    /// checked explicitly before a read is dispatched and after it completes, because the linked token only
    /// reaches a read that honours it: a result produced after loss is discarded, and an exception raised
    /// alongside loss is loss, with only its type chain kept for the log.
    /// </summary>
    private sealed class LockScope(
        IDmsTargetLockHolder holder,
        TimeSpan commandTimeout,
        TimeProvider timeProvider,
        CancellationToken callerToken
    )
    {
        public string CurrentStep { get; private set; } = AcquireTargetLockStep;

        /// <summary>The type chain of a non-cancellation exception a read raised alongside loss, if any.</summary>
        public string? LossAccompanyingTypeChain { get; private set; }

        public async Task<T> ReadAsync<T>(string step, Func<CancellationToken, Task<T>> read)
        {
            CurrentStep = step;

            // Caller cancellation, or loss declared since the previous read, stops this one before anything
            // is dispatched.
            callerToken.ThrowIfCancellationRequested();
            ThrowIfLost();

            using CancellationTokenSource deadline = new(commandTimeout, timeProvider);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                callerToken,
                holder.LostToken,
                deadline.Token
            );
            T result;
            try
            {
                result = await read(linked.Token);
            }
            catch (Exception exception) when (holder.LostToken.IsCancellationRequested)
            {
                // Loss wins over whatever the read threw. A driver exception raised alongside loss is not
                // classified, and only its type chain is kept, because its message may quote the connection.
                if (exception is not OperationCanceledException)
                {
                    LossAccompanyingTypeChain = DmsProvisioningDiagnostic.TypeChain(exception);
                }

                throw Lost();
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                throw new DmsProvisioningFailedException(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.TimedOut,
                        DmsProvisioningDiagnostic.ForOperation(step)
                    )
                );
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException and not DmsProvisioningFailedException)
            {
                throw new DmsProvisioningFailedException(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.Unknown,
                        DmsProvisioningDiagnostic.From(exception, step)
                    ),
                    exception
                );
            }

            // Loss declared while the read completed normally: the result came from a session whose lock
            // may be gone, so it is discarded and nothing further is dispatched.
            ThrowIfLost();
            return result;
        }

        private void ThrowIfLost()
        {
            if (holder.LostToken.IsCancellationRequested)
            {
                throw Lost();
            }
        }

        private OperationCanceledException Lost() =>
            new("The target lock holder session was lost.", holder.LostToken);

        /// <summary>
        /// <c>LockLost</c> is transient while no selected-target mutation has been issued on the holder
        /// (D-12). The shell issues none; the publication and delete stages will record the first one and
        /// turn this into an inspection failure with residue.
        /// </summary>
        public DmsProvisioningFailure LockLostFailure() =>
            DmsProvisioningFailure.Create(
                DmsProvisioningFailureCode.LockLost,
                DmsProvisioningFailureCategory.Transient,
                DmsProvisioningDiagnostic.ForOperation(CurrentStep)
            );
    }
}
