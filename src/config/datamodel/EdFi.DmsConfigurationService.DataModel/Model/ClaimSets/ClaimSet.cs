// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;
using EdFi.DmsConfigurationService.DataModel.Model.Application;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ClaimSetResourceClaimActionAuthStrategies
{
    public int? ActionId { get; set; }
    public string? ActionName { get; set; }
    public IEnumerable<AuthorizationStrategy>? AuthorizationStrategies { get; set; }
}

public class ClaimSetResourceClaim
{
    public required int Id { get; set; }
    public required string? Name { get; set; }
    public List<ClaimSetResourceClaim>? Children { get; set; }
    public List<ResourceClaimAction>? Actions { get; set; }
    public List<ClaimSetResourceClaimActionAuthStrategies>? DefaultAuthorizationsStrategiesForCRUD { get; set; }
    public List<ClaimSetResourceClaimActionAuthStrategies>? AuthorizationStrategyOverridesForCRUD { get; set; }

}

public class ClaimSet
{
    public required int Id { get; set; }
    public required string? Name { get; set; }
    public required bool isSystemReserved { get; set; }
    public required List<SimpleApplication> applications { get; set; }
}

public class ClaimSetWithResources
{
    public required int Id { get; set; }
    public required string? Name { get; set; }
    public required bool isSystemReserved { get; set; }
    public required List<SimpleApplication>? applications { get; set; }
    public required List<ClaimSetResourceClaim>? resourceClaims { get; set; }
}
