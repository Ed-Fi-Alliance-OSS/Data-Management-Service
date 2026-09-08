// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

internal static class CdcWorkflowJournalValidation
{
    internal static void Require(
        [DoesNotReturnIf(false)] bool condition,
        CdcWorkflowStateFailure failure = CdcWorkflowStateFailure.Invalid
    )
    {
        if (!condition)
        {
            throw new CdcWorkflowStateException(failure);
        }
    }

    internal static void Validate(CdcWorkflowJournal journal, DateTimeOffset now)
    {
        Require(
            journal.Version == CdcWorkflowJournal.CurrentVersion,
            CdcWorkflowStateFailure.UnsupportedVersion
        );
        Require(journal.WorkflowId != Guid.Empty && journal.Target is not null);
        Require(
            CdcTargetValidator
                .ValidateBindingIdentity(CdcBindingIdentity.FromTargetIdentity(journal.Target!))
                .Succeeded
        );
        Require(Enum.IsDefined(journal.Target!.Provider));
        Require(
            journal.CreatedAt != default
                && journal.CreatedAt <= now
                && journal.CreatedAt.Offset == TimeSpan.Zero
        );
        Require(!journal.Operations.IsDefault);
        HashSet<Guid> ids = [];
        HashSet<CdcWorkflowEffect> singletonEffects = [];
        List<CdcWorkflowCompletion> completed = [];
        DateTimeOffset previous = journal.CreatedAt;
        DateTimeOffset prerequisiteCompletedAt = journal.CreatedAt;
        bool pendingIncrease = false;
        bool publicationIntended = false;
        List<(int Ceiling, CdcConsumerCapacityEvidence Consumer)> consumerHistory = [];
        HashSet<Guid> acknowledgementInvocations = [];
        foreach (CdcWorkflowOperation operation in journal.Operations)
        {
            Require(operation is not null);
            Require(operation!.OperationId != Guid.Empty && ids.Add(operation.OperationId));
            Require(Enum.IsDefined(operation.Effect));
            Require(
                operation.IntendedAt >= previous
                    && operation.IntendedAt >= prerequisiteCompletedAt
                    && operation.IntendedAt <= now
                    && operation.IntendedAt.Offset == TimeSpan.Zero
            );
            Require(!operation.Completions.IsDefault && operation.Completions.Length <= 1);
            Require(
                operation.Completions.All(completion =>
                    completion is not null && completion.Evidence is not null
                )
            );
            Require(!operation.RecordSizeIncrease.IsDefault);
            if (
                operation.Effect
                is CdcWorkflowEffect.CreateDatabase
                    or CdcWorkflowEffect.AssociateSource
                    or CdcWorkflowEffect.CreateProvider
                    or CdcWorkflowEffect.EstablishConnector
                    or CdcWorkflowEffect.AuthorizeWriterPublication
            )
            {
                Require(singletonEffects.Add(operation.Effect), CdcWorkflowStateFailure.Contradictory);
            }
            if (operation.Effect == CdcWorkflowEffect.CreateDatabase)
            {
                Require(completed.Count == 0 && journal.Operations[0] == operation);
            }
            else
            {
                Require(
                    completed.OfType<CdcWorkflowCompletion.Database>().Any(),
                    CdcWorkflowStateFailure.Contradictory
                );
            }
            if (
                operation.Effect
                is not (CdcWorkflowEffect.CreateDatabase or CdcWorkflowEffect.AssociateSource)
            )
            {
                Require(
                    completed.OfType<CdcWorkflowCompletion.Source>().Any(),
                    CdcWorkflowStateFailure.Contradictory
                );
                Require(
                    completed.OfType<CdcWorkflowCompletion.Database>().Single().Receipt.Outcome
                        == CdcDatabaseCreationOutcome.Created,
                    CdcWorkflowStateFailure.Contradictory
                );
            }
            if (publicationIntended)
            {
                Require(
                    operation.Effect
                        is not (
                            CdcWorkflowEffect.ReserveBinding
                            or CdcWorkflowEffect.ActivateProjection
                            or CdcWorkflowEffect.CreateProvider
                            or CdcWorkflowEffect.RegisterConnector
                        )
                );
            }
            if (operation.Effect == CdcWorkflowEffect.AuthorizeWriterPublication)
            {
                Require(
                    !pendingIncrease
                        && completed.OfType<CdcWorkflowCompletion.Provider>().Any()
                        && completed.OfType<CdcWorkflowCompletion.Connector>().Any()
                );
                publicationIntended = true;
            }
            if (operation.Effect == CdcWorkflowEffect.IncreaseRecordSize)
            {
                Require(!pendingIncrease && operation.RecordSizeIncrease.Length == 1);
                ValidateIncrease(
                    operation,
                    journal,
                    completed,
                    now,
                    consumerHistory,
                    acknowledgementInvocations
                );
                pendingIncrease = operation.Completions.IsEmpty;
            }
            else
            {
                Require(operation.RecordSizeIncrease.IsEmpty);
            }
            foreach (CdcWorkflowCompletionRecord completion in operation.Completions)
            {
                Require(completion is not null && completion.Evidence is not null);
                Require(
                    completion!.ReconciledAt >= operation.IntendedAt
                        && completion.ReconciledAt <= now
                        && completion.ReconciledAt.Offset == TimeSpan.Zero
                );
                ValidateCompletion(operation.Effect, completion.Evidence, journal, completed);
                completed.Add(completion.Evidence);
                if (completion.Evidence is not CdcWorkflowCompletion.Reconciled)
                {
                    prerequisiteCompletedAt = completion.ReconciledAt;
                }
            }
            previous = operation.IntendedAt;
        }
    }

