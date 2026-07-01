// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>One securable-subject predicate within a ReadChanges relationship strategy.</summary>
public sealed record ReadChangesAuthorizationSubject(
    DbColumnName TrackedOldColumn, // c.OldX column the predicate filters
    DbTableName AuthView, // auth view to probe
    DbColumnName AuthViewSubjectColumn, // selected output column (Target EdOrg id, or *_DocumentId)
    DbColumnName AuthViewClaimColumn // WHERE column for claim EdOrg ids (SourceEducationOrganizationId)
);

/// <summary>One supported ReadChanges relationship strategy's AND-composed subjects.</summary>
public sealed record ReadChangesRelationshipCheckSpec(
    ConfiguredAuthorizationStrategy ConfiguredStrategy,
    IReadOnlyList<ReadChangesAuthorizationSubject> Subjects
);

/// <summary>The NamespaceBased check for a tracked-change resource/descriptor.</summary>
public sealed record ReadChangesNamespaceCheckSpec(DbColumnName TrackedOldNamespaceColumn);

public sealed record ReadChangesAuthorizationPlan(
    IReadOnlyList<ReadChangesRelationshipCheckSpec> RelationshipChecks,
    ReadChangesNamespaceCheckSpec? NamespaceCheck,
    AuthorizationClaimEducationOrganizationIdParameterization? ClaimParameterization,
    NamespacePrefixParameterization? NamespaceParameterization
);

public abstract record ReadChangesAuthorizationPlanOutcome
{
    private ReadChangesAuthorizationPlanOutcome() { }

    /// <summary>Proceed: emit the relationship/namespace predicates (either list may be empty → no-op).</summary>
    public sealed record Plan(ReadChangesAuthorizationPlan AuthorizationPlan)
        : ReadChangesAuthorizationPlanOutcome;

    /// <summary>500 — unsupported strategy names (no ReadChanges implementation) or a concrete security-configuration error.</summary>
    public sealed record SecurityConfiguration(
        IReadOnlyList<string> UnavailableStrategyNames,
        IReadOnlyList<string> Errors
    ) : ReadChangesAuthorizationPlanOutcome
    {
        public SecurityConfiguration(IReadOnlyList<string> UnavailableStrategyNames)
            : this(UnavailableStrategyNames, []) { }
    }

    /// <summary>403 — NamespaceBased configured and the client has no namespace prefixes.</summary>
    public sealed record NamespaceNoPrefixesConfigured(string StrategyName)
        : ReadChangesAuthorizationPlanOutcome;
}
