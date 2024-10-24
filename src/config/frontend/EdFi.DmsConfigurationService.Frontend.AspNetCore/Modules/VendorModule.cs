// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Model.Validator;
using Microsoft.AspNetCore.Mvc;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class VendorModule : BaseModule<Vendor, VendorValidator>
{
    protected override string GetBaseRoute() => "/v2/vendors";

    public override void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        base.MapEndpoints(endpoints);

        endpoints
            .MapGet($"{GetBaseRoute()}/{{id}}/applications", GetApplicationsByVendorId)
            .RequireAuthorizationWithPolicy();
    }

    private static async Task<IResult> GetApplicationsByVendorId(
        long id,
        [FromServices] IVendorRepository repository
    )
    {
        var getResult = await repository.GetVendorByIdWithApplicationsAsync(id);

        return getResult switch
        {
            GetResult<Vendor>.GetByIdSuccess success => Results.Ok(success.Result.Applications),
            GetResult<Vendor>.GetByIdFailureNotExists => Results.NotFound(
                new { title = $"Not found: vendor with ID {id}. It may have been recently deleted." }
            ),
            _ => Results.Problem(statusCode: 500),
        };
    }
}
