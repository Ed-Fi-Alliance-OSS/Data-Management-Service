// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The publication stage of a create (6.3 steps 8 to 10, 6.9b, 6.7): the validated staging database is
/// renamed into the selected target on the holder session; the renamed instance is re-checked on an
/// instance-bound session; the marker is assigned there as the last mutation; and a marker proven
/// uncommitted is compensated on the holder session in the same invocation. Every selected-target mutation
/// is recorded in a <see cref="Publication"/> ledger before it is sent, so an outcome that is not settled
/// while the lock is held (the holder in doubt, loss, the caller's cancellation) is settled after the lock
/// is released by reading state, never by a further mutation: a committed marker is <c>Created</c>, a
/// renamed but unmarked target is <c>UnmarkedTarget</c> residue, and an unreadable outcome is
/// <c>OutcomeUnknown</c>.
/// </summary>
public sealed partial class DmsDataStoreProvisioningService
{
    public const string RenameIntoTargetStep = "RenameIntoTarget";
    public const string ResolveRenameStep = "ResolveRename";
    public const string OpenTargetSessionStep = "OpenTargetSession";
    public const string VerifyRenamedTargetStep = "VerifyRenamedTarget";
    public const string AssignMarkerStep = "AssignMarker";
    public const string ResolveMarkerStep = "ResolveMarker";
    public const string CompensateStep = "Compensate";
    public const string ResolveCompensationStep = "ResolveCompensation";

    internal static readonly EventId TargetResidue = new(1438_14, nameof(TargetResidue));

    /// <summary>What the staging stage hands to publication: the validated instance and the package's identity value.</summary>
    private sealed record StagedCandidate(DmsInstanceIdentity Instance, Guid PackageIdentity);

    private enum MarkerVerdict
    {
        Committed,
        Uncommitted,
        Unresolved,
    }

    /// <summary>
    /// The create under the target lock (6.3 steps 1 to 10), then the settlement of what the ledger left in
    /// doubt (D-12, 6.10). A cancellation that arrives once a selected-target mutation was sent is held until
    /// the settlement has run and handed back for the caller to rethrow, unless the marker committed.
    /// </summary>
    private async Task<(
        DmsDataStoreCreateResult? Result,
        OperationCanceledException? Cancellation
    )> CreateUnderLockAsync(
        DmsDataStoreCreateRequest request,
        DmsOperationId operation,
        TrustedTemplateArtifact artifact,
        StagedArtifact stagedArtifact,
        StagingCleanup cleanup,
        Publication publication,
        CancellationToken cancellationToken
    )
    {
        DmsDataStoreCreateResult? result = null;
        OperationCanceledException? cancellation = null;
        try
        {
            result = await RunUnderTargetLockAsync<DmsDataStoreCreateResult>(
                request.TargetName,
                CreateOperationName,
                operation,
                DmsCleanupReport.Clean,
                async scope =>
                {
                    scope.Publication = publication;
                    DmsLiveTargetState state = await PreambleAsync(scope, request.TargetName);
                    if (state is DmsLiveTargetState.Present)
                    {
                        // 6.3 step 1 and 6.5: a target already holding the name is reconciled by reads
                        // only, and nothing is created, restored, renamed, marked, or dropped
                        return await ReconcileAsync(scope, request, artifact, state, InspectTargetStep);
                    }

                    StagedCandidate candidate = await StageAsync(
                        scope,
                        request,
                        artifact,
                        stagedArtifact,
                        cleanup
                    );

                    // 6.3 step 7: advisory; the rename is the authoritative check. A database that took the
                    // name meanwhile is reconciled, never renamed over; the staging database is dropped by
                    // cleanup after the lock is released.
                    DmsLiveTargetState late = await scope.ReadAsync(
                        InspectTargetStep,
                        token => engine.InspectAsync(request.TargetName, token)
                    );
                    if (late is not DmsLiveTargetState.Absent)
                    {
                        return await ReconcileAsync(scope, request, artifact, late, InspectTargetStep);
                    }

                    // D-23: the last await point before publication; no further inspection follows it.
                    await TestHooks.AtAsync(
                        DmsProvisioningHookPoint.BeforeRename,
                        operation,
                        scope.CallerToken
                    );
                    return await PublishAsync(scope, request, artifact, candidate, publication);
                },
                static (failure, report) => new DmsDataStoreCreateResult.Failure(failure, report),
                cancellationToken
            );
        }
        catch (OperationCanceledException canceled) when (publication.State != PublicationState.None)
        {
            // A selected-target mutation was sent: its outcome is settled before the cancellation propagates.
            cancellation = canceled;
        }

        return (
            await SettlePublicationAsync(publication, artifact.Pin, operation, result, cleanup),
            cancellation
        );
    }

