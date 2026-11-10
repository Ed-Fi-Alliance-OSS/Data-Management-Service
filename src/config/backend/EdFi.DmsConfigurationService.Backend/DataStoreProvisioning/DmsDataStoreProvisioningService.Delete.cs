// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// The owned delete (6.6) under the target lock: the target found by the preamble is opened on a session
/// bound to its instance and must carry the expected identity; it is then fenced on the holder session with
/// every other session terminated and proven gone (D-22), its identity and instance are read again under the
/// fence, our own session is closed and proven gone, and the drop re-verifies the instance, the fence and the
/// absence of sessions before it runs. On SQL Server the fence (single-user mode with <c>ROLLBACK IMMEDIATE</c>)
/// can keep no session, so our session is closed and proven gone before the fence; the holder session that set
/// single-user mode then holds the database's one slot, so the identity is read again on the holder through a
/// three-part name, and the instance by inspection.
/// Every selected-target mutation is recorded in a <see cref="Deletion"/>
/// ledger before it is sent: a failure after the fence reverses it on the holder session before the failure is
/// reported, and whatever the lock could not settle (an outcome in doubt, loss, the caller's cancellation) is
/// settled after it is released by reading state only, never by a further mutation. A target left fenced is
/// reported as <see cref="DmsResidueKind.FencedTarget"/> residue for inspection.
/// </summary>
public sealed partial class DmsDataStoreProvisioningService
{
    public const string DeleteOpenSessionStep = "Delete.OpenSession";
    public const string DeleteIdentityStep = "Delete.Identity";
    public const string FenceStep = "Fence";
    public const string VerifyFencedStep = "Delete.VerifyFenced";
    public const string ReleaseTargetSessionStep = "Delete.ReleaseTargetSession";
    public const string UnfenceStep = "Unfence";
    public const string DropTargetStep = "DropTarget";
    public const string ResolveDeletionStep = "ResolveDeletion";

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

        DmsAdminConnectionResolution resolution = await adminConnection.ResolveAsync(cancellationToken);
        if (resolution is DmsAdminConnectionResolution.Failed unresolved)
        {
            return new DmsDataStoreDeleteResult.Failure(
                SecretResolutionFailure(unresolved),
                DmsCleanupReport.Clean
            );
        }

        // Bound in the operation's own frame: settlement after the lock is released connects with it too.
        using IDisposable adminBinding = adminConnection.Bind(
            ((DmsAdminConnectionResolution.Resolved)resolution).Connection
        );

        DmsOperationId operation = DmsOperationId.New();
        Deletion deletion = new(request.TargetName);
        DmsDataStoreDeleteResult? result = null;
        OperationCanceledException? cancellation = null;
        try
        {
            result = await RunUnderTargetLockAsync<DmsDataStoreDeleteResult>(
                request.TargetName,
                DeleteOperationName,
                operation,
                DmsCleanupReport.Clean,
                scope =>
                {
                    scope.Ledger = deletion;
                    return DeleteUnderLockAsync(scope, request, deletion);
                },
                static (failure, cleanup) => new DmsDataStoreDeleteResult.Failure(failure, cleanup),
                cancellationToken
            );
        }
        catch (OperationCanceledException canceled)
            when (deletion.TargetMutationIssued || deletion.State == DeletionState.Dropped)
        {
            // A selected-target mutation may stand, or the drop was confirmed before loss discarded its result: its
            // outcome is settled before the cancellation propagates.
            cancellation = canceled;
        }

        result = await SettleDeletionAsync(deletion, operation, result);
        if (cancellation is not null && result is not DmsDataStoreDeleteResult.Dropped)
        {
            ExceptionDispatchInfo.Throw(cancellation);
        }

