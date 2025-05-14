// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Security.AuthorizationFilters;

/// <summary>
/// Provides no authorization strategy filter
/// </summary>
[AuthorizationStrategyName(AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired)]
public class NoFurtherAuthorizationRequiredFiltersProvider : AuthorizationFiltersProviderBase
{
    public NoFurtherAuthorizationRequiredFiltersProvider()
        : base(AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired) { }
}
