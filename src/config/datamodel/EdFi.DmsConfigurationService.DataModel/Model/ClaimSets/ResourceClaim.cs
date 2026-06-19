// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ResourceClaim
{
    public string? Name { get; set; }

    [JsonPropertyName("claimName")]
    public string? ClaimName { get; set; }

    [JsonPropertyName("parentClaimName")]
    public string? ParentClaimName { get; set; }

    public List<ResourceClaimAction>? Actions { get; set; }

    [JsonPropertyName("_defaultAuthorizationStrategies")]
    public List<ClaimSetResourceClaimActionAuthStrategies> DefaultAuthorizationStrategies { get; set; } = [];

    [JsonPropertyName("authorizationStrategyOverrides")]
    public List<ClaimSetResourceClaimActionAuthStrategies> AuthorizationStrategyOverrides { get; set; } = [];

    [JsonIgnore]
    public List<ResourceClaim> Children { get; set; } = [];
}

public class ResourceClaimAction
{
    public string? Name { get; set; }
    public bool Enabled { get; set; }
}

public class ClaimSetResourceClaimActionAuthStrategies
{
    public int? ActionId { get; set; }

    public string? ActionName { get; set; }

    [JsonConverter(typeof(AuthorizationStrategyListJsonConverter))]
    public List<AuthorizationStrategy>? AuthorizationStrategies { get; set; }
}
