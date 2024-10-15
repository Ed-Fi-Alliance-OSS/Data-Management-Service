// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentValidation;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public abstract class BaseModule<T, TValidator> : IEndpointModule
    where T : class
    where TValidator : IValidator<T>
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The base path must be defined by the derived classes
        string baseRoute = GetBaseRoute();
        endpoints
            .MapPost(
                $"{baseRoute}/",
                async (TValidator validator, T entity, IRepository<T> repository) =>
                    await Insert(validator, entity, repository)
            )
            .RequireAuthorizationWithPolicy();
        endpoints.MapGet(baseRoute, GetAll).RequireAuthorizationWithPolicy();
        endpoints.MapGet($"{baseRoute}/{{id}}", GetById).RequireAuthorizationWithPolicy();
        endpoints
            .MapPut(
                $"{baseRoute}/{{id}}",
                async (TValidator validator, T entity, HttpContext httpContext, IRepository<T> repository) =>
                    await Update(validator, entity, httpContext, repository)
            )
            .RequireAuthorizationWithPolicy();
        endpoints.MapDelete($"{baseRoute}/{{id}}", Delete).RequireAuthorizationWithPolicy();
    }

    protected abstract string GetBaseRoute();

    private static async Task<IResult> Insert(TValidator validator, T entity, IRepository<T> repository)
    {
        await validator.GuardAsync(entity);
        InsertResult insertResult = await repository.AddAsync(entity);
        return insertResult switch
        {
            InsertResult.InsertSuccess => Results.Created(),
            InsertResult.UnknownFailure => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500)
        };
    }

    private static async Task<IResult> GetAll(IRepository<T> repository)
    {
        GetResult<T> getResult = await repository.GetAllAsync();
        return getResult switch
        {
            GetResult<T>.GetSuccess success => Results.Ok(success.Results),
            GetResult<T>.UnknownFailure => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500)
        };
    }

    private static async Task<IResult> GetById(HttpContext httpContext, IRepository<T> repository)
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

        GetResult<T> getResult = await repository.GetByIdAsync(id);
        return getResult switch
        {
            GetResult<T>.GetByIdSuccess success => Results.Ok(success.Result),
            GetResult<T>.GetByIdFailureNotExists => Results.NotFound(),
            GetResult<T>.UnknownFailure => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500)
        };
    }

    private static async Task<IResult> Update(
        TValidator validator,
        T entity,
        HttpContext httpContext,
        IRepository<T> repository
    )
    {
        await validator.GuardAsync(entity);

        Match match = UtilityService.PathExpressionRegex().Match(httpContext.Request.Path);
        if (!match.Success)
        {
            return Results.Problem(statusCode: 500);
        }

        string idString = match.Groups["Id"].Value;
        if (!long.TryParse(idString, out long _))
        {
            return Results.NotFound();
        }

        UpdateResult updateResult = await repository.UpdateAsync(entity);
        return updateResult switch
        {
            UpdateResult.UpdateSuccess => Results.NoContent(),
            UpdateResult.UpdateFailureNotExists => Results.NotFound(),
            UpdateResult.UnknownFailure => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500)
        };
    }

    private static async Task<IResult> Delete(HttpContext httpContext, IRepository<T> repository)
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

        DeleteResult deleteResult = await repository.DeleteAsync(id);
        return deleteResult switch
        {
            DeleteResult.DeleteSuccess => Results.NoContent(),
            DeleteResult.DeleteFailureNotExists => Results.NotFound(),
            DeleteResult.UnknownFailure => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500)
        };
    }
}
