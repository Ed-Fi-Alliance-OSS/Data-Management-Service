// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

public enum CdcWorkflowEffect
{
    CreateDatabase,
    AssociateSource,
    ReserveBinding,
    ActivateProjection,
    CreateProvider,
    PrepareKafka,
    RegisterConnector,
    EstablishConnector,
    StopConnector,
    ResumeConnector,
    AuthorizeWriterPublication,
    IncreaseRecordSize,
    Retire,
}

public enum CdcDatabaseCreationOutcome
{
    Created,
    Reused,
}

/// <summary>Historical receipt of the actual CREATE DATABASE outcome, not schema or CMS creation.</summary>
public sealed record CdcDatabaseCreationReceipt(Guid ReceiptId, CdcDatabaseCreationOutcome Outcome);

/// <summary>Retained provider identity is an opaque digest, never a raw database/source identifier.</summary>
public sealed record CdcRetainedProviderIdentity(
    CdcGovernedArtifactKind Kind,
    string Name,
    string IdentityHash
);

public sealed record CdcConsumerCapacityEvidence(
    string DeploymentIdentity,
    string Revision,
    string ConfirmingOwner,
    string EvidenceReference
);

public sealed record CdcRecordSizeAcknowledgement(
    Guid InvocationId,
    string OperatorIdentity,
    DateTimeOffset ConfirmedAt,
    bool NoConsumers,
    ImmutableArray<CdcConsumerCapacityEvidence> Consumers
);

/// <summary>
/// Scope and acknowledgement history survive partial rollout. They are never reusable readiness or
/// permission for a resumed invocation; the increase controller must obtain renewed confirmation.
/// </summary>
public sealed record CdcRecordSizeIncreaseJournal(
    CdcCompleteBindingIdentity BindingIdentity,
    int PreviousMaxRecordBytes,
    int RequestedMaxRecordBytes,
    ImmutableArray<CdcRecordSizeAcknowledgement> Acknowledgements
);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CdcWorkflowCompletion.Reconciled), "reconciled")]
[JsonDerivedType(typeof(CdcWorkflowCompletion.Database), "database")]
[JsonDerivedType(typeof(CdcWorkflowCompletion.Source), "source")]
[JsonDerivedType(typeof(CdcWorkflowCompletion.Provider), "provider")]
[JsonDerivedType(typeof(CdcWorkflowCompletion.Connector), "connector")]
public abstract record CdcWorkflowCompletion
{
    private CdcWorkflowCompletion() { }

    public sealed record Reconciled : CdcWorkflowCompletion;

    public sealed record Database(CdcDatabaseCreationReceipt Receipt) : CdcWorkflowCompletion;

    public sealed record Source(string PhysicalSourceFingerprint) : CdcWorkflowCompletion;

    public sealed record Provider(
        ImmutableArray<CdcRetainedProviderIdentity> Artifacts,
        ImmutableArray<CdcPostgresqlInitialReplicationSlotProof> InitialSlotProofs
    ) : CdcWorkflowCompletion;

    public sealed record Connector(string SourcePartitionHash) : CdcWorkflowCompletion;
}

public sealed record CdcWorkflowOperation(
    Guid OperationId,
    CdcWorkflowEffect Effect,
    DateTimeOffset IntendedAt,
    ImmutableArray<CdcRecordSizeIncreaseJournal> RecordSizeIncrease,
    ImmutableArray<CdcWorkflowCompletionRecord> Completions
);

public sealed record CdcWorkflowCompletionRecord(DateTimeOffset ReconciledAt, CdcWorkflowCompletion Evidence);

/// <summary>
/// Deployment-owned provenance only. Even completed operations require current live inspection before
/// reuse. In particular, a writer-publication intent irrevocably ends initial-enable eligibility.
/// </summary>
public sealed record CdcWorkflowJournal(
    int Version,
    Guid WorkflowId,
    CdcTargetIdentity Target,
    DateTimeOffset CreatedAt,
    ImmutableArray<CdcWorkflowOperation> Operations
)
{
    public const int CurrentVersion = 1;

    [JsonIgnore]
    public bool WriterPublicationAuthorized =>
        Operations.Any(operation => operation.Effect == CdcWorkflowEffect.AuthorizeWriterPublication);

    [JsonIgnore]
    public bool HasPendingRecordSizeIncrease =>
        Operations.Any(operation =>
            operation.Effect == CdcWorkflowEffect.IncreaseRecordSize && operation.Completions.IsEmpty
        );
}

public enum CdcWorkflowStateFailure
{
    Missing,
    Unavailable,
    Invalid,
    UnsupportedVersion,
    Contradictory,
    LockTimeout,
}

/// <summary>Never attach file paths, parser text, supplied values, or inner exceptions.</summary>
public sealed class CdcWorkflowStateException(CdcWorkflowStateFailure failure)
    : Exception($"CDC workflow state: {failure}. Reconcile trusted provenance before proceeding.")
{
    public CdcWorkflowStateFailure Failure { get; } = failure;
}
