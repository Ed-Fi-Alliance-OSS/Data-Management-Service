// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.ResourceClaims;

public class ResourceClaimResponse
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public long ParentId { get; init; }
    public string? ParentName { get; init; }
    public List<ResourceClaimResponse> Children { get; init; } = [];
}

public class ResourceClaimActionResponse
{
    public long ResourceClaimId { get; init; }
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
    public long ResourceClaimId { get; init; }
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
    public long AuthStrategyId { get; init; }
    public string AuthStrategyName { get; init; } = string.Empty;
}
