// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.Security.AuthorizationFilters;

/// <summary>
/// Provides no authorization strategy filter
/// </summary>
[AuthorizationStrategyName(AuthorizationStrategyName)]
public class NoFurtherAuthorizationRequiredFiltersProvider : IAuthorizationFiltersProvider
{
    private const string AuthorizationStrategyName = "NoFurtherAuthorizationRequired";

    public AuthorizationStrategyEvaluator GetFilters(
        ClientAuthorizations authorizations
    )
    {
        return new AuthorizationStrategyEvaluator([], FilterOperator.Or);
    }
}
