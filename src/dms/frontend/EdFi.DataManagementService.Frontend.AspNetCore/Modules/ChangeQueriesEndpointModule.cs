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
/// Endpoint module for the Change Queries availableChangeVersions endpoint. This is a fixed DMS
/// route: it is not generated from ApiSchema.json and is not gated by OpenAPI path presence.
/// </summary>
public class ChangeQueriesEndpointModule(IOptions<AppSettings> appSettings) : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        string routePattern = FixedRoutePattern.Build(
            appSettings.Value.GetRouteQualifierSegmentsArray(),
            appSettings.Value.MultiTenancy
        );

        endpoints.MapGet(
            $"{routePattern}/changeQueries/v1/availableChangeVersions",
            GetAvailableChangeVersions
        );
    }
}
