// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;

namespace EdFi.DataManagementService.Core.Security.Model;

/// <summary>
/// The claims used for resource authorization
/// </summary>
public class ResourceClaim
{
    public int Id { get; set; }

    [JsonIgnore]
    public int ParentId { get; set; }
    public string? ParentName { get; set; }

    /// <summary>
    /// Resource claim name
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Actions that can be performed on the resource
    /// </summary>
    public List<ResourceClaimAction>? Actions { get; set; }

    [JsonIgnore]
    public bool IsParent { get; set; }

    /// <summary>
    /// Pre-defined authorization strategy for the resource
    /// </summary>
    [JsonPropertyName("_defaultAuthorizationStrategiesForCrud")]
    public List<ResourceClaimActionAuthStrategies> DefaultAuthorizationStrategiesForCrud { get; set; }

    /// <summary>
    /// Authorization strategy overrides for the resource
    /// </summary>
    public List<ResourceClaimActionAuthStrategies> AuthorizationStrategyOverridesForCrud { get; set; }

    /// <summary>
    /// Represents the child resource claims associated with the resource
    /// </summary>
    public List<ResourceClaim> Children { get; set; }

    public ResourceClaim()
    {
        Children = [];
        DefaultAuthorizationStrategiesForCrud = [];
        AuthorizationStrategyOverridesForCrud = [];
    }
}

/// <summary>
/// Action that can be performed on the resource
/// </summary>
public record ResourceClaimAction(string Name, bool Enabled);

/// <summary>
/// Resource claim-authorization strategy
/// combines a resource claim with additional logic, an authorization strategy, to validate the claim
/// </summary>
public record ResourceClaimActionAuthStrategies(
    int ActionId,
    string ActionName,
    IEnumerable<AuthorizationStrategy> AuthorizationStrategies
);
