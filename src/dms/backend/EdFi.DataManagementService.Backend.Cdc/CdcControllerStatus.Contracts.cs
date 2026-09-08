// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>Caller-owned selected runtime; status never starts or disposes projection.</summary>
public sealed class CdcControllerStatusTarget(
    CdcDeploymentRequest request,
    ICdcProjectionRuntime runtime,
    long lagThresholdMilliseconds,
    CdcDeploymentIntegrityReport integrity = CdcDeploymentIntegrityReport.NoReportedLoss
)
{
    [JsonIgnore]
    public CdcDeploymentRequest Request { get; } =
        request ?? throw new ArgumentNullException(nameof(request));

    [JsonIgnore]
    public ICdcProjectionRuntime Runtime { get; } =
        runtime ?? throw new ArgumentNullException(nameof(runtime));
    public long LagThresholdMilliseconds { get; } =
        lagThresholdMilliseconds >= 0
            ? lagThresholdMilliseconds
            : throw new ArgumentOutOfRangeException(nameof(lagThresholdMilliseconds));
    public CdcDeploymentIntegrityReport Integrity { get; } = integrity;

    public override string ToString() => nameof(CdcControllerStatusTarget);
}

public enum CdcIncidentPersistenceState
{
    NotRequired,
    Persisted,
    Failed,
}

public enum CdcConnectorContainmentState
{
    NotRequired,
    Stopped,
    Failed,
}

/// <summary>Only safe scalar evidence crosses the output boundary; raw transport payloads stay private.</summary>
public sealed record CdcControllerStatusDetails(
    DocumentCacheStatusQueuePresence QueuePresence,
    long? LagMilliseconds,
    long? LagThresholdMilliseconds,
    long? P50LagMilliseconds,
    long? P95LagMilliseconds,
    long? P99LagMilliseconds,
    CdcProviderArtifactContinuityState ProviderArtifactState,
    CdcProviderRetainedRangeState RetainedRangeState,
    CdcSqlServerSchemaHistoryState SchemaHistoryState,
    CdcIncidentFailureCategory? IncidentFailureCategory,
    CdcControllerStatusPositions Positions
);

public sealed record CdcControllerStatusPositions(
    string LsnProc,
    string CommitLsn,
    string ChangeLsn,
    long? EventSerialNo,
    string RetainedRangeStart,
    string RetainedRangeEnd,
    IReadOnlyList<CdcIncidentUnavailableFact> UnavailableFacts
);

/// <summary>Historical observational status; no field authorizes restart, writers, or an exact baseline.</summary>
public sealed record CdcControllerTargetStatus(
    DateTimeOffset ObservedAt,
    CdcTargetStatus Status,
    CdcControllerStatusDetails Details,
    bool HasPendingRecordSizeIncrease,
    bool HasSharedOffsetStoreIssue,
    CdcIncidentPersistenceState IncidentPersistence,
    CdcConnectorContainmentState Containment,
    IReadOnlyList<CdcDeploymentDiagnostic> Diagnostics
);

public sealed record CdcControllerStatusResult(
    CdcStatus Aggregate,
    IReadOnlyList<CdcControllerTargetStatus> Targets
);
