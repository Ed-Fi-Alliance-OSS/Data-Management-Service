// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Vendor;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Model.Validator;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class VendorModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v2/vendors/", InsertVendor).RequireAuthorizationWithPolicy();
        endpoints.MapGet("/v2/vendors/", GetAll).RequireAuthorizationWithPolicy();
        endpoints.MapGet($"/v2/vendors/{{id}}", GetById).RequireAuthorizationWithPolicy();
        endpoints.MapPut($"/v2/vendors/{{id}}", Update).RequireAuthorizationWithPolicy();
        endpoints.MapDelete($"/v2/vendors/{{id}}", Delete).RequireAuthorizationWithPolicy();
        endpoints
            .MapGet($"/v2/vendors/{{id}}/applications", GetApplicationsByVendorId)
            .RequireAuthorizationWithPolicy();
    }

    private static async Task<IResult> InsertVendor(
        [FromServices] VendorInsertCommandValidator validator,
        [FromBody] VendorInsertCommand entity,
        [FromServices] HttpContext httpContext,
        [FromServices] IVendorRepository repository
    )
    {
        await validator.GuardAsync(entity);
        var insertResult = await repository.InsertVendor(entity);

        var request = httpContext.Request;
        return insertResult switch
        {
            VendorInsertResult.Success success => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                null
            ),
            VendorInsertResult.FailureUnknown => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500),
        };
    }

    private static async Task<IResult> GetAll([FromServices] IVendorRepository repository)
    {
        VendorQueryResult getResult = await repository.QueryVendor(
            new PagingQuery() { Limit = 9999, Offset = 0 }
        );
        return getResult switch
        {
            VendorQueryResult.Success success => Results.Ok(success.VendorResponses),
            VendorQueryResult.FailureUnknown failure => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500),
        };
    }

    private static async Task<IResult> GetById(
        long id,
        [FromServices] HttpContext httpContext,
        [FromServices] IVendorRepository repository,
        [FromServices] ILogger<VendorModule> logger
    )
    {
        VendorGetResult getResult = await repository.GetVendor(id);
        return getResult switch
        {
            VendorGetResult.Success success => Results.Ok(success.VendorResponse),
            VendorGetResult.FailureNotFound => Results.NotFound(),
            VendorGetResult.FailureUnknown => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500),
        };
    }

    private static async Task<IResult> Update(
        long id,
        [FromServices] VendorUpdateCommandValidator validator,
        [FromBody] VendorUpdateCommand command,
        [FromServices] HttpContext httpContext,
        [FromServices] IVendorRepository repository
    )
    {
        await validator.GuardAsync(command);
        var entityType = command.GetType();
        var idProperty = entityType.GetProperty("Id");
        if (idProperty == null)
        {
            throw new InvalidOperationException("The entity does not contain an Id property.");
        }

        var entityId = idProperty.GetValue(command) as long?;

        if (entityId != id)
        {
            throw new ValidationException(
                new[] { new ValidationFailure("Id", "Request body id must match the id in the url.") }
            );
        }

        var vendorUpdateResult = await repository.UpdateVendor(command);

        return vendorUpdateResult switch
        {
            VendorUpdateResult.Success success => Results.NoContent(),
            VendorUpdateResult.FailureNotExists => Results.NotFound(),
            VendorUpdateResult.FailureUnknown => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500),
        };
    }

    private static async Task<IResult> Delete(
        long id,
        [FromServices] HttpContext httpContext,
        [FromServices] IVendorRepository repository,
        [FromServices] ILogger<VendorModule> logger
    )
    {
        VendorDeleteResult deleteResult = await repository.DeleteVendor(id);
        return deleteResult switch
        {
            VendorDeleteResult.Success => Results.NoContent(),
            VendorDeleteResult.FailureNotExists => Results.NotFound(),
            VendorDeleteResult.FailureUnknown => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500),
        };
    }

    private static async Task<IResult> GetApplicationsByVendorId(
        long id,
        [FromServices] IVendorRepository repository
    )
    {
        var getResult = await repository.GetVendorApplications(id);

        return getResult switch
        {
            VendorApplicationsResult.Success success => Results.Ok(success.ApplicationResponses),
            VendorApplicationsResult.FailureNotExists => Results.NotFound(
                new { title = $"Not found: vendor with ID {id}. It may have been recently deleted." }
            ),
            _ => Results.Problem(statusCode: 500),
        };
    }
}
