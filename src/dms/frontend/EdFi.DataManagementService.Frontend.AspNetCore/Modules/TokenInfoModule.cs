// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using Microsoft.Extensions.Options;
using static EdFi.DataManagementService.Frontend.AspNetCore.AspNetCoreFrontend;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

/// <summary>
/// Endpoint module for OAuth token introspection
/// </summary>
public class TokenInfoModule(IOptions<AppSettings> appSettings) : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        string routePattern = FixedRoutePattern.Build(
            appSettings.Value.GetRouteQualifierSegmentsArray(),
            appSettings.Value.MultiTenancy
        );

        endpoints.MapPost($"{routePattern}/oauth/token_info", GetTokenInfo);
    }
}
