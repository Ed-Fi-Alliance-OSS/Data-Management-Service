// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Frontend.AspNetCore.AspNetCoreFrontend;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class CoreEndpointModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/data/{**dmsPath}", Upsert).RequireAuthorization(SecurityConstants.ServicePolicy);
        endpoints.MapGet("/data/{**dmsPath}", Get).RequireAuthorization(SecurityConstants.ServicePolicy);
        endpoints
            .MapPut("/data/{**dmsPath}", UpdateById)
            .RequireAuthorization(SecurityConstants.ServicePolicy);
        endpoints
            .MapDelete("/data/{**dmsPath}", DeleteById)
            .RequireAuthorization(SecurityConstants.ServicePolicy);
    }
}
