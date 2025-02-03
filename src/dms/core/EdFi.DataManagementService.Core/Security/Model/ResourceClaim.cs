// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;

namespace EdFi.DataManagementService.Core.Security.Model;

public class ResourceClaim
{
    public int Id { get; set; }

    [JsonIgnore]
    public int ParentId { get; set; }
    public string? ParentName { get; set; }
    public string? Name { get; set; }
    public List<ResourceClaimAction>? Actions { get; set; }

    [JsonIgnore]
    public bool IsParent { get; set; }

    [JsonPropertyName("_defaultAuthorizationStrategiesForCrud")]
    public List<ResourceClaimActionAuthStrategies> DefaultAuthorizationStrategies { get; set; }
    public List<ResourceClaimActionAuthStrategies> AuthorizationStrategyOverrides { get; set; }
    public List<ResourceClaim> Children { get; set; }

    public ResourceClaim()
    {
        Children = [];
        DefaultAuthorizationStrategies = [];
        AuthorizationStrategyOverrides = [];
    }
}

public record ResourceClaimAction(string Name, bool Enabled);

public record ResourceClaimActionAuthStrategies(
    int ActionId,
    string ActionName,
    IEnumerable<AuthorizationStrategy> AuthorizationStrategies
);