    /// <summary>
    /// 6.3 steps 8 to 10 under the lock, entered right after the <c>BeforeRename</c> hook. A collision with
    /// any database already holding the target name ends in reconciliation reads (6.5); the publication
    /// itself returns <c>Created</c> or throws the step's failure.
    /// </summary>
    private async Task<DmsDataStoreCreateResult> PublishAsync(
        LockScope scope,
        DmsDataStoreCreateRequest request,
        TrustedTemplateArtifact artifact,
        StagedCandidate candidate,
        Publication publication
    )
    {
        DmsDataStoreProvisioningOptions settings = options.Value;
        DmsOperationId operation = scope.Operation;
        DmsTargetDatabaseName target = request.TargetName;

        // 6.3 step 8: the rename is this operation's selected-target mutation, in doubt from the moment it is
        // sent until a definite outcome settles it (D-7b, D-12).
        publication.RenameDispatched(candidate, scope.Holder.Session);
        DmsCommandOutcome<DmsInstanceIdentity> renamed = await scope.CallAsync(
            RenameIntoTargetStep,
            settings.CommandTimeout,
            token =>
                engine.RenameIntoTargetAsync(
                    scope.Holder,
                    operation.StagingDatabaseName,
                    candidate.Instance,
                    target,
                    token
                ),
            publication.ObserveRename
        );
        switch (renamed)
        {
            case DmsCommandOutcome<DmsInstanceIdentity>.Succeeded succeeded
                when succeeded.Value.IsSameStableInstance(candidate.Instance):
                break;
            case DmsCommandOutcome<DmsInstanceIdentity>.Succeeded:
                // The rename returned, but the target name holds another instance; settled after the lock.
                throw StepFailed(DmsProvisioningFailureCode.InstanceChanged, RenameIntoTargetStep);
            case DmsCommandOutcome<DmsInstanceIdentity>.Failed
            {
                Failure.Class: DmsCommandFailureClass.DuplicateName
            }:
                // D-6: any database holding the name refuses the rename and is left untouched; the staging
                // database is ours and dropped by cleanup. What holds the name is inspected afresh and
                // reconciled (6.5).
                DmsLiveTargetState colliding = await scope.ReadAsync(
                    ReconcileInspectStep,
                    token => engine.InspectAsync(target, token)
                );
                return await ReconcileAsync(scope, request, artifact, colliding, ReconcileInspectStep);
            case DmsCommandOutcome<DmsInstanceIdentity>.Failed failed:
                throw StepFailed(RenameFailureCodeOf(failed.Failure), failed.Failure.Diagnostic);
            case DmsCommandOutcome<DmsInstanceIdentity>.Unknown:
                // The holder session is in doubt: the lock is given up, and the rename is resolved after it
                // is released, once the holder is proven gone (6.3 step 8, 6.9a).
                throw new DmsProvisioningFailedException(scope.LockLostFailure());
            default:
                throw new InvalidOperationException("Unknown rename outcome.");
        }

        DmsInstanceIdentity ourTarget = publication.TargetInstance!;

        // D-23: the last await point before the marker's session is opened; opening it is the instance check
        // that a database dropped or recreated under the name since the rename fails.
        await TestHooks.AtAsync(DmsProvisioningHookPoint.BeforeMarker, operation, scope.CallerToken);

        // 6.3 step 9.
        DmsSession? session = await OpenOwnedSessionAsync(scope, OpenTargetSessionStep, target, ourTarget);
        if (session is null)
        {
            publication.Displaced();
            throw StepFailed(DmsProvisioningFailureCode.InstanceChanged, OpenTargetSessionStep);
        }

        DmsCommandOutcome marked;
        await using (session)
        {
            await VerifyRenamedTargetAsync(scope, artifact.Manifest, session, candidate.PackageIdentity);

            // 6.3 step 10 on the instance-bound session; the marker is in doubt from the moment it is sent.
            publication.MarkerDispatched(session.ServerSession);
            try
            {
                marked = await scope.CallAsync(
                    AssignMarkerStep,
                    settings.CommandTimeout,
                    async token =>
                    {
                        DmsCommandOutcome outcome = await engine.AssignMarkerAsync(
                            session,
                            request.ExpectedSourceIdentity,
                            token
                        );

                        // D-23: the server committed, but the client lost the result.
                        return
                            outcome is DmsCommandOutcome.Succeeded
                            && TestHooks.ShouldInjectFault(
                                DmsProvisioningFaultPoint.FailAfterMarkerExecuted,
                                operation
                            )
                            ? new DmsCommandOutcome.Unknown(session.ServerSession)
                            : outcome;
                    },
                    publication.ObserveMarker
                );
            }
            catch (OperationCanceledException)
                when (scope.CallerToken.IsCancellationRequested
                    && !scope.Holder.LostToken.IsCancellationRequested
                )
            {
                // The caller's cancellation leaves the marker in doubt; it is resolved, never skipped (6.10).
                marked = new DmsCommandOutcome.Unknown(session.ServerSession);
            }
        }

        switch (marked)
        {
            case DmsCommandOutcome.Succeeded:
                return new DmsDataStoreCreateResult.Created(artifact.Pin, DmsCleanupReport.Clean);
            case DmsCommandOutcome.Failed failed:
                return await CompensateAsync(scope, target, publication, failed.Failure.Diagnostic);
            case DmsCommandOutcome.Unknown:
                // 6.9b: a committed marker is success even under cancellation; one proven uncommitted on our
                // instance permits compensation; anything else is left for inspection.
                switch (await ResolveMarkerAsync(publication, operation))
                {
                    case MarkerVerdict.Committed:
                        publication.MarkerResolvedCommitted();
                        return new DmsDataStoreCreateResult.Created(artifact.Pin, DmsCleanupReport.Clean);
                    case MarkerVerdict.Uncommitted:
                        publication.MarkerResolvedUncommitted();
                        return await CompensateAsync(
                            scope,
                            target,
                            publication,
                            DmsProvisioningDiagnostic.ForOperation(ResolveMarkerStep)
                        );
                    default:
                        publication.MarkerUnresolved();
                        throw StepFailed(DmsProvisioningFailureCode.OutcomeUnknown, ResolveMarkerStep);
                }
            default:
                throw new InvalidOperationException("Unknown marker outcome.");
        }
    }

