// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class ActionsModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredGet("/actions", GetUserActions);
    }

    private static IResult GetUserActions(IClaimSetRepository repository)
    {
        var actionList = repository.GetActions();

        return Results.Ok(actionList);
    }
}
