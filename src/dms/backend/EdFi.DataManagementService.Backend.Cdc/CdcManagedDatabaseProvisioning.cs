// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>Trusted managed host callbacks; creation returns the actual provider CREATE outcome.</summary>
public interface ICdcManagedDatabaseProvisioner
{
    bool CreateDatabase();
    void ProvisionSchema(bool databaseWasCreated);
    void ValidateSchema();
    Task<string> ReadSourceFingerprintAsync(CancellationToken cancellationToken);
}

public sealed record CdcManagedProvisioningResult(
    Guid WorkflowId,
    CdcTargetIdentity Target,
    CdcDatabaseCreationReceipt CreationReceipt,
    string PhysicalSourceFingerprint
);

/// <summary>
/// Holds the controller lock across physical creation, immediate durable receipt, and schema/source
/// association. Missing creation evidence is never reconstructed from a surviving database.
/// </summary>
public sealed class CdcManagedDatabaseProvisioning(LocalCdcWorkflowJournalStore store)
{
    public async Task<CdcManagedProvisioningResult> ProvisionAsync(
        CdcTargetIdentity target,
        ICdcManagedDatabaseProvisioner provisioner,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await store.AcquireAsync(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(100),
            cancellationToken
        );
        CdcWorkflowJournal journal;
        try
        {
            journal = await session.ReadAsync(target, cancellationToken);
        }
        catch (CdcWorkflowStateException exception)
            when (exception.Failure == CdcWorkflowStateFailure.Missing)
        {
            journal = await session.CreateAsync(Guid.NewGuid(), target, cancellationToken);
        }

        // An already associated source may be inspected again, but provisioning cannot repair or
        // mutate a workflow that has moved beyond the original schema phase.
        if (
            journal.Operations.Any(operation =>
                operation.Effect
                    is not (CdcWorkflowEffect.CreateDatabase or CdcWorkflowEffect.AssociateSource)
            )
        )
        {
            throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Contradictory);
        }
        if (!journal.Operations.IsEmpty)
        {
            var receipt = journal
                .Operations.SelectMany(o => o.Completions)
                .Select(c => c.Evidence)
                .OfType<CdcWorkflowCompletion.Database>()
                .SingleOrDefault();
            var source = journal
                .Operations.SelectMany(o => o.Completions)
                .Select(c => c.Evidence)
                .OfType<CdcWorkflowCompletion.Source>()
                .SingleOrDefault();
            // Before association there is no durable physical identity with which to verify a retry.
            // Even a successful CREATE receipt must not authorize a different connection's database.
            if (receipt is null || source is null)
            {
                throw new CdcManagedProvisioningRecoveryException();
            }
            string current = await provisioner.ReadSourceFingerprintAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (current != source.PhysicalSourceFingerprint)
            {
                throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Contradictory);
            }
            // Keep ordinary create-only schema compatibility checks on repeated provisioning.
            provisioner.ValidateSchema();
            cancellationToken.ThrowIfCancellationRequested();
            return new(journal.WorkflowId, target, receipt.Receipt, current);
        }

        Guid creationOperation = Guid.NewGuid();
        await session.RecordIntentAsync(
            target,
            journal.WorkflowId,
            creationOperation,
            CdcWorkflowEffect.CreateDatabase,
            [],
            cancellationToken
        );
        cancellationToken.ThrowIfCancellationRequested();
        bool created = provisioner.CreateDatabase();
        CdcDatabaseCreationReceipt creationReceipt = new(
            Guid.NewGuid(),
            created ? CdcDatabaseCreationOutcome.Created : CdcDatabaseCreationOutcome.Reused
        );
        // Once CREATE returned, finish persisting its outcome even if cancellation just arrived.
        // A crash/write failure leaves the intent unfinished and therefore rejecting on retry.
        await session.ReconcileCompletionAsync(
            target,
            journal.WorkflowId,
            creationOperation,
            (_, _) =>
                Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                    new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                        new CdcWorkflowCompletion.Database(creationReceipt)
                    )
                ),
            CancellationToken.None
        );
        cancellationToken.ThrowIfCancellationRequested();
        provisioner.ProvisionSchema(created);
        Guid sourceOperation = Guid.NewGuid();
        await session.RecordIntentAsync(
            target,
            journal.WorkflowId,
            sourceOperation,
            CdcWorkflowEffect.AssociateSource,
            [],
            cancellationToken
        );
        journal = await session.ReconcileCompletionAsync(
            target,
            journal.WorkflowId,
            sourceOperation,
            async (_, token) =>
                new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                    new CdcWorkflowCompletion.Source(await provisioner.ReadSourceFingerprintAsync(token))
                ),
            cancellationToken
        );
        string fingerprint = (
            (CdcWorkflowCompletion.Source)journal.Operations[^1].Completions[0].Evidence
        ).PhysicalSourceFingerprint;
        return new(journal.WorkflowId, target, creationReceipt, fingerprint);
    }
}

public sealed class CdcManagedProvisioningRecoveryException()
    : Exception(
        "Managed database creation or source association was interrupted. Ownership cannot be reconstructed. "
            + "Clean up the original database and its incomplete workflow, then reprovision; do not adopt the surviving database."
    );
