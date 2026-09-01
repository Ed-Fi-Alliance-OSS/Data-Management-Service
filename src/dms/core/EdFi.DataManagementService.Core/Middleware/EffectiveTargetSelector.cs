// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Whether a pipeline's requests read or write. There is deliberately no "none" member: every pipeline
/// that selects a target does read the database, token introspection included, so a "none" would be a
/// false statement about one of them. The two pipelines that genuinely open no database carry no policy
/// because they never select.
/// </summary>
internal enum DatabaseAccessIntent
{
    ReadOnly,
    ReadWrite,
}

/// <summary>
/// What a pipeline does with a request for a snapshot.
/// </summary>
internal enum SnapshotEligibility
{
    /// <summary>A parsed request for a snapshot selects one, or is rejected when none is configured.</summary>
    Allowed,

    /// <summary>A parsed request for a snapshot is rejected because the request would modify data.</summary>
    RejectedAsMutation,

    /// <summary>The header carries no meaning here and is ignored.</summary>
    NotApplicable,
}

/// <summary>
/// Whether a pipeline may be served by a read replica.
/// </summary>
internal enum ReplicaEligibility
{
    Allowed,
    NotApplicable,
}

/// <summary>
/// The routing policy of one pipeline, supplied at pipeline construction. Endpoint policy lives here
/// rather than in handlers or repositories, because DMS builds a separate pipeline per operation and
/// the HTTP method alone cannot tell GET-by-id, GET-many, tracked changes, available change versions,
/// and token introspection apart.
/// </summary>
internal sealed record DerivativeRoutingPolicy(
    DatabaseAccessIntent AccessIntent,
    SnapshotEligibility Snapshot,
    ReplicaEligibility Replica
);

/// <summary>
/// The routing verdict for one request. Success carries the assigned target; the two rejections carry
/// none, because selection never assigns a target when it rejects.
/// </summary>
internal abstract record EffectiveTargetSelectionResult
{
    private EffectiveTargetSelectionResult() { }

    public sealed record Selected(EffectiveDataStoreTarget Target) : EffectiveTargetSelectionResult;

    /// <summary>
    /// A snapshot was requested on a snapshot-eligible read and none is configured. There is no
    /// fallback: serving current data would silently discard the point-in-time guarantee the request
    /// asked for.
    /// </summary>
    public sealed record MissingSnapshot : EffectiveTargetSelectionResult;

    /// <summary>
    /// A snapshot was requested on a pipeline whose requests would modify data. A snapshot is read-only,
    /// so the request is rejected before any validation that would open a database.
    /// </summary>
    public sealed record RejectedAsMutation : EffectiveTargetSelectionResult;
}

/// <summary>
/// Chooses the one physical database a request uses. Pure: it reads the policy, the parent's configured
/// derivatives, and whether a snapshot was requested, and it returns a verdict.
/// </summary>
/// <remarks>
/// It performs presence and blank checks only. It never parses, normalizes, validates, or rewrites a
/// connection string, and it constructs no provider type: a value that is present and non-blank but
/// provider-invalid must stay selectable and fail at the connection-acquisition boundary, not here.
/// </remarks>
internal static class EffectiveTargetSelector
{
    public static EffectiveTargetSelectionResult Select(
        DerivativeRoutingPolicy policy,
        DataStore parent,
        bool snapshotRequested
    )
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(parent);

        // The snapshot axis is evaluated first, so an explicit request for a snapshot overrides a
        // configured read replica rather than competing with it.
        if (snapshotRequested)
        {
            switch (policy.Snapshot)
            {
                case SnapshotEligibility.Allowed:
                    return parent.TryGetDerivative(
                        DataStoreDerivativeType.Snapshot,
                        out string? snapshotConnectionString
                    )
                        ? Select(EffectiveTargetKind.Snapshot, snapshotConnectionString)
                        : new EffectiveTargetSelectionResult.MissingSnapshot();

                case SnapshotEligibility.RejectedAsMutation:
                    return new EffectiveTargetSelectionResult.RejectedAsMutation();

                case SnapshotEligibility.NotApplicable:
                default:
                    // The header carries no meaning on this pipeline, so evaluation continues as if it
                    // had not been sent.
                    break;
            }
        }

        // A read replica is used only for a read-only request on a replica-eligible pipeline that has
        // one configured, and only when no snapshot was selected or rejected above.
        if (
            policy.AccessIntent == DatabaseAccessIntent.ReadOnly
            && policy.Replica == ReplicaEligibility.Allowed
            && parent.TryGetDerivative(
                DataStoreDerivativeType.ReadReplica,
                out string? replicaConnectionString
            )
        )
        {
            return Select(EffectiveTargetKind.ReadReplica, replicaConnectionString);
        }

        // Every remaining path selects the parent's own database, and says so explicitly rather than
        // leaving the target unassigned for a consumer to default.
        return Select(EffectiveTargetKind.Primary, parent.ConnectionString!);
    }

    /// <summary>
    /// The configured string is carried through byte for byte. Realizing a provider-specific form is
    /// the acquisition boundary's job.
    /// </summary>
    private static EffectiveTargetSelectionResult.Selected Select(
        EffectiveTargetKind kind,
        string connectionString
    ) => new(new EffectiveDataStoreTarget(kind, connectionString));
}
