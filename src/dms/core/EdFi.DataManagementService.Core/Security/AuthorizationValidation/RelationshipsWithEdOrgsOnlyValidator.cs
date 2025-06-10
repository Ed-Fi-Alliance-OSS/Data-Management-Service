// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationValidation;

/// <summary>
/// validates whether a client is authorized to access a resource based on relationships with education organizations.
/// </summary>
[AuthorizationStrategyName(AuthorizationStrategyName)]
public class RelationshipsWithEdOrgsOnlyValidator(IAuthorizationRepository authorizationRepository)
    : IAuthorizationValidator
{
    private const string AuthorizationStrategyName =
        AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly;

    public async Task<ResourceAuthorizationResult> ValidateAuthorization(
        DocumentSecurityElements securityElements,
        AuthorizationFilter[] authorizationFilters,
        AuthorizationSecurableInfo[] authorizationSecurableInfos,
        OperationType operationType
    )
    {
        var edOrgResult = await RelationshipsBasedAuthorizationHelper.ValidateEdOrgAuthorization(
            authorizationRepository,
            securityElements,
            authorizationFilters,
            operationType
        );
        return RelationshipsBasedAuthorizationHelper.BuildResourceAuthorizationResult(
            edOrgResult,
            authorizationFilters
        );
    }
}
