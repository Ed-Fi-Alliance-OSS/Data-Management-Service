// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class SecuredModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/secure", GetDetails).RequireAuthorizationWithPolicy();
    }

    public IResult GetDetails(HttpContext httpContext)
    {
        var currentClient = httpContext.User;
        return Results.Ok($"Client name: {currentClient.Claims.First(x => x.Type.Equals("client_id")).Value}");
    }
}
