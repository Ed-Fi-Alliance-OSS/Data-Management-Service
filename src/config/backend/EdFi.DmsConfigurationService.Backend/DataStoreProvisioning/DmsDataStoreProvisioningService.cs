// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
/// released; the lock is released on every path. This build delivers the shell and the staging stage of a
/// create (6.3 steps 2 to 7): the artifact is staged and compared with the pin before the lock, the staging
/// database is created and claimed on the holder session, the artifact is restored into it, the result is
/// validated in full on an instance-bound session, and the staging database is dropped on every exit that
/// does not publish it, bounded (D-19), with what remains reported as residue. The stages that mutate the
/// selected target (publication, reconcile validation, owned deletion) arrive with Steps 3.6 to 3.8: the
/// orchestrator throws <see cref="NotImplementedException"/> where they begin, after cleanup and after the
/// lock is released, and is not registered as the provisioner until they exist.
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
        DmsDataStoreCreateResult result;
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
            result = await RunUnderTargetLockAsync<DmsDataStoreCreateResult>(
                request.TargetName,
                CreateOperationName,
                operation,
                DmsCleanupReport.Clean,
                async scope =>
                {
                    DmsLiveTargetState state = await PreambleAsync(scope, request.TargetName);
                    if (state is DmsLiveTargetState.Present)
                    {
                        throw NotYetImplemented("reconcile (6.5)", "Step 3.7");
                    }

                    await StageAsync(scope, request, artifact, stagedArtifact, cleanup);
                    throw NotYetImplemented("publication (6.3 steps 8 to 10)", "Step 3.6");
                },
                static (failure, cleanup) => new DmsDataStoreCreateResult.Failure(failure, cleanup),
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
            // operation, so it may be dropped on any session, also after loss; then the private directory.
            await cleanup.RunAsync(engine, options.Value, timeProvider, logger, operation);
            cleanup.Add(artifact.Release());
        }

        // What cleanup could not remove rides every result of this create (6.11); it never changes the class.
        return WithCleanup(result, cleanup.Report);
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
    /// The staging stage (6.3 steps 2 to 7) under the lock: the engine floor, the staging name free, the
    /// staging database created and claimed on the holder session, the artifact restored into it with the
    /// outcome protocol, full validation on an instance-bound session, the session released with proof, and
    /// the target re-inspected; ends at the <c>BeforeRename</c> hook. The selected target name is never
    /// addressed by anything but an inspection.
    /// </summary>
    private async Task StageAsync(
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
        DmsServerSessionId stagingSession = await ValidateStagingAsync(
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
            token => engine.TerminateSessionAsync(stagingSession, settings.SessionTerminationTimeout, token)
        );
        if (released is DmsSessionTermination.NotTerminated)
        {
            throw StepFailed(
                DmsProvisioningFailureCode.ExclusiveAccessNotEstablished,
                ReleaseStagingSessionStep
            );
        }

        // 6.3 step 7: advisory; the rename is the authoritative check. A target that appeared meanwhile is
        // reconciled, never renamed over.
        DmsLiveTargetState targetState = await scope.ReadAsync(
            InspectTargetStep,
            token => engine.InspectAsync(request.TargetName, token)
        );
        if (targetState is DmsLiveTargetState.Present)
        {
            throw NotYetImplemented("reconcile after a late-appearing target (6.3 step 7, 6.5)", "Step 3.7");
        }

        await TestHooks.AtAsync(DmsProvisioningHookPoint.BeforeRename, operation, scope.CallerToken);
    }

    /// <summary>6.3 step 4: the staging database created and claimed on the holder session, registered for cleanup.</summary>
    private async Task<DmsInstanceIdentity> CreateStagingAsync(
        LockScope scope,
        DmsDatabaseName stage,
        DmsProvisionClaim claim,
        StagingCleanup cleanup
    )
    {
        DmsCommandOutcome<DmsInstanceIdentity> created = await scope.CallAsync(
            CreateStagingStep,
            options.Value.CommandTimeout,
            token => engine.CreateStagingAsync(scope.Holder, stage, claim, token)
        );
        switch (created)
        {
            case DmsCommandOutcome<DmsInstanceIdentity>.Succeeded succeeded:
                cleanup.Created(succeeded.Value);
                return succeeded.Value;
            case DmsCommandOutcome<DmsInstanceIdentity>.Failed failed:
                throw StepFailed(CreateStagingFailureCodeOf(failed.Failure), failed.Failure.Diagnostic);
            case DmsCommandOutcome<DmsInstanceIdentity>.Unknown unknown:
                await HolderInDoubtAsync(scope, unknown.Session, claim, cleanup);
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
        DmsCommandOutcome<DmsInstanceIdentity> restored = await scope.CallAsync(
            RestoreIntoStagingStep,
            settings.RestoreTimeout + settings.SessionTerminationTimeout + settings.CommandTimeout,
            token => engine.RestoreIntoStagingAsync(stage, stagedArtifact, operation, token)
        );
        if (
            restored is DmsCommandOutcome<DmsInstanceIdentity>.Succeeded
            && TestHooks.ShouldInjectFault(DmsProvisioningFaultPoint.DropConnectionDuringRestore, operation)
        )
        {
            // D-23: the server committed, but the client lost the result; the session is gone already.
            restored = new DmsCommandOutcome<DmsInstanceIdentity>.Unknown(null);
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
            case DmsCommandOutcome<DmsInstanceIdentity>.Unknown unknown:
                // D-7b: the engine already proved every replay session gone, or named the one it could
                // not; a session it could not prove gone is given one more bounded attempt, and the
                // state is read by the validation that follows. Nothing destructive happens while the
                // transaction's fate is open: the drop is withheld unless the state proves it rolled back.
                if (unknown.Session is { } session)
                {
                    DmsSessionTermination verdict = await scope.CallAsync(
                        ResolveRestoreStep,
                        settings.SessionTerminationTimeout + settings.CommandTimeout,
                        token =>
                            engine.TerminateSessionAsync(session, settings.SessionTerminationTimeout, token)
                    );
                    if (verdict is DmsSessionTermination.NotTerminated)
                    {
                        cleanup.Withhold();
                        throw StepFailed(DmsProvisioningFailureCode.OutcomeUnknown, ResolveRestoreStep);
                    }
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
    /// session's server id, for the proof that it is gone.
    /// </summary>
    private async Task<DmsServerSessionId> ValidateStagingAsync(
        LockScope scope,
        DmsDataStoreCreateRequest request,
        RestoreManifest manifest,
        DmsDatabaseName stage,
        DmsInstanceIdentity stagingInstance,
        bool restoreInDoubt,
        StagingCleanup cleanup
    )
    {
        DmsSessionOpenResult opened = await scope.ReadAsync(
            OpenStagingSessionStep,
            token => engine.OpenSessionAsync(stage, stagingInstance, token)
        );
        if (opened is not DmsSessionOpenResult.Opened { Session: { } session })
        {
            throw StepFailed(DmsProvisioningFailureCode.InstanceChanged, OpenStagingSessionStep);
        }

        await using (session)
        {
            DmsIdentityRead identity = await scope.ReadAsync(
                ReadIdentityStep,
                token => engine.ReadIdentityAsync(session, token)
            );
            DmsTargetFacts facts = await scope.ReadAsync(
                ReadTargetFactsStep,
                token => engine.ReadTargetFactsAsync(session, token)
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

            return session.ServerSession;
        }
    }

    /// <summary>
    /// An unknown outcome on the holder session (6.3 step 4): the holder may be gone, so it is terminated
    /// from a fresh session with proof, and the staging name is inspected; a database carrying this
    /// operation's claim is cleaned up. Without proof nothing is dropped and the database, if claimed,
    /// stays as residue. Either way the operation ends <c>LockLost</c>, transient: no selected-target
    /// mutation was issued.
    /// </summary>
    private async Task HolderInDoubtAsync(
        LockScope scope,
        DmsServerSessionId? holderSession,
        DmsProvisionClaim claim,
        StagingCleanup cleanup
    )
    {
        DmsDataStoreProvisioningOptions settings = options.Value;
        using CancellationTokenSource bound = new(
            settings.SessionTerminationTimeout + settings.CommandTimeout,
            timeProvider
        );
        bool proven = true;
        if (holderSession is { } session)
        {
            DmsSessionTermination verdict = await engine.TerminateSessionAsync(
                session,
                settings.SessionTerminationTimeout,
                bound.Token
            );
            proven = verdict is not DmsSessionTermination.NotTerminated;
        }

        DmsLiveTargetState state = await engine.InspectAsync(
            scope.Operation.StagingDatabaseName,
            bound.Token
        );
        if (
            state is DmsLiveTargetState.Present { Instance: DmsInstanceIdentity.Postgresql present }
            && DmsProvisionClaim.TryParse(present.Claim, out DmsProvisionClaim? found)
            && found == claim
        )
        {
            cleanup.Created(present);
            if (!proven)
            {
                cleanup.Withhold();
            }
        }
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
            DmsOperationId.New(),
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

    private static NotImplementedException NotYetImplemented(string stage, string step) =>
        new(
            $"The {stage} stage of the DMS data store provisioner arrives with {step}; this build contains the orchestrator shell and the staging stage only."
        );

    /// <summary>
    /// The staging database this create owns (6.7): dropped on every exit with bounded attempts (D-19)
    /// unless the drop was withheld because the replay's fate is open (D-7b); what cannot be dropped, or
    /// is deliberately left, is residue (6.11) alongside what the artifact release could not remove.
    /// </summary>
    private sealed class StagingCleanup(DmsDatabaseName stage)
    {
        private readonly List<DmsCleanupResidue> _residue = [];
        private DmsInstanceIdentity? _instance;
        private bool _withheld;

        public DmsCleanupReport Report => new([.. _residue]);

        public void Created(DmsInstanceIdentity instance) => _instance = instance;

        /// <summary>No destructive recovery: the staging database is reported, not dropped.</summary>
        public void Withhold() => _withheld = true;

        public void Add(IReadOnlyList<DmsCleanupResidue> residue) => _residue.AddRange(residue);

        public async Task RunAsync(
            IDmsTargetEngine engine,
            DmsDataStoreProvisioningOptions settings,
            TimeProvider timeProvider,
            ILogger logger,
            DmsOperationId operation
        )
        {
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

            _residue.Add(new DmsCleanupResidue(DmsResidueKind.StagingDatabase, stage.Value));
            logger.LogError(
                StagingResidue,
                "DMS data store provisioning {OperationId} left its staging database {StagingDatabase} in place ({Reason})",
                operation.Value,
                stage.Value,
                _withheld ? "outcome unresolved" : "drop failed"
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

        /// <summary>The type chain of a non-cancellation exception a read raised alongside loss, if any.</summary>
        public string? LossAccompanyingTypeChain { get; private set; }

        public Task<T> ReadAsync<T>(string step, Func<CancellationToken, Task<T>> read) =>
            CallAsync(step, commandTimeout, read);

        /// <summary>A gated engine call under its own <paramref name="deadline"/>; a read is a call under <c>CommandTimeout</c>.</summary>
        public async Task<T> CallAsync<T>(
            string step,
            TimeSpan deadline,
            Func<CancellationToken, Task<T>> call
        )
        {
            CurrentStep = step;

            // Caller cancellation, or loss declared since the previous read, stops this one before anything
            // is dispatched.
            callerToken.ThrowIfCancellationRequested();
            ThrowIfLost();

            using CancellationTokenSource deadlineSource = new(deadline, timeProvider);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                callerToken,
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
