// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using static EdFi.DataManagementService.Api.AspNetCoreFrontend;

namespace EdFi.DataManagementService.Api.Modules;

public class CoreModule : IModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        PathSegmentToRefine = "/data";

        endpoints.MapPost("/data/{**catchAll}", Upsert).RequireAuthorization();
        endpoints.MapGet("/data/{**catchAll}", GetById).RequireAuthorization();
        endpoints.MapPut("/data/{**catchAll}", UpdateById).RequireAuthorization();
        endpoints.MapDelete("/data/{**catchAll}", DeleteById).RequireAuthorization();
    }
}
