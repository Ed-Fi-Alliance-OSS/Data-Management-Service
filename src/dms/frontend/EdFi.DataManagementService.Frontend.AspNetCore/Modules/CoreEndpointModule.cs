// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using Microsoft.Extensions.Options;
using static EdFi.DataManagementService.Frontend.AspNetCore.AspNetCoreFrontend;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class CoreEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.ServiceProvider.GetRequiredService<IOptions<IdentitySettings>>().Value;
        var enforceAuthorization = settings != null && settings.EnforceAuthorization;

        endpoints.MapPost("/data/{**dmsPath}", Upsert).RequireAuthorizationWithPolicy(enforceAuthorization);
        endpoints.MapGet("/data/{**dmsPath}", Get).RequireAuthorizationWithPolicy(enforceAuthorization);
        endpoints.MapPut("/data/{**dmsPath}", UpdateById).RequireAuthorizationWithPolicy(enforceAuthorization);
        endpoints.MapDelete("/data/{**dmsPath}", DeleteById).RequireAuthorizationWithPolicy(enforceAuthorization);
    }
}