    /// <summary>
    /// 6.3 step 9: the rename changes no content, so only identity (still the package value) and the effective
    /// schema are re-read on the instance-bound session; any difference means the instance changed, which is
    /// for inspection and never compensated.
    /// </summary>
    private async Task VerifyRenamedTargetAsync(
        LockScope scope,
        RestoreManifest manifest,
        DmsSession session,
        Guid packageIdentity
    )
    {
        DmsIdentityRead identity = await scope.ReadAsync(
            VerifyRenamedTargetStep,
            token => engine.ReadIdentityAsync(session, token)
        );
        DmsTargetFacts facts = await scope.ReadAsync(
            VerifyRenamedTargetStep,
            token => engine.ReadTargetFactsAsync(session, token)
        );
        DmsTargetValidation verdict = DmsTargetValidator.Validate(
            facts,
            identity,
            manifest,
            options.Value.Compatibility?.Projects ?? [],
            new DmsTargetValidationRule.AfterRename(packageIdentity)
        );
        if (verdict is DmsTargetValidation.Invalid invalid)
        {
            throw StepFailed(
                DmsProvisioningFailureCode.InstanceChanged,
                VerifyRenamedTargetStep + "." + invalid.Check
            );
        }
    }

    /// <summary>
    /// 6.7 compensation: with the marker proven uncommitted, the renamed target is dropped on the holder
    /// session with its instance re-verified, on a bounded token of its own so the caller's cancellation
    /// cannot skip it (6.10); it is never issued after loss, which the holder refuses. A completed
    /// compensation ends <c>IdentityAssignmentFailed</c>, or the caller's cancellation when that is what
    /// ended the marker; a refused or failed one ends <c>CompensationFailed</c> with the target reported.
    /// </summary>
    private async Task<DmsDataStoreCreateResult> CompensateAsync(
        LockScope scope,
        DmsTargetDatabaseName target,
        Publication publication,
        DmsProvisioningDiagnostic markerDiagnostic
    )
    {
        publication.CompensationDispatched();
        DmsCommandOutcome dropped = await scope.RecoveryCallAsync(
            CompensateStep,
            options.Value.CommandTimeout,
            token =>
                engine.DropInstanceAsync(
                    scope.Holder,
                    target,
                    publication.TargetInstance!,
                    DmsDropReason.Compensation,
                    token
                ),
            publication.ObserveCompensation
        );
        switch (dropped)
        {
            case DmsCommandOutcome.Succeeded:
                scope.CallerToken.ThrowIfCancellationRequested();
                throw StepFailed(DmsProvisioningFailureCode.IdentityAssignmentFailed, markerDiagnostic);
            case DmsCommandOutcome.Failed { Failure.Class: DmsCommandFailureClass.DoesNotExist } failed:
                throw StepFailed(DmsProvisioningFailureCode.InstanceChanged, failed.Failure.Diagnostic);
            case DmsCommandOutcome.Failed failed:
                throw StepFailed(DmsProvisioningFailureCode.CompensationFailed, failed.Failure.Diagnostic);
            default:
                // The holder is in doubt; whether the target is gone is read after the lock is released.
                throw StepFailed(DmsProvisioningFailureCode.CompensationFailed, CompensateStep);
        }
    }

