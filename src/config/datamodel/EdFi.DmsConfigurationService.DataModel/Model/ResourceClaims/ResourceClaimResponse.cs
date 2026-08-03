// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.ResourceClaims;

public class ResourceClaimResponse
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int ParentId { get; init; }
    public string? ParentName { get; init; }
    public List<ResourceClaimResponse> Children { get; init; } = [];
}

public class ResourceClaimActionResponse
{
    public int ResourceClaimId { get; init; }
    public string ResourceName { get; init; } = string.Empty;
    public string ClaimName { get; init; } = string.Empty;
    public List<ActionNameResponse> Actions { get; init; } = [];
}

public class ActionNameResponse
{
    public string Name { get; init; } = string.Empty;
}

public class ResourceClaimActionAuthStrategyResponse
{
    public int ResourceClaimId { get; init; }
    public string ResourceName { get; init; } = string.Empty;
    public string ClaimName { get; init; } = string.Empty;
    public List<ActionWithAuthorizationStrategyResponse> AuthorizationStrategiesForActions { get; init; } =
    [];
}

public class ActionWithAuthorizationStrategyResponse
{
    public int ActionId { get; init; }
    public string ActionName { get; init; } = string.Empty;
    public List<AuthorizationStrategyForActionResponse> AuthorizationStrategies { get; init; } = [];
}

public class AuthorizationStrategyForActionResponse
{
    public int AuthStrategyId { get; init; }
    public string AuthStrategyName { get; init; } = string.Empty;
}
