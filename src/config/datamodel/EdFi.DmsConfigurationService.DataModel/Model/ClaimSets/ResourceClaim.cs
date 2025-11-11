// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ResourceClaim
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public List<ResourceClaimAction>? Actions { get; set; }

    [JsonPropertyName("_defaultAuthorizationStrategiesForCrud")]
    public List<ClaimSetResourceClaimActionAuthStrategies?> DefaultAuthorizationStrategiesForCRUD { get; set; } =
    [];
    public List<ClaimSetResourceClaimActionAuthStrategies?> AuthorizationStrategyOverridesForCRUD { get; set; } =
    [];

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
    public IEnumerable<AuthorizationStrategy>? AuthorizationStrategies { get; set; }
}
