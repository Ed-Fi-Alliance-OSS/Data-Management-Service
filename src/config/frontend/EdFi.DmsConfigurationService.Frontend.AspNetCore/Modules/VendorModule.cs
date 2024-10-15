// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Model.Validator;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class VendorModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapPost(
                "/v2/vendors/",
                async (
                    VendorValidator validator,
                    Vendor vendor,
                    HttpContext HttpContext,
                    IRepository<Vendor> repository
                ) => await Insert(validator, vendor, HttpContext, repository)
            )
            .RequireAuthorizationWithPolicy();
        endpoints.MapGet("/v2/vendors", GetAll).RequireAuthorizationWithPolicy();
        endpoints.MapGet("/v2/vendors/{id}", GetById).RequireAuthorizationWithPolicy();
        endpoints
            .MapPut(
                "/v2/vendors/{id}",
                async (
                    VendorValidator validator,
                    Vendor vendor,
                    HttpContext httpContext,
                    IRepository<Vendor> repository
                ) => await Update(validator, vendor, httpContext, repository)
            )
            .RequireAuthorizationWithPolicy();
        endpoints.MapDelete("/v2/vendors/{id}", Delete).RequireAuthorizationWithPolicy();
    }

    private async Task<IResult> Insert(
        VendorValidator validator,
        Vendor vendor,
        HttpContext httpContext,
        IRepository<Vendor> repository
    )
    {
        await validator.GuardAsync(vendor);
        var insertResult = await repository.AddAsync(vendor);
        var request = httpContext.Request;
        return insertResult switch
        {
            InsertResult.InsertSuccess success => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                null
            ),
            InsertResult.UnknownFailure => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500),
        };
    }

    private async Task<IResult> GetAll(IRepository<Vendor> repository)
    {
        var getResult = await repository.GetAllAsync();
        return getResult switch
        {
            GetResult<Vendor>.GetSuccess success => Results.Ok(success.Results),
            GetResult<Vendor>.UnknownFailure => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500),
        };
    }

    private async Task<IResult> GetById(HttpContext httpContext, IRepository<Vendor> repository)
    {
        Match match = UtilityService.PathExpressionRegex().Match(httpContext.Request.Path);

        string idString = match.Groups["Id"].Value;
        if (long.TryParse(idString, out long id))
        {
            var getResult = await repository.GetByIdAsync(id);
            return getResult switch
            {
                GetResult<Vendor>.GetByIdSuccess success => Results.Ok(success.Result),
                GetResult<Vendor>.GetByIdFailureNotExists => Results.NotFound(),
                GetResult<Vendor>.UnknownFailure => Results.Problem(statusCode: 500),
                _ => Results.Problem(statusCode: 500),
            };
        }

        return Results.NotFound();
    }

    private async Task<IResult> Update(
        VendorValidator validator,
        Vendor vendor,
        HttpContext httpContext,
        IRepository<Vendor> repository
    )
    {
        await validator.GuardAsync(vendor);
        Match match = UtilityService.PathExpressionRegex().Match(httpContext.Request.Path);

        string idString = match.Groups["Id"].Value;
        if (long.TryParse(idString, out long id))
        {
            var updateResult = await repository.UpdateAsync(vendor);
            return updateResult switch
            {
                UpdateResult.UpdateSuccess success => Results.NoContent(),
                UpdateResult.UpdateFailureNotExists => Results.NotFound(),
                UpdateResult.UnknownFailure => Results.Problem(statusCode: 500),
                _ => Results.Problem(statusCode: 500),
            };
        }

        return Results.NotFound();
    }

    private async Task<IResult> Delete(HttpContext httpContext, IRepository<Vendor> repository)
    {
        Match match = UtilityService.PathExpressionRegex().Match(httpContext.Request.Path);

        string idString = match.Groups["Id"].Value;
        if (long.TryParse(idString, out long id))
        {
            var deleteResult = await repository.DeleteAsync(id);
            return deleteResult switch
            {
                DeleteResult.DeleteSuccess success => Results.NoContent(),
                DeleteResult.DeleteFailureNotExists => Results.NotFound(),
                DeleteResult.UnknownFailure => Results.Problem(statusCode: 500),
                _ => Results.Problem(statusCode: 500),
            };
        }

        return Results.NotFound();
    }
}