    private static void ValidateCompletion(
        CdcWorkflowEffect effect,
        CdcWorkflowCompletion evidence,
        CdcWorkflowJournal journal,
        List<CdcWorkflowCompletion> completed
    )
    {
        switch (effect, evidence)
        {
            case (CdcWorkflowEffect.CreateDatabase, CdcWorkflowCompletion.Database database):
                Require(
                    database.Receipt is not null
                        && database.Receipt.ReceiptId != Guid.Empty
                        && Enum.IsDefined(database.Receipt.Outcome)
                );
                break;
            case (CdcWorkflowEffect.AssociateSource, CdcWorkflowCompletion.Source source):
                Require(CdcSha256ValueValidator.IsValid(source.PhysicalSourceFingerprint));
                break;
            case (CdcWorkflowEffect.CreateProvider, CdcWorkflowCompletion.Provider provider):
                Require(!provider.Artifacts.IsDefaultOrEmpty && !provider.InitialSlotProofs.IsDefault);
                Require(provider.Artifacts.All(a => a is not null));
                // Provider names depend only on deployment/instance/generation, not the public topic prefix.
                CdcArtifactNameResult names = CdcArtifactNameGenerator.Render(
                    new(
                        journal.Target.DeploymentKey,
                        "journal",
                        journal.Target.InstanceKey,
                        journal.Target.Generation,
                        journal.Target.Provider
                    )
                );
                Require(names.Succeeded);
                CdcGovernedArtifactName[] identities = names
                    .Inventory!.GovernedArtifacts.Where(artifact =>
                        artifact.Kind
                            is CdcGovernedArtifactKind.PostgresqlLogicalSlot
                                or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument
                                or CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache
                                or CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat
                    )
                    .ToArray();
                Require(provider.Artifacts.Length == identities.Length);
                Require(
                    Array.TrueForAll(
                        identities,
                        expected =>
                            provider.Artifacts.Any(actual =>
                                actual.Kind == expected.Kind && actual.Name == expected.Name
                            )
                    )
                );
                Require(
                    provider.Artifacts.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count()
                        == provider.Artifacts.Length
                );
                foreach (CdcRetainedProviderIdentity artifact in provider.Artifacts)
                {
                    Require(
                        artifact is not null
                            && Enum.IsDefined(artifact.Kind)
                            && CdcSha256ValueValidator.IsValid(artifact.IdentityHash)
                    );
                    SafeToken(artifact!.Name);
                }
                Require(
                    journal.Target.Provider == CdcProvider.Postgresql
                        ? provider.InitialSlotProofs.Length == 1
                        : provider.InitialSlotProofs.IsEmpty
                );
                foreach (var proof in provider.InitialSlotProofs)
                {
                    Require(proof is not null && proof.SourceFingerprint is not null);
                    Require(
                        proof!.SourceFingerprint.Version
                            == EdFi.DataManagementService.Backend.Ddl.CdcSourceFingerprintMetadata.Version
                    );
                    Require(
                        CdcPostgresqlProviderPosition.ParseWalLsn(proof.RetainedRestartLsn).Succeeded
                            && CdcPostgresqlProviderPosition
                                .ParseWalLsn(proof.RetainedConfirmedFlushLsn)
                                .Succeeded
                    );
                    Require(
                        proof.SourceFingerprint.Value
                            == completed
                                .OfType<CdcWorkflowCompletion.Source>()
                                .Single()
                                .PhysicalSourceFingerprint
                    );
                    Require(
                        provider.Artifacts.Any(a =>
                            a.Kind == CdcGovernedArtifactKind.PostgresqlLogicalSlot
                            && a.Name == proof.ReplicationSlotName.Value
                        )
                    );
                }
                break;
            case (CdcWorkflowEffect.EstablishConnector, CdcWorkflowCompletion.Connector connector):
                Require(completed.OfType<CdcWorkflowCompletion.Provider>().Any());
                Require(CdcSha256ValueValidator.IsValid(connector.SourcePartitionHash));
                break;
            case (
                not (
                    CdcWorkflowEffect.CreateDatabase
                    or CdcWorkflowEffect.AssociateSource
                    or CdcWorkflowEffect.CreateProvider
                    or CdcWorkflowEffect.EstablishConnector
                ),
                CdcWorkflowCompletion.Reconciled
            ):
                break;
            default:
                Require(false, CdcWorkflowStateFailure.Contradictory);
                break;
        }
    }