    /// <summary>
    /// 6.9b resolution of a marker in doubt, on tokens of its own: the marker's session is proven gone, then
    /// the identity is read on a new session bound to the renamed instance. The expected value is
    /// <see cref="MarkerVerdict.Committed"/>; the package value is <see cref="MarkerVerdict.Uncommitted"/>;
    /// an unproven termination, a refused open, or any other read is <see cref="MarkerVerdict.Unresolved"/>.
    /// </summary>
    private async Task<MarkerVerdict> ResolveMarkerAsync(Publication publication, DmsOperationId operation)
    {
        if (!await ProveSessionGoneAsync(publication.MarkerSession!.Value, operation, ResolveMarkerStep))
        {
            return MarkerVerdict.Unresolved;
        }

        using CancellationTokenSource bound = new(options.Value.CommandTimeout, timeProvider);
        string failure;
        try
        {
            DmsSessionOpenResult opened = await engine.OpenSessionAsync(
                publication.Target,
                publication.TargetInstance!,
                bound.Token
            );
            if (opened is not DmsSessionOpenResult.Opened { Session: { } session })
            {
                return MarkerVerdict.Unresolved;
            }

            await using (session)
            {
                DmsIdentityRead identity = await engine.ReadIdentityAsync(session, bound.Token);
                return identity switch
                {
                    DmsIdentityRead.Found found
                        when found.SourceIdentity == publication.ExpectedSourceIdentity =>
                        MarkerVerdict.Committed,
                    DmsIdentityRead.Found found when found.SourceIdentity == publication.PackageIdentity =>
                        MarkerVerdict.Uncommitted,
                    _ => MarkerVerdict.Unresolved,
                };
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Only the type chain leaves the catch: a driver error may quote the connection.
            failure = DmsProvisioningDiagnostic.TypeChain(exception);
        }

        LogRecoveryUnproven(operation, ResolveMarkerStep, failure);
        return MarkerVerdict.Unresolved;
    }

    /// <summary>
    /// After the lock is released (6.10, D-12): settles what the ledger left in doubt by reading state only,
    /// and reports a renamed but unmarked target. A committed marker is <c>Created</c> whatever ended the
    /// stage; an outcome that cannot be read is <c>OutcomeUnknown</c>; otherwise the stage's own result
    /// stands. Returns null only when <paramref name="result"/> is null and nothing overrides it.
    /// </summary>
    private async Task<DmsDataStoreCreateResult?> SettlePublicationAsync(
        Publication publication,
        DmsTrustedArtifactPin pin,
        DmsOperationId operation,
        DmsDataStoreCreateResult? result,
        StagingCleanup cleanup
    )
    {
        string? unresolvedStep = await ResolveInDoubtAsync(publication, operation);
        publication.ApplyTo(cleanup);
        if (publication.State == PublicationState.Marked)
        {
            return result as DmsDataStoreCreateResult.Created
                ?? new DmsDataStoreCreateResult.Created(pin, DmsCleanupReport.Clean);
        }

        if (publication.State == PublicationState.Renamed)
        {
            cleanup.Add([new DmsCleanupResidue(DmsResidueKind.UnmarkedTarget, publication.Target.Value)]);
            logger.LogError(
                TargetResidue,
                "DMS data store provisioning {OperationId} left its renamed target unmarked for operator inspection",
                operation.Value
            );
            return ForInspection(result, operation);
        }

        if (unresolvedStep is not null)
        {
            return Fail(
                static (failure, report) => new DmsDataStoreCreateResult.Failure(failure, report),
                operation,
                CreateOperationName,
                DmsProvisioningFailure.Create(
                    DmsProvisioningFailureCode.OutcomeUnknown,
                    DmsProvisioningDiagnostic.ForOperation(unresolvedStep)
                ),
                DmsCleanupReport.Clean
            );
        }

        return result;
    }

    /// <summary>
    /// An unmarked target cannot be repaired by a retry: the next attempt finds the package identity under the
    /// name and refuses it as unowned. A failure that a stage ended with in another category is therefore
    /// reported as <c>IdentityAssignmentFailed</c> for inspection, with the stage's diagnostic kept; a failure
    /// already requiring inspection stands, and a null result (a cancellation to rethrow) stays null.
    /// </summary>
    private DmsDataStoreCreateResult? ForInspection(
        DmsDataStoreCreateResult? result,
        DmsOperationId operation
    )
    {
        if (
            result is not DmsDataStoreCreateResult.Failure failure
            || failure.Detail.Category == DmsProvisioningFailureCategory.OperatorInspectionRequired
        )
        {
            return result;
        }

        return Fail(
            static (detail, report) => new DmsDataStoreCreateResult.Failure(detail, report),
            operation,
            CreateOperationName,
            DmsProvisioningFailure.Create(
                DmsProvisioningFailureCode.IdentityAssignmentFailed,
                failure.Detail.Diagnostic
            ),
            failure.Cleanup
        );
    }

    /// <summary>
    /// Settles the one mutation the ledger may leave in doubt, by reading state only. Returns the step whose
    /// outcome stays unresolved, or null.
    /// </summary>
    private async Task<string?> ResolveInDoubtAsync(Publication publication, DmsOperationId operation)
    {
        switch (publication.State)
        {
            case PublicationState.RenameInDoubt:
                return await ResolveRenameAsync(publication, operation) ? null : ResolveRenameStep;
            case PublicationState.MarkerInDoubt:
                MarkerVerdict verdict = await ResolveMarkerAsync(publication, operation);
                if (verdict == MarkerVerdict.Committed)
                {
                    publication.MarkerResolvedCommitted();
                    return null;
                }

                if (verdict == MarkerVerdict.Uncommitted)
                {
                    // Never compensated without the lock (6.7): the target is reported instead.
                    publication.MarkerResolvedUncommitted();
                    return null;
                }

                publication.MarkerUnresolved();
                return ResolveMarkerStep;
            case PublicationState.CompensationInDoubt:
                await ResolveCompensationAsync(publication, operation);
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// 6.3 step 8 for a rename in doubt: the holder session is proven gone, then the operation's instance is
    /// looked for under both names. Under the target name it was renamed (unmarked); under the staging name
    /// it was not. True when either was found; false when the holder could not be proven gone, a read
    /// failed, or the instance is under neither name.
    /// </summary>
    private async Task<bool> ResolveRenameAsync(Publication publication, DmsOperationId operation)
    {
        if (!await ProveSessionGoneAsync(publication.HolderSession, operation, ResolveRenameStep))
        {
            return false;
        }

        using CancellationTokenSource bound = new(options.Value.CommandTimeout, timeProvider);
        string failure;
        try
        {
            DmsLiveTargetState target = await engine.InspectAsync(publication.Target, bound.Token);
            if (
                InstanceUnder(target) is { } renamed
                && renamed.IsSameStableInstance(publication.StagingInstance!)
            )
            {
                publication.RenameResolvedRenamed(renamed);
                return true;
            }

            DmsLiveTargetState staging = await engine.InspectAsync(publication.Staging, bound.Token);
            if (InstanceUnder(staging) is { } kept && kept.IsSameStableInstance(publication.StagingInstance!))
            {
                publication.RenameResolvedNotRenamed();
                return true;
            }

            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = DmsProvisioningDiagnostic.TypeChain(exception);
        }

        LogRecoveryUnproven(operation, ResolveRenameStep, failure);
        return false;
    }

    /// <summary>
    /// A compensation drop in doubt: once the holder is proven gone, the target name is read; the instance
    /// still there is reported. Without that proof, or when the read fails, the target is reported too,
    /// because nothing shows it gone.
    /// </summary>
    private async Task ResolveCompensationAsync(Publication publication, DmsOperationId operation)
    {
        if (!await ProveSessionGoneAsync(publication.HolderSession, operation, ResolveCompensationStep))
        {
            publication.CompensationResolvedPresent();
            return;
        }

        using CancellationTokenSource bound = new(options.Value.CommandTimeout, timeProvider);
        try
        {
            DmsLiveTargetState target = await engine.InspectAsync(publication.Target, bound.Token);
            if (
                InstanceUnder(target) is { } present
                && present.IsSameStableInstance(publication.TargetInstance!)
            )
            {
                publication.CompensationResolvedPresent();
            }
            else
            {
                publication.Displaced();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogRecoveryUnproven(
                operation,
                ResolveCompensationStep,
                DmsProvisioningDiagnostic.TypeChain(exception)
            );
            publication.CompensationResolvedPresent();
        }
    }

    private static DmsInstanceIdentity? InstanceUnder(DmsLiveTargetState state) =>
        state switch
        {
            DmsLiveTargetState.Present present => present.Instance,
            DmsLiveTargetState.Fenced fenced => fenced.Instance,
            _ => null,
        };

    private static DmsProvisioningFailureCode RenameFailureCodeOf(DmsCommandFailure failure) =>
        failure.Class switch
        {
            DmsCommandFailureClass.ObjectInUse => DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
            DmsCommandFailureClass.PrivilegeMissing => DmsProvisioningFailureCode.AdminPrivilegeMissing,
            DmsCommandFailureClass.DoesNotExist => DmsProvisioningFailureCode.InstanceChanged,
            _ => DmsProvisioningFailureCode.ServerRejected,
        };

    private void LogRecoveryUnproven(DmsOperationId operation, string step, string failure) =>
        logger.LogWarning(
            RecoveryUnproven,
            "DMS data store provisioning {OperationId} could not prove a session gone during {Step}: {ExceptionTypeChain}",
            operation.Value,
            step,
            failure
        );

    /// <summary>Where the selected target stands in a create's publication (D-12, 6.7).</summary>
    private enum PublicationState
    {
        /// <summary>No selected-target mutation was sent.</summary>
        None,

        /// <summary>The rename was sent and no definite outcome has settled it.</summary>
        RenameInDoubt,

        /// <summary>The rename definitely did not happen; the staging database is still ours under its name.</summary>
        NotRenamed,

        /// <summary>The operation's instance is under the target name without the marker.</summary>
        Renamed,

        /// <summary>The marker was sent and no definite outcome has settled it.</summary>
        MarkerInDoubt,

        /// <summary>The marker's outcome could not be resolved; nothing more is attempted.</summary>
        MarkerUnresolved,

        /// <summary>The marker committed: the target is created.</summary>
        Marked,

        /// <summary>The compensation drop was sent and no definite outcome has settled it.</summary>
        CompensationInDoubt,

        /// <summary>The renamed target was dropped by compensation.</summary>
        Compensated,

        /// <summary>The operation's instance is no longer under the target name; nothing of it is reported there.</summary>
        Displaced,
    }

    /// <summary>
    /// The ledger of a create's selected-target mutations (D-12, 6.7): each is recorded before it is sent and
    /// settled from the outcome it returns, observed before any loss check can discard it, so whatever ends
    /// the stage leaves an exact record of what may have happened to the target.
    /// </summary>
    private sealed class Publication(
        DmsTargetDatabaseName target,
        DmsDatabaseName staging,
        Guid expectedSourceIdentity
    )
    {
        public DmsTargetDatabaseName Target => target;

        public DmsDatabaseName Staging => staging;

        public Guid ExpectedSourceIdentity => expectedSourceIdentity;

        public PublicationState State { get; private set; } = PublicationState.None;

        public DmsInstanceIdentity? StagingInstance { get; private set; }

        public Guid PackageIdentity { get; private set; }

        public DmsServerSessionId HolderSession { get; private set; }

        public DmsInstanceIdentity? TargetInstance { get; private set; }

        public DmsServerSessionId? MarkerSession { get; private set; }

        /// <summary>
        /// Whether a selected-target mutation may have taken effect on the holder: <c>LockLost</c> is then an
        /// inspection failure, not a transient one (D-12).
        /// </summary>
        public bool TargetMutationIssued =>
            State is not (PublicationState.None or PublicationState.NotRenamed);

        public void RenameDispatched(StagedCandidate candidate, DmsServerSessionId holderSession)
        {
            StagingInstance = candidate.Instance;
            PackageIdentity = candidate.PackageIdentity;
            HolderSession = holderSession;
            State = PublicationState.RenameInDoubt;
        }

        public void ObserveRename(DmsCommandOutcome<DmsInstanceIdentity> outcome)
        {
            switch (outcome)
            {
                case DmsCommandOutcome<DmsInstanceIdentity>.Succeeded succeeded
                    when succeeded.Value.IsSameStableInstance(StagingInstance!):
                    RenameResolvedRenamed(succeeded.Value);
                    break;
                case DmsCommandOutcome<DmsInstanceIdentity>.Failed:
                    RenameResolvedNotRenamed();
                    break;
            }
        }

        public void RenameResolvedRenamed(DmsInstanceIdentity instance)
        {
            TargetInstance = instance;
            State = PublicationState.Renamed;
        }

        public void RenameResolvedNotRenamed() => State = PublicationState.NotRenamed;

        public void MarkerDispatched(DmsServerSessionId session)
        {
            MarkerSession = session;
            State = PublicationState.MarkerInDoubt;
        }

        public void ObserveMarker(DmsCommandOutcome outcome)
        {
            switch (outcome)
            {
                case DmsCommandOutcome.Succeeded:
                    MarkerResolvedCommitted();
                    break;
                case DmsCommandOutcome.Failed:
                    MarkerResolvedUncommitted();
                    break;
            }
        }

        public void MarkerResolvedCommitted() => State = PublicationState.Marked;

        public void MarkerResolvedUncommitted() => State = PublicationState.Renamed;

        public void MarkerUnresolved() => State = PublicationState.MarkerUnresolved;

        public void CompensationDispatched() => State = PublicationState.CompensationInDoubt;

        public void ObserveCompensation(DmsCommandOutcome outcome)
        {
            switch (outcome)
            {
                case DmsCommandOutcome.Succeeded:
                    State = PublicationState.Compensated;
                    break;
                case DmsCommandOutcome.Failed { Failure.Class: DmsCommandFailureClass.DoesNotExist }:
                    Displaced();
                    break;
                case DmsCommandOutcome.Failed:
                    CompensationResolvedPresent();
                    break;
            }
        }

        public void CompensationResolvedPresent() => State = PublicationState.Renamed;

        public void Displaced() => State = PublicationState.Displaced;

        /// <summary>
        /// The staging cleanup follows the instance: once it has left the staging name there is nothing to
        /// drop there; while the rename is in doubt nothing may be dropped there either.
        /// </summary>
        public void ApplyTo(StagingCleanup cleanup)
        {
            switch (State)
            {
                case PublicationState.None or PublicationState.NotRenamed:
                    break;
                case PublicationState.RenameInDoubt:
                    cleanup.Withhold();
                    break;
                default:
                    cleanup.Published();
                    break;
            }
        }
    }
}
