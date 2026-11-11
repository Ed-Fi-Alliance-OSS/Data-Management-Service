// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// Reconciliation of a create whose target name is already taken, under the lock and by reads only:
/// nothing is created, restored, renamed, marked, or dropped on the database holding the name. It is entered
/// from the preamble, from the last inspection before the rename, and from a rename
/// refused because the name is taken; in the last two the staging database is this operation's
/// and is dropped by cleanup after the lock is released. A matching identity is necessary but proves nothing
/// about content: an owned target is <c>Reconciled</c> only after the full validation set passes
/// against the manifest of the trusted artifact, which was verified, staged and compared with the request's
/// pin before the lock.
/// </summary>
public sealed partial class DmsDataStoreProvisioningService
{
    public const string ReconcileInspectStep = "Reconcile.Inspect";
    public const string ReconcileOpenSessionStep = "Reconcile.OpenSession";
    public const string ReconcileIdentityStep = "Reconcile.Identity";
    public const string ReconcileReadTargetFactsStep = "Reconcile.ReadTargetFacts";
    public const string ReconcileValidateStep = "Reconcile.Validate";

    /// <summary>
    /// For the database <paramref name="state"/> describes, as the inspection named <paramref name="inspectStep"/>
    /// found it. Step 1: a protected or fenced database keeps the preamble's verdicts, and anything that is not
    /// an online database is <c>TargetUnverifiable</c>. Step 2: on a session bound to the inspected instance, an
    /// unreadable identity is <c>TargetUnverifiable</c> and another identity <c>TargetUnowned</c>, with no
    /// further read; a connection, an identity read, or a facts read the server definitely refuses for lack of a
    /// privilege leaves the target unverifiable too, reported with the refusal's sanitized diagnostic (type chain
    /// and provider code) under the step that was refused, so it
    /// requires inspection instead of looking transient. Step 3: the expected identity is followed by every validation check under the reconcile rule,
    /// whose first failure is the result. Step 4: <c>Reconciled</c> with the pin.
    /// </summary>
    private async Task<DmsDataStoreCreateResult> ReconcileAsync(
        LockScope scope,
        DmsDataStoreCreateRequest request,
        TrustedTemplateArtifact artifact,
        DmsLiveTargetState state,
        string inspectStep
    )
    {
        DmsLiveTargetState.Present present = state switch
        {
            DmsLiveTargetState.Present { State: DmsDatabaseState.Online } online => online,
            DmsLiveTargetState.Protected => throw StepFailed(
                DmsProvisioningFailureCode.ProtectedTarget,
                inspectStep
            ),
            DmsLiveTargetState.Fenced => throw StepFailed(
                DmsProvisioningFailureCode.TargetFenced,
                inspectStep
            ),
            _ => throw StepFailed(DmsProvisioningFailureCode.TargetUnverifiable, inspectStep),
        };

        DmsSession? session = await OpenOwnedSessionAsync(
            scope,
            ReconcileOpenSessionStep,
            request.TargetName,
            present.Instance,
            DmsProvisioningFailureCode.TargetUnverifiable
        );
        if (session is null)
        {
            // The name no longer resolves to the inspected instance: nothing can be said about it.
            throw StepFailed(DmsProvisioningFailureCode.TargetUnverifiable, ReconcileOpenSessionStep);
        }

        await using (session)
        {
            DmsIdentityRead identity = await scope.ReadAsync(
                ReconcileIdentityStep,
                token => engine.ReadIdentityAsync(session, token)
            );
            ThrowIfAccessDenied(
                identity,
                DmsProvisioningFailureCode.TargetUnverifiable,
                ReconcileIdentityStep
            );
            if (identity is not DmsIdentityRead.Found found)
            {
                throw StepFailed(DmsProvisioningFailureCode.TargetUnverifiable, ReconcileIdentityStep);
            }

            if (found.SourceIdentity != request.ExpectedSourceIdentity)
            {
                throw StepFailed(DmsProvisioningFailureCode.TargetUnowned, ReconcileIdentityStep);
            }

            DmsTargetFacts facts = await ReadFactsAsync(
                scope,
                ReconcileReadTargetFactsStep,
                session,
                DmsProvisioningFailureCode.TargetUnverifiable
            );
            DmsTargetValidation verdict = DmsTargetValidator.Validate(
                facts,
                identity,
                artifact.Manifest,
                options.Value.Compatibility?.Projects ?? [],
                new DmsTargetValidationRule.Reconcile(request.ExpectedSourceIdentity)
            );
            if (verdict is DmsTargetValidation.Invalid invalid)
            {
                throw StepFailed(invalid.Code, ReconcileValidateStep + "." + invalid.Check);
            }
        }

        return new DmsDataStoreCreateResult.Reconciled(artifact.Pin, DmsCleanupReport.Clean);
    }
}
