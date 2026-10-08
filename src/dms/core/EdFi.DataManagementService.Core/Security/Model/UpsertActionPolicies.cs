// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core.Security.Model;

/// <summary>
/// What the client's claim set grants one POST action, keeping everything needed to render that action's
/// denial or security-configuration failure once the backend reports which action the target selected.
/// </summary>
internal abstract record UpsertActionPolicyEvidence(string ActionName)
{
    public sealed record Permitted(string ActionName, IReadOnlyList<string> StrategyNames)
        : UpsertActionPolicyEvidence(ActionName);

    public sealed record Denied(string ActionName, string ResourceClaimName, string ClaimSetName)
        : UpsertActionPolicyEvidence(ActionName);

    public sealed record NoStrategies(
        string ActionName,
        IReadOnlyList<string> MatchedResourceClaimUris,
        string MatchedResourceClaimName
    ) : UpsertActionPolicyEvidence(ActionName);
}

/// <summary>
/// The Create and Update evidence a POST resolved. Which one applies is decided by the target the backend
/// observes.
/// </summary>
internal sealed record UpsertActionPolicies(
    UpsertActionPolicyEvidence Create,
    UpsertActionPolicyEvidence Update
)
{
    public UpsertActionPolicyEvidence For(UpsertTargetAction action) =>
        action switch
        {
            UpsertTargetAction.Create => Create,
            UpsertTargetAction.Update => Update,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
}
