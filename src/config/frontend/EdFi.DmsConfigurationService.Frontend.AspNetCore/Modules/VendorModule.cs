// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Vendor;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Model.Validator;
using FluentValidation;
using FluentValidation.Results;

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
        VendorInsertCommandValidator validator,
        VendorInsertCommand entity,
        HttpContext httpContext,
        IVendorRepository repository
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
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetAll(IVendorRepository repository, HttpContext httpContext)
    {
        VendorQueryResult getResult = await repository.QueryVendor(
            new PagingQuery() { Limit = 9999, Offset = 0 }
        );
        return getResult switch
        {
            VendorQueryResult.Success success => Results.Ok(success.VendorResponses),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        long id,
        HttpContext httpContext,
        IVendorRepository repository,
        ILogger<VendorModule> logger
    )
    {
        VendorGetResult getResult = await repository.GetVendor(id);
        return getResult switch
        {
            VendorGetResult.Success success => Results.Ok(success.VendorResponse),
            VendorGetResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"Vendor {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Update(
        long id,
        VendorUpdateCommandValidator validator,
        VendorUpdateCommand command,
        HttpContext httpContext,
        IVendorRepository repository
    )
    {
        await validator.GuardAsync(command);

        if (command.Id != id)
        {
            throw new ValidationException(
                new[] { new ValidationFailure("Id", "Request body id must match the id in the url.") }
            );
        }

        var vendorUpdateResult = await repository.UpdateVendor(command);

        return vendorUpdateResult switch
        {
            VendorUpdateResult.Success success => Results.NoContent(),
            VendorUpdateResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound(
                    $"Vendor {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Delete(
        long id,
        HttpContext httpContext,
        IVendorRepository repository,
        ILogger<VendorModule> logger
    )
    {
        VendorDeleteResult deleteResult = await repository.DeleteVendor(id);
        return deleteResult switch
        {
            VendorDeleteResult.Success => Results.NoContent(),
            VendorDeleteResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound(
                    $"Vendor {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetApplicationsByVendorId(
        long id,
        IVendorRepository repository,
        HttpContext httpContext
    )
    {
        var getResult = await repository.GetVendorApplications(id);

        return getResult switch
        {
            VendorApplicationsResult.Success success => Results.Ok(success.ApplicationResponses),
            VendorApplicationsResult.FailureNotExists => FailureResults.NotFound(
                $"Vendor {id} not found. It may have been recently deleted.",
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