    private static void ValidateIncrease(
        CdcWorkflowOperation operation,
        CdcWorkflowJournal journal,
        List<CdcWorkflowCompletion> completed,
        DateTimeOffset now,
        List<(int Ceiling, CdcConsumerCapacityEvidence Consumer)> consumerHistory,
        HashSet<Guid> invocations
    )
    {
        CdcRecordSizeIncreaseJournal increase = operation.RecordSizeIncrease[0];
        Require(increase is not null && increase.BindingIdentity is not null);
        Require(increase!.BindingIdentity.ToTargetIdentity() == journal.Target);
        Require(
            increase.BindingIdentity.PhysicalSourceFingerprint
                == completed.OfType<CdcWorkflowCompletion.Source>().Single().PhysicalSourceFingerprint
        );
        Require(
            CdcArtifactNameGenerator.RecoverFromCompleteBindingIdentity(increase.BindingIdentity).Succeeded
        );
        SafeToken(increase.BindingIdentity.ConnectorName);
        SafeToken(increase.BindingIdentity.TopicName);
        Require(
            increase.PreviousMaxRecordBytes > 0
                && increase.RequestedMaxRecordBytes > increase.PreviousMaxRecordBytes
        );
        Require(!increase.Acknowledgements.IsDefaultOrEmpty);
        Require(
            increase.Acknowledgements[0] is not null
                && increase.Acknowledgements[0].ConfirmedAt <= operation.IntendedAt
        );
        DateTimeOffset previous = journal.CreatedAt;
        foreach (CdcRecordSizeAcknowledgement acknowledgement in increase.Acknowledgements)
        {
            ValidateAcknowledgement(acknowledgement, previous, now);
            Require(invocations.Add(acknowledgement.InvocationId));
            foreach (CdcConsumerCapacityEvidence consumer in acknowledgement.Consumers)
            {
                var deploymentHistory = consumerHistory
                    .Where(entry => entry.Consumer.DeploymentIdentity == consumer.DeploymentIdentity)
                    .ToList();
                if (deploymentHistory.Count == 0)
                {
                    // Renaming/replacing a deployment cannot reuse a previous deployment's evidence.
                    Require(
                        !consumerHistory.Exists(entry =>
                            entry.Consumer.EvidenceReference == consumer.EvidenceReference
                        )
                    );
                }
                else
                {
                    var latest = deploymentHistory[^1].Consumer;
                    if (
                        latest.Revision != consumer.Revision
                        || latest.ConfirmingOwner != consumer.ConfirmingOwner
                    )
                    {
                        Require(
                            !deploymentHistory.Exists(entry =>
                                entry.Consumer.EvidenceReference == consumer.EvidenceReference
                            )
                        );
                    }
                    Require(
                        !deploymentHistory.Exists(entry =>
                            entry.Consumer.EvidenceReference == consumer.EvidenceReference
                            && (
                                entry.Consumer.Revision != consumer.Revision
                                || entry.Consumer.ConfirmingOwner != consumer.ConfirmingOwner
                                || entry.Ceiling < increase.RequestedMaxRecordBytes
                            )
                        )
                    );
                }
            }
            consumerHistory.AddRange(
                acknowledgement.Consumers.Select(consumer => (increase.RequestedMaxRecordBytes, consumer))
            );
            if (!operation.Completions.IsEmpty)
            {
                Require(acknowledgement.ConfirmedAt <= operation.Completions[0].ReconciledAt);
            }
            previous = acknowledgement.ConfirmedAt;
        }
    }

    internal static void ValidateAcknowledgement(
        CdcRecordSizeAcknowledgement acknowledgement,
        DateTimeOffset earliest,
        DateTimeOffset now
    )
    {
        Require(acknowledgement is not null);
        Require(acknowledgement!.InvocationId != Guid.Empty);
        SafeToken(acknowledgement.OperatorIdentity);
        Require(
            acknowledgement.ConfirmedAt >= earliest
                && acknowledgement.ConfirmedAt <= now
                && acknowledgement.ConfirmedAt.Offset == TimeSpan.Zero
        );
        Require(
            !acknowledgement.Consumers.IsDefault
                && acknowledgement.NoConsumers == acknowledgement.Consumers.IsEmpty
        );
        foreach (var consumer in acknowledgement.Consumers)
        {
            Require(consumer is not null);
            SafeToken(consumer!.DeploymentIdentity);
            SafeToken(consumer.Revision);
            SafeToken(consumer.ConfirmingOwner);
            SafeToken(consumer.EvidenceReference);
        }
        Require(
            acknowledgement
                .Consumers.Select(consumer => consumer.DeploymentIdentity)
                .Distinct(StringComparer.Ordinal)
                .Count() == acknowledgement.Consumers.Length
        );
    }

    private static void SafeToken(string value)
    {
        CdcDiagnosticCollector diagnostics = new();
        CdcKafkaSafeTokenValidator.Validate(value, "$", "workflow token", diagnostics);
        Require(!diagnostics.HasDiagnostics);
    }
}
