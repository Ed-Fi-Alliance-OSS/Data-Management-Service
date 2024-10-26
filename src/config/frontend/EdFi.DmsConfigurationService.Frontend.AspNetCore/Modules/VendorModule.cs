// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend;
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
        VendorInsertCommandValidator validator,
        VendorInsertCommand entity,
        HttpContext httpContext,
        IVendorRepository repository
    )
    {
        validator.GuardAsync(entity);
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

    private static async Task<IResult> GetAll(IVendorRepository repository)
    {
        VendorQueryResult getResult = await repository.QueryVendor(
            new PagingQuery() { Limit = 25, Offset = 0 }
        );
        return getResult switch
        {
            VendorQueryResult.Success success => Results.Ok(success.VendorResponses),
            VendorQueryResult.FailureUnknown failure => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500),
        };
    }

    private static async Task<IResult> GetById(
        HttpContext httpContext,
        IVendorRepository repository,
        ILogger<VendorModule> logger
    )
    {
        Match match = UtilityService.PathExpressionRegex().Match(httpContext.Request.Path);
        if (!match.Success)
        {
            logger.LogInformation("Request path did not match regex");
            return Results.Problem(statusCode: 500);
        }

        string idString = match.Groups["Id"].Value;
        if (!long.TryParse(idString, out long id))
        {
            return Results.NotFound();
        }

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
        VendorUpdateCommandValidator validator,
        VendorUpdateCommand entity,
        HttpContext httpContext,
        IVendorRepository repository
    )
    {
        validator.GuardAsync(entity);
        Match match = UtilityService.PathExpressionRegex().Match(httpContext.Request.Path);

        string idString = match.Groups["Id"].Value;
        if (long.TryParse(idString, out long id))
        {
            var entityType = entity.GetType();
            var idProperty = entityType.GetProperty("Id");
            if (idProperty == null)
            {
                throw new InvalidOperationException("The entity does not contain an Id property.");
            }

            var entityId = idProperty.GetValue(entity) as long?;

            if (entityId != id)
            {
                throw new ValidationException(
                    new[] { new ValidationFailure("Id", "Request body id must match the id in the url.") }
                );
            }

            var vendorUpdateResult = await repository.UpdateVendor(entity);

            return vendorUpdateResult switch
            {
                VendorUpdateResult.Success success => Results.NoContent(),
                VendorUpdateResult.FailureNotExists => Results.NotFound(),
                VendorUpdateResult.FailureUnknown => Results.Problem(statusCode: 500),
                _ => Results.Problem(statusCode: 500),
            };
        }

        return Results.NotFound();
    }

    private static async Task<IResult> GetApplicationsByVendorId(
        long id,
        [FromServices] IApplicationRepository repository
    )
    {
        var getResult = await repository.GetApplicationsByVendorId(id);

        return getResult switch
        {
            ApplicationsByVendorResult.Success success => Results.Ok(success.ApplicationResponses),
            ApplicationsByVendorResult.FailureVendorNotFound => Results.NotFound(
                new { title = $"Not found: vendor with ID {id}. It may have been recently deleted." }
            ),
            _ => Results.Problem(statusCode: 500),
        };
    }

    private static async Task<IResult> Delete(HttpContext httpContext, IVendorRepository repository)
    {
        Match match = UtilityService.PathExpressionRegex().Match(httpContext.Request.Path);
        if (!match.Success)
        {
            return Results.Problem(statusCode: 500);
        }

        string idString = match.Groups["Id"].Value;

        if (!long.TryParse(idString, out long id))
        {
            return Results.NotFound();
        }

        VendorDeleteResult deleteResult = await repository.DeleteVendor(id);
        return deleteResult switch
        {
            VendorDeleteResult.Success => Results.NoContent(),
            VendorDeleteResult.FailureNotExists => Results.NotFound(),
            VendorDeleteResult.FailureUnknown => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500),
        };
    }
}
