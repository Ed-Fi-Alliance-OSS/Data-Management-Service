// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationStrategies;

/// <summary>
/// Implements an authorization strategy that performs no additional authorization.
/// </summary>
[AuthorizationStrategyName(AuthorizationStrategyName)]
public class NoFurtherAuthorizationRequiredAuthorizationStrategyHandler : IAuthorizationStrategyHandler
{
    private const string AuthorizationStrategyName = "NoFurtherAuthorizationRequired";

    public AuthorizationResult IsRequestAuthorized(
        SecurityElements securityElements,
        ApiClientDetails details
    )
    {
        return new AuthorizationResult(true);
    }
}
