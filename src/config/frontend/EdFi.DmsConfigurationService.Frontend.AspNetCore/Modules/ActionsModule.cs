// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.Action;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class ActionsModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/actions", GetUserActions).RequireAuthorizationWithPolicy();
    }

    public IResult GetUserActions(HttpContext httpContext)
    {
        var response = new AdminAction[]
        {
            new AdminAction
            {
                Id = 1,
                Name = "Create",
                Uri = "uri://ed-fi.org/api/actions/create",
            },
            new AdminAction
            {
                Id = 2,
                Name = "Read",
                Uri = "uri://ed-fi.org/api/actions/read",
            },
            new AdminAction
            {
                Id = 3,
                Name = "Update",
                Uri = "uri://ed-fi.org/api/actions/update",
            },
            new AdminAction
            {
                Id = 4,
                Name = "Delete",
                Uri = "uri://ed-fi.org/api/actions/delete",
            },
        };

        return Results.Ok(response);
    }
}
