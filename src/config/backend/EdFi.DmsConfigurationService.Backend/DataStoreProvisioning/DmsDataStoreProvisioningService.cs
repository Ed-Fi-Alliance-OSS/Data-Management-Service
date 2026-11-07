// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.ExceptionServices;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
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
/// released; the lock is released on every path. This build delivers the shell, a complete create path
/// (6.3 steps 2 to 10), and the owned delete (6.6): the artifact is staged and compared with the pin before the lock, the staging
/// database is created and claimed on the holder session, the artifact is restored into it, the result is
/// validated in full on an instance-bound session, renamed into the target on the holder session, re-checked,
/// and marked, with compensation for a marker proven uncommitted; the staging database is dropped on every
/// exit that does not publish it, bounded (D-19), with what remains reported as residue. A target found
/// holding the name, by the preamble, by the last inspection before the rename, or by the rename itself, is
/// reconciled (6.5) by reads only, against the full validation set of the trusted template. A delete fences
/// an owned target, proves exclusivity, re-verifies ownership and instance, and drops it on the holder
/// session, reversing the fence on every failure it can. Before any of that, after the boundary, the
/// administrative connection string is resolved again (6.1 step 3) so the engine connects with the current
/// secret values.
/// </summary>
public sealed partial class DmsDataStoreProvisioningService(
    IDmsDataStoreProvisionerReadiness readiness,
    IDmsTargetEngine engine,
    ITemplateArtifactCatalog catalog,
    IOptions<DmsDataStoreProvisioningOptions> options,
    ProtectedDatabaseNames protectedNames,
    IDmsAdminConnectionSource adminConnection,
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
    public const string EngineCheckStep = "EngineCheck";
    public const string InspectStagingStep = "InspectStaging";
    public const string CreateStagingStep = "CreateStaging";
    public const string RestoreIntoStagingStep = "RestoreIntoStaging";
    public const string ResolveRestoreStep = "ResolveRestore";
    public const string OpenStagingSessionStep = "OpenStagingSession";
    public const string ReadIdentityStep = "ReadIdentity";
    public const string ReadTargetFactsStep = "ReadTargetFacts";
    public const string ValidateStagingStep = "ValidateStaging";
    public const string ReleaseStagingSessionStep = "ReleaseStagingSession";
    public const string DropStagingStep = "DropStaging";

    internal static readonly EventId OperationFailed = new(1438_10, nameof(OperationFailed));
    internal static readonly EventId LockLost = new(1438_11, nameof(LockLost));
    internal static readonly EventId StagingResidue = new(1438_12, nameof(StagingResidue));
    internal static readonly EventId RecoveryUnproven = new(1438_13, nameof(RecoveryUnproven));

    private static readonly TimeSpan[] _cleanupBackoff =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(2),
    ];

    /// <summary>The D-23 seams; production keeps the no-op.</summary>
    internal IDmsProvisioningTestHooks TestHooks { get; init; } = NoOpProvisioningTestHooks.Instance;

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

        if (await ResolveAdminConnectionAsync(cancellationToken) is { } unresolved)
        {
            return new DmsDataStoreCreateResult.Failure(unresolved, DmsCleanupReport.Clean);
        }

        // 6.2 runs before the lock so no lock is held during file work (6.3 step 1). The operation id names
        // the private directory, the staging database, and the holder session alike.
        DmsOperationId operation = DmsOperationId.New();
        TemplateArtifactStaging staging = await catalog.StageAsync(
            request.Template,
            operation,
            cancellationToken
        );
        if (staging is TemplateArtifactStaging.Failure refused)
        {
            return new DmsDataStoreCreateResult.Failure(refused.Detail, refused.Cleanup);
        }

        TrustedTemplateArtifact artifact = ((TemplateArtifactStaging.Staged)staging).Artifact;
        StagingCleanup cleanup = new(operation.StagingDatabaseName);
        Publication publication = new(
            request.TargetName,
            operation.StagingDatabaseName,
            request.ExpectedSourceIdentity
        );
        DmsDataStoreCreateResult? result;
        OperationCanceledException? cancellation = null;
        try
        {
            if (artifact.Pin != request.PinnedArtifact)
            {
                throw new DmsProvisioningFailedException(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.ArtifactChangedSinceCreate,
                        DmsProvisioningDiagnostic.ForOperation(ResolveArtifactStep)
                    )
                );
            }

            // 6.2 step 8: the artifact is extracted while hashing; a mismatch is ArtifactTampered.
            StagedArtifact stagedArtifact = await artifact.ExtractArtifactAsync(cancellationToken);
            (result, cancellation) = await CreateUnderLockAsync(
                request,
                operation,
                artifact,
                stagedArtifact,
                cleanup,
                publication,
                cancellationToken
            );
        }
        catch (DmsProvisioningFailedException stepFailure)
        {
            // A refusal before the lock: nothing was created on the server.
            result = Fail(
                static (failure, cleanup) => new DmsDataStoreCreateResult.Failure(failure, cleanup),
                operation,
                CreateOperationName,
                stepFailure.Failure,
                DmsCleanupReport.Clean
            );
        }
        finally
        {
            // 6.7 cleanup on every exit, after the lock is released: the staging database is unique to this
            // operation, so it may be dropped on any session, also after loss, unless it was renamed into the
            // target or a rename is still in doubt; then the private directory.
            publication.ApplyTo(cleanup);
            await cleanup.RunAsync(
                engine,
                options.Value,
                timeProvider,
                logger,
                operation,
                session => ProveSessionGoneAsync(session, operation, CreateStagingStep)
            );
            cleanup.Add(await ReleaseArtifactAsync(artifact));
        }

        if (cancellation is not null && result is not DmsDataStoreCreateResult.Created)
        {
            ExceptionDispatchInfo.Throw(cancellation);
        }

        // What cleanup could not remove rides every result of this create (6.11); it never changes the class.
        return WithCleanup(
            result ?? throw new InvalidOperationException("The create ended without a result."),
            cleanup.Report
        );
    }

    /// <summary>
    /// Releases the private directory with <c>CleanupAttempts</c> tries and the D-19 backoff while residue
    /// remains, so a password file the replay could not delete gets every bounded chance; only the final
    /// attempt's residue is logged and returned, a credential file at Critical.
    /// </summary>
    private async Task<IReadOnlyList<DmsCleanupResidue>> ReleaseArtifactAsync(
        TrustedTemplateArtifact artifact
    )
    {
        int attempts = Math.Max(1, options.Value.CleanupAttempts);
        for (int attempt = 1; attempt < attempts; attempt++)
        {
            if (artifact.Release(logResidue: false).Count == 0)
            {
                return [];
            }

            await Task.Delay(
                _cleanupBackoff[Math.Min(attempt - 1, _cleanupBackoff.Length - 1)],
                timeProvider
            );
        }

        return artifact.Release(logResidue: true);
    }

    private static DmsDataStoreCreateResult WithCleanup(
        DmsDataStoreCreateResult result,
        DmsCleanupReport residue
    )
    {
        if (!residue.HasResidue)
        {
            return result;
        }

        return result switch
        {
            DmsDataStoreCreateResult.Failure failure => failure with
            {
                Cleanup = Merge(failure.Cleanup, residue),
            },
            DmsDataStoreCreateResult.Created created => created with
            {
                Cleanup = Merge(created.Cleanup, residue),
            },
            DmsDataStoreCreateResult.Reconciled reconciled => reconciled with
            {
                Cleanup = Merge(reconciled.Cleanup, residue),
            },
            _ => throw new InvalidOperationException("Unknown create result."),
        };
    }

    private static DmsCleanupReport Merge(DmsCleanupReport first, DmsCleanupReport second) =>
        new([.. first.Residue, .. second.Residue]);

    /// <summary>
    /// The staging stage (6.3 steps 2 to 6) under the lock: the engine floor, the staging name free, the
    /// staging database created and claimed on the holder session, the artifact restored into it with the
    /// outcome protocol, full validation on an instance-bound session, the session released with proof, and
    /// the selected target name is never addressed. Returns the validated instance and the package identity.
    /// </summary>
    private async Task<StagedCandidate> StageAsync(
        LockScope scope,
        DmsDataStoreCreateRequest request,
        TrustedTemplateArtifact artifact,
        StagedArtifact stagedArtifact,
        StagingCleanup cleanup
    )
    {
        DmsDataStoreProvisioningOptions settings = options.Value;
        DmsOperationId operation = scope.Operation;
        DmsDatabaseName stage = operation.StagingDatabaseName;

        // 6.3 step 2: the live major must carry the manifest's (D-9); the other floors are readiness probes.
        if (scope.ServerFacts.MajorVersion < ManifestMajorOf(artifact.Manifest))
        {
            throw StepFailed(DmsProvisioningFailureCode.EngineIncompatible, EngineCheckStep);
        }

        // 6.3 step 3: a generated name already in use is transient; a retry draws a new operation id.
        DmsLiveTargetState stagingState = await scope.ReadAsync(
            InspectStagingStep,
            token => engine.InspectAsync(stage, token)
        );
        if (stagingState is not DmsLiveTargetState.Absent)
        {
            throw StepFailed(DmsProvisioningFailureCode.StagingNameCollision, InspectStagingStep);
        }

        // 6.3 step 4 on the holder session.
        DmsProvisionClaim claim = new(operation, request.ExpectedSourceIdentity);
        DmsInstanceIdentity stagingInstance = await CreateStagingAsync(scope, stage, claim, cleanup);

        // 6.3 step 5: the replay and its outcome; true when the replay's fate is read by the validation.
        bool restoreInDoubt = await RestoreAsync(scope, stage, stagedArtifact, stagingInstance, cleanup);

        // 6.3 step 6 on an instance-bound session; for a restore in doubt, the reads also decide its fate.
        (DmsServerSessionId stagingSession, Guid packageIdentity) = await ValidateStagingAsync(
            scope,
            request,
            artifact.Manifest,
            stage,
            stagingInstance,
            restoreInDoubt,
            cleanup
        );

        // D-22: our own session is proven gone before the staging database is addressed again.
        DmsSessionTermination released = await scope.CallAsync(
            ReleaseStagingSessionStep,
            settings.SessionTerminationTimeout + settings.CommandTimeout,
            token => engine.TerminateSessionAsync(stagingSession, settings.SessionTerminationTimeout, token),
            observe: null
        );
        if (released is DmsSessionTermination.NotTerminated)
        {
            throw StepFailed(
                DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                ReleaseStagingSessionStep
            );
        }

        return new StagedCandidate(stagingInstance, packageIdentity);
    }

    /// <summary>6.3 step 4: the staging database created and claimed on the holder session, registered for cleanup.</summary>
    private async Task<DmsInstanceIdentity> CreateStagingAsync(
        LockScope scope,
        DmsDatabaseName stage,
        DmsProvisionClaim claim,
        StagingCleanup cleanup
    )
    {
        // The create is this operation's, and unproven, from the moment it is sent: whatever ends the step,
        // cleanup accounts for the name, and only a returned definite outcome or a verified termination of
        // the holder session it ran on settles it (6.7, 6.11, D-7b).
        cleanup.Pending(claim, scope.Holder.Session);
        DmsCommandOutcome<DmsInstanceIdentity> created = await scope.CallAsync(
            CreateStagingStep,
            options.Value.CommandTimeout,
            token => engine.CreateStagingAsync(scope.Holder, stage, claim, token),
            cleanup.ObserveCreate
        );
        switch (created)
        {
            case DmsCommandOutcome<DmsInstanceIdentity>.Succeeded succeeded:
                return succeeded.Value;
            case DmsCommandOutcome<DmsInstanceIdentity>.Failed failed:
                throw StepFailed(CreateStagingFailureCodeOf(failed.Failure), failed.Failure.Diagnostic);
            case DmsCommandOutcome<DmsInstanceIdentity>.Unknown:
                // The holder is in doubt: the operation ends LockLost (transient, no selected-target mutation),
                // and cleanup, once the holder is released, proves it gone before it decides about the name.
                throw new DmsProvisioningFailedException(scope.LockLostFailure());
            default:
                throw new InvalidOperationException("Unknown create-staging outcome.");
        }
    }

    /// <summary>
    /// 6.3 step 5: the replay, bounded by the engine (the scope's deadline is a backstop beyond it), and its
    /// outcome. Returns true when the outcome is unresolved and the validation's reads must decide the
    /// replay's fate; throws for a definite failure or an outcome nothing can resolve.
    /// </summary>
    private async Task<bool> RestoreAsync(
        LockScope scope,
        DmsDatabaseName stage,
        StagedArtifact stagedArtifact,
        DmsInstanceIdentity stagingInstance,
        StagingCleanup cleanup
    )
    {
        DmsDataStoreProvisioningOptions settings = options.Value;
        DmsOperationId operation = scope.Operation;

        // The replay's fate is open from the moment it is dispatched: the drop is withheld first, and only
        // a returned definite or quiescent outcome, or a later proven termination, lifts it. An exception,
        // a cancellation, or a loss before any outcome returns therefore leaves the staging database as
        // residue, never dropped without proof (D-7b).
        cleanup.Withhold();
        DmsCommandOutcome<DmsInstanceIdentity> restored = await scope.CallAsync(
            RestoreIntoStagingStep,
            settings.RestoreTimeout + settings.SessionTerminationTimeout + settings.CommandTimeout,
            token => engine.RestoreIntoStagingAsync(stage, stagedArtifact, operation, token),
            outcome => cleanup.Observe(outcome.WithoutValue())
        );
        if (
            restored is DmsCommandOutcome<DmsInstanceIdentity>.Succeeded
            && TestHooks.ShouldInjectFault(DmsProvisioningFaultPoint.DropConnectionDuringRestore, operation)
        )
        {
            // D-23: the server committed, but the client lost the result; the session is gone already.
            restored = new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(null) { Quiescent = true };
        }

        await TestHooks.AtAsync(DmsProvisioningHookPoint.AfterRestoreStarted, operation, scope.CallerToken);
        switch (restored)
        {
            case DmsCommandOutcome<DmsInstanceIdentity>.Succeeded succeeded:
                if (!succeeded.Value.IsSameStableInstance(stagingInstance))
                {
                    throw StepFailed(DmsProvisioningFailureCode.InstanceChanged, RestoreIntoStagingStep);
                }

                return false;
            case DmsCommandOutcome<DmsInstanceIdentity>.Failed failed:
                ThrowRestoreFailure(failed.Failure, scope.CallerToken);
                return false;
            case DmsCommandOutcome<DmsInstanceIdentity>.Unknown unresolvable
                when unresolvable.Residue.Count > 0 || unresolvable is { Session: null, Quiescent: false }:
                // Nothing names a session to prove gone, or the engine reports something it could not end,
                // such as a replay process. The outcome cannot be resolved here and nothing destructive may
                // run. The observation above already withheld the drop and kept the residue.
                throw StepFailed(DmsProvisioningFailureCode.OutcomeUnknown, ResolveRestoreStep);
            case DmsCommandOutcome<DmsInstanceIdentity>.Unknown unknown:
                // D-7b: the engine already proved the replay quiescent, or named the session it could not
                // prove gone; that session is given one more attempt on a recovery token of its own, and
                // only a proven termination lifts the withheld drop. The state is then read by the
                // validation that follows.
                if (unknown.Session is { } session)
                {
                    if (!await ProveSessionGoneAsync(session, operation, ResolveRestoreStep))
                    {
                        throw StepFailed(DmsProvisioningFailureCode.OutcomeUnknown, ResolveRestoreStep);
                    }

                    cleanup.Permit();
                }

                return true;
            default:
                throw new InvalidOperationException("Unknown restore outcome.");
        }
    }

    /// <summary>
    /// 6.3 step 6: identity and facts on an instance-bound session, judged by the validator under the
    /// staging rule. For a restore in doubt, both singleton tables missing proves the replay rolled back
    /// (a transient restore failure, or the caller's cancellation), one missing leaves the outcome
    /// unresolved and the staging database in place; both present means it committed. Returns the
    /// session's server id, for the proof that it is gone, and the package's identity value, which the
    /// validator has proven a valid identity that differs from the expected one.
    /// </summary>
    private async Task<(DmsServerSessionId Session, Guid PackageIdentity)> ValidateStagingAsync(
        LockScope scope,
        DmsDataStoreCreateRequest request,
        RestoreManifest manifest,
        DmsDatabaseName stage,
        DmsInstanceIdentity stagingInstance,
        bool restoreInDoubt,
        StagingCleanup cleanup
    )
    {
        DmsSession? session = await OpenOwnedSessionAsync(
            scope,
            OpenStagingSessionStep,
            stage,
            stagingInstance,
            DmsProvisioningFailureCode.AdminPrivilegeMissing
        );
        if (session is null)
        {
            throw StepFailed(DmsProvisioningFailureCode.InstanceChanged, OpenStagingSessionStep);
        }

        await using (session)
        {
            DmsIdentityRead identity = await scope.ReadAsync(
                ReadIdentityStep,
                token => engine.ReadIdentityAsync(session, token)
            );
            ThrowIfAccessDenied(identity, DmsProvisioningFailureCode.AdminPrivilegeMissing, ReadIdentityStep);
            DmsTargetFacts facts = await ReadFactsAsync(
                scope,
                ReadTargetFactsStep,
                session,
                DmsProvisioningFailureCode.AdminPrivilegeMissing
            );
            if (restoreInDoubt)
            {
                bool identityMissing =
                    identity is DmsIdentityRead.Invalid { Problem: DmsIdentityReadProblem.TableMissing };
                bool effectiveSchemaMissing =
                    facts.EffectiveSchema is DmsEffectiveSchemaRead.Invalid
                    {
                        Problem: DmsEffectiveSchemaReadProblem.TableMissing,
                    };
                if (identityMissing && effectiveSchemaMissing)
                {
                    scope.CallerToken.ThrowIfCancellationRequested();
                    throw new DmsProvisioningFailedException(
                        DmsProvisioningFailure.Create(
                            DmsProvisioningFailureCode.RestoreFailed,
                            DmsProvisioningFailureCategory.Transient,
                            DmsProvisioningDiagnostic.ForOperation(ResolveRestoreStep)
                        )
                    );
                }

                if (identityMissing || effectiveSchemaMissing)
                {
                    cleanup.Withhold();
                    throw StepFailed(DmsProvisioningFailureCode.OutcomeUnknown, ResolveRestoreStep);
                }
            }

            DmsTargetValidation verdict = DmsTargetValidator.Validate(
                facts,
                identity,
                manifest,
                options.Value.Compatibility?.Projects ?? [],
                new DmsTargetValidationRule.Staging(request.ExpectedSourceIdentity)
            );
            if (verdict is DmsTargetValidation.Invalid invalid)
            {
                throw StepFailed(invalid.Code, ValidateStagingStep + "." + invalid.Check);
            }

            if (identity is not DmsIdentityRead.Found package)
            {
                throw StepFailed(
                    DmsProvisioningFailureCode.PackageIdentityInvalid,
                    ValidateStagingStep + "." + DmsTargetValidator.IdentityCheck
                );
            }

            return (session.ServerSession, package.SourceIdentity);
        }
    }

    /// <summary>
    /// Opens an instance-bound session through <paramref name="scope"/> and owns it from the moment the engine
    /// returns it: when the scope then discards the result because loss was declared while the session opened,
    /// the session is disposed here, once, before the loss propagates. Null when the engine refused because the
    /// name no longer resolves to <paramref name="instance"/>; otherwise the caller owns the returned session.
    /// A definite access refusal ends the step with <paramref name="accessDenied"/> and the refusal's
    /// sanitized diagnostic, re-labelled with <paramref name="step"/>.
    /// </summary>
    private async Task<DmsSession?> OpenOwnedSessionAsync(
        LockScope scope,
        string step,
        DmsDatabaseName name,
        DmsInstanceIdentity instance,
        DmsProvisioningFailureCode accessDenied
    )
    {
        DmsSession? returned = null;
        DmsSessionOpenResult opened;
        try
        {
            opened = await scope.CallAsync(
                step,
                options.Value.CommandTimeout,
                token => engine.OpenSessionAsync(name, instance, token),
                result => returned = (result as DmsSessionOpenResult.Opened)?.Session
            );
        }
        catch (Exception) when (returned is not null)
        {
            await returned.DisposeAsync();
            throw;
        }

        return opened switch
        {
            DmsSessionOpenResult.Opened open => open.Session,
            DmsSessionOpenResult.AccessDenied refused => throw StepFailed(
                accessDenied,
                AtStep(refused.Diagnostic, step)
            ),
            _ => null,
        };
    }

    /// <summary>
    /// An identity read the server definitely refused for lack of a privilege ends the step with
    /// <paramref name="code"/> and the refusal's sanitized diagnostic, before anything else is read.
    /// </summary>
    private static void ThrowIfAccessDenied(
        DmsIdentityRead identity,
        DmsProvisioningFailureCode code,
        string step
    )
    {
        if (identity is DmsIdentityRead.Invalid { Problem: DmsIdentityReadProblem.AccessDenied } denied)
        {
            throw StepFailed(
                code,
                denied.Diagnostic is { } diagnostic
                    ? AtStep(diagnostic, step)
                    : DmsProvisioningDiagnostic.ForOperation(step)
            );
        }
    }

    /// <summary>The engine's type chain and provider code, labelled with the orchestrator's step.</summary>
    private static DmsProvisioningDiagnostic AtStep(DmsProvisioningDiagnostic diagnostic, string step) =>
        new(diagnostic.ExceptionTypeChain, diagnostic.ProviderErrorCode, step);

    /// <summary>
    /// The facts read under <paramref name="step"/>. A read the server definitely refused for lack of a
    /// privilege (<see cref="DmsAccessDeniedException"/>, which the scope reports as an unknown outcome) ends
    /// the step with <paramref name="accessDenied"/> and the refusal's sanitized diagnostic. The scope's loss
    /// check runs first, so a refusal raised alongside loss is still loss; the caller's cancellation, a
    /// deadline and every other exception keep the scope's classification.
    /// </summary>
    private async Task<DmsTargetFacts> ReadFactsAsync(
        LockScope scope,
        string step,
        DmsSession session,
        DmsProvisioningFailureCode accessDenied
    )
    {
        try
        {
            return await scope.ReadAsync(step, token => engine.ReadTargetFactsAsync(session, token));
        }
        catch (DmsProvisioningFailedException failure)
            when (failure.InnerException is DmsAccessDeniedException denied)
        {
            throw StepFailed(accessDenied, AtStep(denied.Diagnostic, step));
        }
    }

    /// <summary>
    /// Recovery for an unknown outcome (6.10): terminates <paramref name="session"/> with verification on a
    /// token of its own, bounded by <c>SessionTerminationTimeout</c> plus <c>CommandTimeout</c>, so neither
    /// the caller's cancellation nor the holder's loss can cut it short. True only for a proven-gone verdict;
    /// a refusal, a timeout, or any exception is no proof, and only the exception's type chain is logged.
    /// </summary>
    private async Task<bool> ProveSessionGoneAsync(
        DmsServerSessionId session,
        DmsOperationId operation,
        string step
    )
    {
        DmsDataStoreProvisioningOptions settings = options.Value;
        using CancellationTokenSource bound = new(
            settings.SessionTerminationTimeout + settings.CommandTimeout,
            timeProvider
        );
        string failure;
        try
        {
            DmsSessionTermination verdict = await engine.TerminateSessionAsync(
                session,
                settings.SessionTerminationTimeout,
                bound.Token
            );
            return verdict is not DmsSessionTermination.NotTerminated;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Only the type chain leaves the catch: a driver error may quote the connection.
            failure = DmsProvisioningDiagnostic.TypeChain(exception);
        }

        LogRecoveryUnproven(operation, step, failure);
        return false;
    }

    private static DmsProvisioningFailureCode CreateStagingFailureCodeOf(DmsCommandFailure failure) =>
        failure.Class switch
        {
            DmsCommandFailureClass.DuplicateName => DmsProvisioningFailureCode.StagingNameCollision,
            DmsCommandFailureClass.PrivilegeMissing => DmsProvisioningFailureCode.AdminPrivilegeMissing,
            _ => DmsProvisioningFailureCode.ServerRejected,
        };

    /// <summary>6.3 step 5 for a definite restore failure: the engine's fixed operation names what ended the replay.</summary>
    private static void ThrowRestoreFailure(DmsCommandFailure failure, CancellationToken callerToken)
    {
        switch (failure.Diagnostic.Operation)
        {
            case DmsEngineOperations.RestoreCancelled:
                throw new OperationCanceledException(
                    "The replay was cancelled and rolled back.",
                    callerToken
                );
            case DmsEngineOperations.RestoreTimedOut:
                throw StepFailed(DmsProvisioningFailureCode.TimedOut, failure.Diagnostic);
            case DmsEngineOperations.RestoreRolledBack:
                throw new DmsProvisioningFailedException(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.RestoreFailed,
                        DmsProvisioningFailureCategory.Transient,
                        failure.Diagnostic
                    )
                );
            case DmsEngineOperations.RoleStateUnsafe:
                throw StepFailed(DmsProvisioningFailureCode.RoleStateUnsafe, failure.Diagnostic);
            default:
                throw StepFailed(
                    failure.Class == DmsCommandFailureClass.PrivilegeMissing
                        ? DmsProvisioningFailureCode.AdminPrivilegeMissing
                        : DmsProvisioningFailureCode.RestoreFailed,
                    failure.Diagnostic
                );
        }
    }

    private static int ManifestMajorOf(RestoreManifest manifest)
    {
        ReadOnlySpan<char> version = manifest.EngineVersion;
        int dot = version.IndexOf('.');
        if (dot >= 0)
        {
            version = version[..dot];
        }

        // An unparsable major fails the floor: the manifest reader accepts only well-formed versions.
        return int.TryParse(version, out int major) ? major : int.MaxValue;
    }

    /// <summary>The common preamble under the lock (6.1 steps 5 and 6).</summary>
    private async Task<DmsLiveTargetState> PreambleAsync(LockScope scope, DmsTargetDatabaseName target)
    {
        // The facts feed the engine checks of later stages (6.3 step 2, 6.4 step 2).
        scope.ServerFacts = await scope.ReadAsync(ReadServerFactsStep, engine.ReadServerFactsAsync);
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
        DmsOperationId operation,
        DmsCleanupReport priorCleanup,
        Func<LockScope, Task<TResult>> body,
        Func<DmsProvisioningFailure, DmsCleanupReport, TResult> failure,
        CancellationToken cancellationToken
    )
    {
        DmsDataStoreProvisioningOptions settings = options.Value;
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

        LockScope scope = new(holder, operation, settings.CommandTimeout, timeProvider, cancellationToken);
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

    /// <summary>
    /// 6.1 step 3: the administrative connection string is resolved again for every operation, after the
    /// boundary and before any file work, server contact, or the lock, so a rotated secret reaches the next
    /// operation through the secret cache. A failure is transient <c>SecretResolutionFailed</c>.
    /// </summary>
    private async Task<DmsProvisioningFailure?> ResolveAdminConnectionAsync(
        CancellationToken cancellationToken
    ) =>
        await adminConnection.ResolveAsync(cancellationToken) is DmsAdminConnectionResolution.Failed failed
            ? DmsProvisioningFailure.Create(
                DmsProvisioningFailureCode.SecretResolutionFailed,
                failed.Diagnostic
            )
            : null;

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

    private static DmsProvisioningFailedException StepFailed(
        DmsProvisioningFailureCode code,
        DmsProvisioningDiagnostic diagnostic
    ) => new(DmsProvisioningFailure.Create(code, diagnostic));

    /// <summary>
    /// An operation's record of the selected-target mutations it sent (D-12): <c>LockLost</c> is transient
    /// while none may have taken effect, and an inspection failure once one may have.
    /// </summary>
    private interface ITargetMutationLedger
    {
        bool TargetMutationIssued { get; }
    }

    /// <summary>
    /// The staging database this create owns (6.7): dropped on every exit with bounded attempts (D-19)
    /// unless the drop was withheld because the replay's fate is open (D-7b); what cannot be dropped, or
    /// is deliberately left, is residue (6.11) alongside what the artifact release could not remove.
    /// </summary>
    private sealed class StagingCleanup(DmsDatabaseName stage)
    {
        private readonly List<DmsCleanupResidue> _residue = [];
        private DmsInstanceIdentity? _instance;
        private DmsProvisionClaim? _pendingClaim;
        private DmsServerSessionId? _pendingSession;
        private bool _withheld;

        public DmsCleanupReport Report => new([.. _residue]);

        public void Created(DmsInstanceIdentity instance) => _instance = instance;

        /// <summary>
        /// The staging database was renamed into the target: nothing of it remains under the staging name, so
        /// there is neither an instance to drop nor a create to account for there.
        /// </summary>
        public void Published()
        {
            _instance = null;
            _pendingClaim = null;
        }

        /// <summary>
        /// A create carrying <paramref name="claim"/> is about to be sent on <paramref name="session"/>: until a
        /// definite outcome or a verified termination of that session settles it, the drop is withheld and
        /// cleanup accounts for the name.
        /// </summary>
        public void Pending(DmsProvisionClaim claim, DmsServerSessionId session)
        {
            _pendingClaim = claim;
            _pendingSession = session;
            Withhold();
        }

        /// <summary>
        /// Settles the create from its returned outcome before any loss check can discard it: a success is
        /// the instance to drop; a definite failure leaves only what the engine reported as residue; an
        /// unknown outcome stays pending and keeps the drop withheld.
        /// </summary>
        public void ObserveCreate(DmsCommandOutcome<DmsInstanceIdentity> outcome)
        {
            Observe(outcome.WithoutValue());
            switch (outcome)
            {
                case DmsCommandOutcome<DmsInstanceIdentity>.Succeeded succeeded:
                    Created(succeeded.Value);
                    break;
                case DmsCommandOutcome<DmsInstanceIdentity>.Failed:
                    _pendingClaim = null;
                    break;
            }
        }

        /// <summary>No destructive recovery: the staging database is reported, not dropped.</summary>
        public void Withhold() => _withheld = true;

        /// <summary>The outcome that withheld the drop was resolved with proof (D-7b): cleanup may drop again.</summary>
        public void Permit() => _withheld = false;

        /// <summary>
        /// Records an engine outcome as soon as it is returned, for a command whose dispatch withheld the
        /// drop: its residue is kept; a definite or quiescent outcome lifts the withhold; an unknown outcome
        /// that is not quiescent keeps it until a recovery proves the command's session gone.
        /// </summary>
        public void Observe(DmsCommandOutcome outcome)
        {
            Add(outcome.Residue);
            if (outcome is DmsCommandOutcome.Unknown { Quiescent: false })
            {
                Withhold();
            }
            else
            {
                Permit();
            }
        }

        public void Add(IReadOnlyList<DmsCleanupResidue> residue) => _residue.AddRange(residue);

        public async Task RunAsync(
            IDmsTargetEngine engine,
            DmsDataStoreProvisioningOptions settings,
            TimeProvider timeProvider,
            ILogger logger,
            DmsOperationId operation,
            Func<DmsServerSessionId, Task<bool>> proveSessionGone
        )
        {
            if (_instance is null && _pendingClaim is not null)
            {
                // An unsettled create is proven over only by its session being gone; the holder is released
                // by now, so a verified termination is usually immediate.
                if (_withheld && _pendingSession is { } session && await proveSessionGone(session))
                {
                    Permit();
                }

                string? unaccounted = await ClaimPendingAsync(engine, settings.CommandTimeout, timeProvider);
                if (unaccounted is not null)
                {
                    ReportResidue(logger, operation, unaccounted);
                    return;
                }
            }

            if (_instance is null)
            {
                return;
            }

            if (!_withheld)
            {
                for (int attempt = 0; attempt < settings.CleanupAttempts; attempt++)
                {
                    if (attempt > 0)
                    {
                        await Task.Delay(
                            _cleanupBackoff[Math.Min(attempt - 1, _cleanupBackoff.Length - 1)],
                            timeProvider
                        );
                    }

                    if (await TryDropAsync(engine, settings.CommandTimeout, timeProvider))
                    {
                        return;
                    }
                }
            }

            ReportResidue(logger, operation, _withheld ? "outcome unresolved" : "drop failed");
        }

        /// <summary>
        /// What an unsettled create left under the staging name: a database carrying this operation's claim
        /// becomes the instance to drop (or to keep, while the drop is withheld). Returns the reason the name
        /// must be reported instead: it could not be inspected, it holds a database this operation cannot
        /// prove its own, or it is empty while an unproven create might still commit. An empty name with
        /// nothing in doubt returns null: nothing was created.
        /// </summary>
        private async Task<string?> ClaimPendingAsync(
            IDmsTargetEngine engine,
            TimeSpan commandTimeout,
            TimeProvider timeProvider
        )
        {
            using CancellationTokenSource bound = new(commandTimeout, timeProvider);
            DmsLiveTargetState state;
            try
            {
                state = await engine.InspectAsync(stage, bound.Token);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return "create unsettled, name not inspected";
            }

            switch (state)
            {
                case DmsLiveTargetState.Absent:
                    return _withheld ? "create unsettled" : null;
                case DmsLiveTargetState.Present { Instance: DmsInstanceIdentity.Postgresql present }
                    when string.Equals(present.Claim, _pendingClaim!.Text, StringComparison.Ordinal):
                    _instance = present;
                    return null;
                default:
                    return "create unsettled, name holds an unclaimed database";
            }
        }

        private void ReportResidue(ILogger logger, DmsOperationId operation, string reason)
        {
            _residue.Add(new DmsCleanupResidue(DmsResidueKind.StagingDatabase, stage.Value));
            logger.LogError(
                StagingResidue,
                "DMS data store provisioning {OperationId} left its staging database {StagingDatabase} in place ({Reason})",
                operation.Value,
                stage.Value,
                reason
            );
        }

        /// <summary>One bounded drop; a definite refusal because the name holds a different instance means nothing of ours remains.</summary>
        private async Task<bool> TryDropAsync(
            IDmsTargetEngine engine,
            TimeSpan commandTimeout,
            TimeProvider timeProvider
        )
        {
            using CancellationTokenSource bound = new(commandTimeout, timeProvider);
            try
            {
                DmsCommandOutcome outcome = await engine.DropStagingAsync(stage, _instance!, bound.Token);
                return outcome
                    is DmsCommandOutcome.Succeeded
                        or DmsCommandOutcome.Failed { Failure.Class: DmsCommandFailureClass.DoesNotExist };
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Retried within the bound; the type chain alone would be loggable, the message may quote the connection.
                return false;
            }
        }
    }

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
        DmsOperationId operation,
        TimeSpan commandTimeout,
        TimeProvider timeProvider,
        CancellationToken callerToken
    )
    {
        public string CurrentStep { get; private set; } = AcquireTargetLockStep;

        public IDmsTargetLockHolder Holder => holder;

        public DmsOperationId Operation => operation;

        public CancellationToken CallerToken => callerToken;

        /// <summary>The preamble's server facts (6.1 step 5), for the engine checks of later stages.</summary>
        public DmsServerFacts ServerFacts { get; set; } = null!;

        /// <summary>The operation's ledger of selected-target mutations, which decides the category of <c>LockLost</c>.</summary>
        public ITargetMutationLedger? Ledger { get; set; }

        /// <summary>The type chain of a non-cancellation exception a read raised alongside loss, if any.</summary>
        public string? LossAccompanyingTypeChain { get; private set; }

        public Task<T> ReadAsync<T>(string step, Func<CancellationToken, Task<T>> read) =>
            CallAsync(step, commandTimeout, read, observe: null);

        /// <summary>
        /// A gated engine call under its own <paramref name="deadline"/>; a read is a call under
        /// <c>CommandTimeout</c>. <paramref name="observe"/> sees a normally returned result before the loss
        /// check can discard it, so a command whose outcome must be accounted for is never lost to loss.
        /// </summary>
        public Task<T> CallAsync<T>(
            string step,
            TimeSpan deadline,
            Func<CancellationToken, Task<T>> call,
            Action<T>? observe
        ) => CallCoreAsync(step, deadline, call, observe, callerToken);

        /// <summary>
        /// A call that the caller's cancellation must not skip, such as compensation (6.10): bounded by
        /// <paramref name="deadline"/> and stopped by loss, but not linked to the caller's token.
        /// </summary>
        public Task<T> RecoveryCallAsync<T>(
            string step,
            TimeSpan deadline,
            Func<CancellationToken, Task<T>> call,
            Action<T>? observe
        ) => CallCoreAsync(step, deadline, call, observe, CancellationToken.None);

        private async Task<T> CallCoreAsync<T>(
            string step,
            TimeSpan deadline,
            Func<CancellationToken, Task<T>> call,
            Action<T>? observe,
            CancellationToken caller
        )
        {
            CurrentStep = step;

            // Caller cancellation, or loss declared since the previous read, stops this one before anything
            // is dispatched.
            caller.ThrowIfCancellationRequested();
            ThrowIfLost();

            using CancellationTokenSource deadlineSource = new(deadline, timeProvider);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                caller,
                holder.LostToken,
                deadlineSource.Token
            );
            T result;
            try
            {
                result = await call(linked.Token);
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
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
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
            // may be gone, so it is discarded and nothing further is dispatched, once it has been observed.
            observe?.Invoke(result);
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
        /// (D-12); once the operation's ledger records one whose effect is not proven absent, it is an
        /// inspection failure, and what the mutation did is settled after the lock is released.
        /// </summary>
        public DmsProvisioningFailure LockLostFailure() =>
            DmsProvisioningFailure.Create(
                DmsProvisioningFailureCode.LockLost,
                Ledger?.TargetMutationIssued == true
                    ? DmsProvisioningFailureCategory.OperatorInspectionRequired
                    : DmsProvisioningFailureCategory.Transient,
                DmsProvisioningDiagnostic.ForOperation(CurrentStep)
            );
    }
}
