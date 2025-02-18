// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationValidation;

/// <summary>
/// Validates the authorization strategy that performs no additional authorization.
/// </summary>
[AuthorizationStrategyName(AuthorizationStrategyName)]
public class NoFurtherAuthorizationRequiredValidator : IAuthorizationValidator
{
    private const string AuthorizationStrategyName = "NoFurtherAuthorizationRequired";

    public AuthorizationResult ValidateAuthorization(
        DocumentSecurityElements securityElements,
        ClientAuthorizations authorizations
    )
    {
        return new AuthorizationResult(true);
    }
}