        return result ?? throw new InvalidOperationException("The delete ended without a result.");
    }

    /// <summary>6.6 steps 1 to 6 under the lock; returns the result or throws the step's failure.</summary>
    private async Task<DmsDataStoreDeleteResult> DeleteUnderLockAsync(
        LockScope scope,
        DmsDataStoreDeleteRequest request,
        Deletion deletion
    )
    {
        DmsTargetDatabaseName target = request.TargetName;

        // 6.6 step 1: the preamble refuses protected and fenced targets.
        DmsLiveTargetState state = await PreambleAsync(scope, target);
        if (state is DmsLiveTargetState.Absent)
        {
            return new DmsDataStoreDeleteResult.AlreadyAbsent();
        }

        if (state is not DmsLiveTargetState.Present { State: DmsDatabaseState.Online } present)
        {
            throw StepFailed(DmsProvisioningFailureCode.TargetUnverifiable, InspectTargetStep);
        }

        DmsInstanceIdentity instance = present.Instance;

        // SQL Server's fence ends every session on the target and keeps none (6.6 SQL Server step 3).
        bool fenceKeepsNoSession = string.Equals(
            engine.Provider,
            DmsDataStoreProvisioningOptions.MssqlProvider,
            StringComparison.Ordinal
        );

        // 6.6 step 2: our target session, bound to the inspected instance; ownership is read, nothing is mutated.
        DmsSession? session = await OpenOwnedSessionAsync(
            scope,
            DeleteOpenSessionStep,
            target,
            instance,
            DmsProvisioningFailureCode.TargetUnverifiable
        );
        if (session is null)
        {
            throw StepFailed(DmsProvisioningFailureCode.TargetUnverifiable, DeleteOpenSessionStep);
        }

        DmsServerSessionId targetSession = session.ServerSession;
        bool sessionClosed = false;
        try
        {
            await VerifyOwnedAsync(scope, session, request.ExpectedSourceIdentity, DeleteIdentityStep);

            if (fenceKeepsNoSession)
            {
                // 6.6 SQL Server step 3: our work session is closed and proven gone before the fence.
                sessionClosed = true;
                await session.DisposeAsync();
                await ReleaseTargetSessionAsync(scope, targetSession);
            }

            // 6.6 step 3: D-23, the last await point before the fence.
            await TestHooks.AtAsync(DmsProvisioningHookPoint.BeforeFence, scope.Operation, scope.CallerToken);
            await FenceAsync(scope, target, instance, fenceKeepsNoSession ? null : targetSession, deletion);

            await WhileFencedAsync(
                scope,
                deletion,
                async () =>
                {
                    // 6.6 step 4: ownership and instance again, now that no other session can change them.
                    // SQL Server: the holder holds the single-user slot, so it reads the identity itself.
                    // That read runs on the holder's gate: its deadline covers the gate wait (one CommandTimeout,
                    // a heartbeat probe's bound) and SQL Server's holder bound (one CommandTimeout).
                    await VerifyOwnedAsync(
                        scope,
                        fenceKeepsNoSession
                            ? token => engine.ReadIdentityOnHolderAsync(scope.Holder, target, token)
                            : token => engine.ReadIdentityAsync(session, token),
                        fenceKeepsNoSession ? options.Value.CommandTimeout * 2 : options.Value.CommandTimeout,
                        request.ExpectedSourceIdentity,
                        VerifyFencedStep
                    );

                    DmsLiveTargetState fenced = await scope.ReadAsync(
                        VerifyFencedStep,
                        token => engine.InspectAsync(target, token)
                    );
                    switch (fenced)
                    {
                        case DmsLiveTargetState.Fenced under
                            when under.Instance.IsSameStableInstance(instance):
                            break;
                        case DmsLiveTargetState.Present under
                            when under.Instance.IsSameStableInstance(instance):
                            // Our instance accepts connections again: exclusivity no longer holds.
                            throw StepFailed(
                                DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                                VerifyFencedStep
                            );
                        default:
                            deletion.Displaced();
                            throw StepFailed(DmsProvisioningFailureCode.InstanceChanged, VerifyFencedStep);
                    }

                    if (!fenceKeepsNoSession)
                    {
                        // 6.6 step 5: our own session is closed and proven gone before the drop checks for sessions.
                        sessionClosed = true;
                        await session.DisposeAsync();
                        await ReleaseTargetSessionAsync(scope, targetSession);
                    }

                    // D-23: the last await point before the drop.
                    await TestHooks.AtAsync(
                        DmsProvisioningHookPoint.BeforeDrop,
                        scope.Operation,
                        scope.CallerToken
                    );
                }
            );
        }
        finally
        {
            if (!sessionClosed)
            {
                await session.DisposeAsync();
            }
        }

        return await DropAsync(scope, target, instance, deletion);
    }

    /// <summary>
    /// Proves <paramref name="targetSession"/>, already closed by the client, gone from the server (D-22), so a later
    /// fence or drop cannot meet it; unproven is <c>ExclusiveAccessNotEstablished</c>.
    /// </summary>
    private async Task ReleaseTargetSessionAsync(LockScope scope, DmsServerSessionId targetSession)
    {
        DmsDataStoreProvisioningOptions settings = options.Value;
        DmsSessionTermination released = await scope.CallAsync(
            ReleaseTargetSessionStep,
            settings.SessionTerminationTimeout + settings.CommandTimeout,
            token => engine.TerminateSessionAsync(targetSession, settings.SessionTerminationTimeout, token),
            observe: null
        );
        if (released is DmsSessionTermination.NotTerminated)
        {
            throw StepFailed(
                DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                ReleaseTargetSessionStep
            );
        }
    }

    /// <summary>
    /// The identity on <paramref name="session"/>: a refused or unreadable identity is
    /// <c>TargetUnverifiable</c> and another identity <c>TargetUnowned</c>, both under <paramref name="step"/>.
    /// </summary>
    private Task VerifyOwnedAsync(LockScope scope, DmsSession session, Guid expected, string step) =>
        VerifyOwnedAsync(
            scope,
            token => engine.ReadIdentityAsync(session, token),
            options.Value.CommandTimeout,
            expected,
            step
        );

    /// <summary>
    /// The identity <paramref name="read"/> returns within <paramref name="deadline"/>, judged as
    /// <see cref="VerifyOwnedAsync(LockScope, DmsSession, Guid, string)"/> does.
    /// </summary>
    private static async Task VerifyOwnedAsync(
        LockScope scope,
        Func<CancellationToken, Task<DmsIdentityRead>> read,
        TimeSpan deadline,
        Guid expected,
        string step
    )
    {
        DmsIdentityRead identity = await scope.CallAsync(step, deadline, read, observe: null);
        ThrowIfAccessDenied(identity, DmsProvisioningFailureCode.TargetUnverifiable, step);
        if (identity is not DmsIdentityRead.Found found)
        {
            throw StepFailed(DmsProvisioningFailureCode.TargetUnverifiable, step);
        }

        if (found.SourceIdentity != expected)
        {
            throw StepFailed(DmsProvisioningFailureCode.TargetUnowned, step);
        }
    }

    /// <summary>
    /// 6.6 step 3 on the holder session, keeping <paramref name="keep"/> (none on SQL Server). A target that is no longer the
    /// instance is <c>InstanceChanged</c> with nothing fenced; a fence that stands but did not establish
    /// exclusivity is reversed first and reported as <c>ExclusiveAccessNotEstablished</c>, or
    /// <c>AdminPrivilegeMissing</c> for a missing privilege; an outcome in doubt puts the lock in doubt
    /// (<c>LockLost</c>), and what the fence did is read after the lock is released.
    /// </summary>
    private async Task FenceAsync(
        LockScope scope,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        DmsServerSessionId? keep,
        Deletion deletion
    )
    {
        DmsDataStoreProvisioningOptions settings = options.Value;

        // Recorded as it is handed to the engine, and in doubt from then on: only a refusal by the scope's own
        // caller or loss check, or a DmsCommandNotDispatchedException from the holder's gate or the provider's
        // check before sending, proves nothing was sent. Whatever else ends the call without a definite outcome
        // (an unknown outcome or an exception raised alongside loss, a deadline, the caller's cancellation)
        // leaves the fence in doubt for settlement after the lock is released.
        //
        // The deadline bounds the whole step from the request, the wait for the holder's gate included: the largest
        // bound a provider sets once it holds the gate (PostgreSQL's: SessionTerminationTimeout + 5 x
        // CommandTimeout) plus one CommandTimeout, the bound of a heartbeat probe that may hold the gate. The
        // termination loop keeps its whole SessionTerminationTimeout, and the reads after it their own allowances,
        // while the gate wait and the statements around the loop fit in those six CommandTimeouts. A probe that
        // outlasts its own, because its driver is still completing a cancellation after the server left a trivial
        // statement unanswered that long, spends that allowance as a slow statement would; this deadline then ends
        // the fence with the outcome the provider's bound gives.
        DmsCommandOutcome fenced = await scope.CallAsync(
            FenceStep,
            settings.SessionTerminationTimeout + (settings.CommandTimeout * 6),
            token =>
                DispatchAsync(
                    () => deletion.FenceDispatched(instance, scope.Holder.Session),
                    deletion.NotDispatched,
                    () => engine.FenceAsync(scope.Holder, target, instance, keep, token)
                ),
            deletion.ObserveFence
        );

        switch (fenced)
        {
            case DmsCommandOutcome.Succeeded:
                return;
            case DmsCommandOutcome.Failed { Failure.Class: DmsCommandFailureClass.DoesNotExist } failed:
                throw StepFailed(DmsProvisioningFailureCode.InstanceChanged, failed.Failure.Diagnostic);
            case DmsCommandOutcome.Failed failed:
                DmsProvisioningFailureCode code = failed.Failure.Class switch
                {
                    DmsCommandFailureClass.PrivilegeMissing =>
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                    _ when deletion.IsFenced => DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                    _ => DmsProvisioningFailureCode.ServerRejected,
                };
                await UnfenceIfFencedAsync(scope, deletion);
                scope.CallerToken.ThrowIfCancellationRequested();
                throw StepFailed(code, failed.Failure.Diagnostic);
            case DmsCommandOutcome.Unknown:
                throw new DmsProvisioningFailedException(scope.LockLostFailure());
            default:
                throw new InvalidOperationException("Unknown fence outcome.");
        }
    }

    /// <summary>
    /// 6.6 step 5 on the holder session. Success is <c>Dropped</c>; a name found empty is <c>AlreadyAbsent</c>;
    /// another instance under the name is <c>InstanceChanged</c> and touched nothing; any other definite failure
    /// reverses the fence first and is <c>ExclusiveAccessNotEstablished</c>, <c>AdminPrivilegeMissing</c>, or
    /// <c>DropFailed</c>; an outcome in doubt is <c>LockLost</c> and settled after the lock is released.
    /// The step's deadline covers the wait for the holder's gate (one <c>CommandTimeout</c>, a heartbeat probe's
    /// bound) and the largest provider bound once the gate is held (PostgreSQL's instance check, session count,
    /// and drop, one <c>CommandTimeout</c> each).
    /// </summary>
    private async Task<DmsDataStoreDeleteResult> DropAsync(
        LockScope scope,
        DmsTargetDatabaseName target,
        DmsInstanceIdentity instance,
        Deletion deletion
    )
    {
        DmsCommandOutcome dropped;
        try
        {
            dropped = await scope.CallAsync(
                DropTargetStep,
                options.Value.CommandTimeout * 4,
                token =>
                    DispatchAsync(
                        deletion.DropDispatched,
                        deletion.NotDispatched,
                        () =>
                            engine.DropInstanceAsync(
                                scope.Holder,
                                target,
                                instance,
                                DmsDropReason.Delete,
                                token
                            )
                    ),
                deletion.ObserveDrop
            );
        }
        catch (OperationCanceledException)
            when (deletion.State == DeletionState.Fenced
                && scope.CallerToken.IsCancellationRequested
                && !scope.Holder.LostToken.IsCancellationRequested
            )
        {
            // The drop never reached the driver (the ledger still shows the fence, not a drop in doubt): the
            // scope, the holder's gate or the provider's check before sending refused it for the caller's
            // cancellation, so the fence is reversed first while the holder still owns the lock. A drop that may
            // have reached the driver stays in doubt whatever ended it, and is settled after the lock is released.
            await UnfenceIfFencedAsync(scope, deletion);
            throw;
        }
        catch (DmsProvisioningFailedException timedOut)
            when (timedOut.Failure.Code == DmsProvisioningFailureCode.TimedOut
                && deletion.State == DeletionState.Fenced
                && !scope.Holder.LostToken.IsCancellationRequested
            )
        {
            // The drop's deadline expired before it reached the driver (the ledger still shows the fence): the
            // fence is reversed the same way while the holder still owns the lock, and the timeout is reported.
            await UnfenceIfFencedAsync(scope, deletion);
            throw;
        }

        switch (dropped)
        {
            case DmsCommandOutcome.Succeeded:
                return new DmsDataStoreDeleteResult.Dropped(DmsCleanupReport.Clean);
            case DmsCommandOutcome.Failed
            {
                Failure:
                {
                    Class: DmsCommandFailureClass.DoesNotExist,
                    Diagnostic.Operation: DmsEngineOperations.DropTargetAbsent
                },
            }:
                return new DmsDataStoreDeleteResult.AlreadyAbsent();
            case DmsCommandOutcome.Failed { Failure.Class: DmsCommandFailureClass.DoesNotExist } failed:
                throw StepFailed(DmsProvisioningFailureCode.InstanceChanged, failed.Failure.Diagnostic);
            case DmsCommandOutcome.Failed failed:
                DmsProvisioningFailureCode code = failed.Failure.Class switch
                {
                    DmsCommandFailureClass.ObjectInUse =>
                        DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                    DmsCommandFailureClass.PrivilegeMissing =>
                        DmsProvisioningFailureCode.AdminPrivilegeMissing,
                    _ => DmsProvisioningFailureCode.DropFailed,
                };
                await UnfenceIfFencedAsync(scope, deletion);
                scope.CallerToken.ThrowIfCancellationRequested();
                throw StepFailed(code, failed.Failure.Diagnostic);
            case DmsCommandOutcome.Unknown:
                throw new DmsProvisioningFailedException(scope.LockLostFailure());
            default:
                throw new InvalidOperationException("Unknown drop outcome.");
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> while the target is fenced: a step failure, or the caller's cancellation,
    /// reverses the fence on the holder session before it propagates. Loss of the holder propagates at once:
    /// the fence can no longer be reversed under the lock and is reported after it is released.
    /// </summary>
    private async Task WhileFencedAsync(LockScope scope, Deletion deletion, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (DmsProvisioningFailedException)
        {
            await UnfenceIfFencedAsync(scope, deletion);
            throw;
        }
        catch (OperationCanceledException)
            when (scope.CallerToken.IsCancellationRequested && !scope.Holder.LostToken.IsCancellationRequested
            )
        {
            await UnfenceIfFencedAsync(scope, deletion);
            throw;
        }
    }

    /// <summary>
    /// Reverses a standing fence on the holder session, on a bounded token of its own so the caller's
    /// cancellation cannot skip it (6.10). A refused or unresolved reversal leaves the ledger fenced (or in
    /// doubt) for settlement, which reports the target; loss propagates.
    /// The bound covers the wait for the holder's gate (one <c>CommandTimeout</c>, a heartbeat probe's bound) and
    /// the largest provider bound once the gate is held (PostgreSQL's instance check and reversal, one
    /// <c>CommandTimeout</c> each), so a probe holding the gate cannot leave an owned target fenced.
    /// </summary>
    private async Task UnfenceIfFencedAsync(LockScope scope, Deletion deletion)
    {
        if (!deletion.IsFenced)
        {
            return;
        }

        try
        {
            await scope.RecoveryCallAsync(
                UnfenceStep,
                options.Value.CommandTimeout * 3,
                token =>
                {
                    deletion.UnfenceDispatched();
                    return engine.UnfenceAsync(scope.Holder, deletion.Target, deletion.Instance!, token);
                },
                deletion.ObserveUnfence
            );
        }
        catch (DmsProvisioningFailedException)
        {
            // A deadline or an unclassified error: whether the fence stands is read after the lock is released.
        }
    }

    /// <summary>
    /// After the lock is released (6.10, D-12): settles what the ledger left in doubt by reading state only. A
    /// drop the engine confirmed is <c>Dropped</c> even when loss or the caller's cancellation discarded its
    /// result, because the ledger observed it first. A
    /// drop in doubt is resolved by the name once the holder is proven gone: empty is <c>Dropped</c> whatever
    /// ended the stage, our instance is <c>DropFailed</c>, another is <c>InstanceChanged</c>, and an unreadable
    /// name is <c>OutcomeUnknown</c>. A fence that may stand is read the same way and, unless the name shows
    /// our instance accepting connections, or no longer our instance, is reported as
    /// <see cref="DmsResidueKind.FencedTarget"/> with the failure moved to inspection (<see cref="ForFencedTarget"/>).
    /// </summary>
    private async Task<DmsDataStoreDeleteResult?> SettleDeletionAsync(
        Deletion deletion,
        DmsOperationId operation,
        DmsDataStoreDeleteResult? result
    )
    {
        if (deletion.State == DeletionState.Dropped)
        {
            return new DmsDataStoreDeleteResult.Dropped(DmsCleanupReport.Clean);
        }

        if (
            deletion.State
            is not (
                DeletionState.FenceInDoubt
                or DeletionState.Fenced
                or DeletionState.UnfenceInDoubt
                or DeletionState.DropInDoubt
            )
        )
        {
            return result;
        }

        bool dropInDoubt = deletion.State == DeletionState.DropInDoubt;
        DmsLiveTargetState? state = await ReadSettledTargetAsync(deletion, operation);
        DmsInstanceIdentity? under = state is null ? null : InstanceUnder(state);
        bool ours = under is not null && under.IsSameStableInstance(deletion.Instance!);

        if (dropInDoubt)
        {
            if (state is DmsLiveTargetState.Absent)
            {
                return new DmsDataStoreDeleteResult.Dropped(DmsCleanupReport.Clean);
            }

            DmsProvisioningDiagnostic resolved = DmsProvisioningDiagnostic.ForOperation(ResolveDeletionStep);
            DmsProvisioningFailure settled = (state, ours) switch
            {
                (null, _) => DmsProvisioningFailure.Create(
                    DmsProvisioningFailureCode.OutcomeUnknown,
                    resolved
                ),
                (_, true) => DmsProvisioningFailure.Create(
                    DmsProvisioningFailureCode.DropFailed,
                    DmsProvisioningFailureCategory.OperatorInspectionRequired,
                    resolved
                ),
                _ => DmsProvisioningFailure.Create(DmsProvisioningFailureCode.InstanceChanged, resolved),
            };
            result = Fail(
                static (failure, report) => new DmsDataStoreDeleteResult.Failure(failure, report),
                operation,
                DeleteOperationName,
                settled,
                DmsCleanupReport.Clean
            );
        }

        bool fencedOrUnknown = state is null || (ours && state is DmsLiveTargetState.Fenced);
        if (!fencedOrUnknown)
        {
            return result;
        }

        logger.LogError(
            TargetResidue,
            "DMS data store provisioning {OperationId} left the target fenced for operator inspection",
            operation.Value
        );
        return result is DmsDataStoreDeleteResult.Failure failure
            ? new DmsDataStoreDeleteResult.Failure(
                ForFencedTarget(failure.Detail),
                Merge(
                    failure.Cleanup,
                    new DmsCleanupReport([
                        new DmsCleanupResidue(DmsResidueKind.FencedTarget, deletion.Target.Value),
                    ])
                )
            )
            : result;
    }

    /// <summary>
    /// The state under the target name once the holder session is proven gone, on tokens of its own; null when
    /// the holder cannot be proven gone or the name cannot be read on any attempt, because nothing is then known.
    /// </summary>
    private async Task<DmsLiveTargetState?> ReadSettledTargetAsync(
        Deletion deletion,
        DmsOperationId operation
    )
    {
        if (!await ProveSessionGoneAsync(deletion.HolderSession, operation, ResolveDeletionStep))
        {
            return null;
        }

        return await ReadForRecoveryAsync<DmsLiveTargetState?>(
            operation,
            ResolveDeletionStep,
            async token => await engine.InspectAsync(deletion.Target, token),
            null
        );
    }

    /// <summary>
    /// A target left fenced needs an operator, whatever ended the delete: a failure already requiring inspection
    /// stands; one whose code allows inspection moves there; any other (a deadline, an unclassified error, a
    /// missing privilege) becomes <c>ExclusiveAccessNotEstablished</c> for inspection, the code section 8 gives a
    /// fence that could not be completed, with the original diagnostic kept.
    /// </summary>
    private static DmsProvisioningFailure ForFencedTarget(DmsProvisioningFailure failure)
    {
        if (failure.Category == DmsProvisioningFailureCategory.OperatorInspectionRequired)
        {
            return failure;
        }

        DmsProvisioningFailureCode code = DmsProvisioningFailure
            .CategoriesOf(failure.Code)
            .Contains(DmsProvisioningFailureCategory.OperatorInspectionRequired)
            ? failure.Code
            : DmsProvisioningFailureCode.ExclusiveAccessNotEstablished;
        return DmsProvisioningFailure.Create(
            code,
            DmsProvisioningFailureCategory.OperatorInspectionRequired,
            failure.Diagnostic
        );
    }

    /// <summary>Where the selected target stands in a delete (D-12, 6.6).</summary>
    private enum DeletionState
    {
        /// <summary>No selected-target mutation was sent, or the one sent proved to change nothing.</summary>
        None,

        /// <summary>The fence was sent and no definite outcome has settled it.</summary>
        FenceInDoubt,

        /// <summary>The fence stands.</summary>
        Fenced,

        /// <summary>The reversal of the fence was sent and no definite outcome has settled it.</summary>
        UnfenceInDoubt,

        /// <summary>The fence was reversed.</summary>
        Unfenced,

        /// <summary>The drop was sent and no definite outcome has settled it.</summary>
        DropInDoubt,

        /// <summary>The target was dropped.</summary>
        Dropped,

        /// <summary>The instance is no longer under the target name; nothing of it is reported there.</summary>
        Displaced,
    }

    /// <summary>
    /// The ledger of a delete's selected-target mutations (D-12): each is recorded as it is handed to the
    /// engine and settled from the outcome it returns, observed before any loss check can discard it.
    /// </summary>
    private sealed class Deletion(DmsTargetDatabaseName target) : ITargetMutationLedger
    {
        /// <summary>The state before the mutation now in flight was recorded as dispatched.</summary>
        private DeletionState _beforeDispatch = DeletionState.None;

        public DmsTargetDatabaseName Target => target;

        public DeletionState State { get; private set; } = DeletionState.None;

        public DmsInstanceIdentity? Instance { get; private set; }

        public DmsServerSessionId HolderSession { get; private set; }

        public bool TargetMutationIssued =>
            State
                is DeletionState.FenceInDoubt
                    or DeletionState.Fenced
                    or DeletionState.UnfenceInDoubt
                    or DeletionState.DropInDoubt;

        public bool IsFenced => State == DeletionState.Fenced;

        public void FenceDispatched(DmsInstanceIdentity instance, DmsServerSessionId holderSession)
        {
            Instance = instance;
            HolderSession = holderSession;
            Dispatch(DeletionState.FenceInDoubt);
        }

        /// <summary>A definite failure leaves the fence standing only when the engine reports it as residue.</summary>
        public void ObserveFence(DmsCommandOutcome outcome)
        {
            switch (outcome)
            {
                case DmsCommandOutcome.Succeeded:
                    State = DeletionState.Fenced;
                    break;
                case DmsCommandOutcome.Failed failed:
                    State = failed switch
                    {
                        _ when failed.Residue.Any(residue => residue.Kind == DmsResidueKind.FencedTarget) =>
                            DeletionState.Fenced,
                        { Failure.Class: DmsCommandFailureClass.DoesNotExist } => DeletionState.Displaced,
                        _ => DeletionState.None,
                    };
                    break;
            }
        }

        public void UnfenceDispatched() => State = DeletionState.UnfenceInDoubt;

        public void ObserveUnfence(DmsCommandOutcome outcome)
        {
            switch (outcome)
            {
                case DmsCommandOutcome.Succeeded:
                    State = DeletionState.Unfenced;
                    break;
                case DmsCommandOutcome.Failed { Failure.Class: DmsCommandFailureClass.DoesNotExist }:
                    State = DeletionState.Displaced;
                    break;
                case DmsCommandOutcome.Failed:
                    State = DeletionState.Fenced;
                    break;
            }
        }

        public void DropDispatched() => Dispatch(DeletionState.DropInDoubt);

        /// <summary>The mutation recorded last never reached the driver: the state before it stands.</summary>
        public void NotDispatched() => State = _beforeDispatch;

        public void ObserveDrop(DmsCommandOutcome outcome)
        {
            switch (outcome)
            {
                case DmsCommandOutcome.Succeeded:
                    State = DeletionState.Dropped;
                    break;
                case DmsCommandOutcome.Failed { Failure.Class: DmsCommandFailureClass.DoesNotExist }:
                    State = DeletionState.Displaced;
                    break;
                case DmsCommandOutcome.Failed:
                    State = DeletionState.Fenced;
                    break;
            }
        }

        public void Displaced() => State = DeletionState.Displaced;

        private void Dispatch(DeletionState inDoubt)
        {
            _beforeDispatch = State;
            State = inDoubt;
        }
    }
}
