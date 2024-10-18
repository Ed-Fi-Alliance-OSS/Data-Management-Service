// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentValidation;
using FluentValidation.Results;

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
                async (TValidator validator, T entity, HttpContext httpContext, IRepository<T> repository) =>
                    await Insert(validator, entity, httpContext, repository)
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

    private static async Task<IResult> Insert(
        TValidator validator,
        T entity,
        HttpContext httpContext,
        IRepository<T> repository
    )
    {
        await validator.GuardAsync(entity);
        var insertResult = await repository.AddAsync(entity);

        if (insertResult is InsertResult.FailureReferenceNotFound failure)
        {
            throw new ValidationException(
                new[] { new ValidationFailure(failure.ReferenceName, $"Reference '{failure.ReferenceName}' does not exist.") }
            );
        }

        var request = httpContext.Request;
        return insertResult switch
        {
            InsertResult.InsertSuccess success
                => Results.Created(
                    $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                    null
                ),
            InsertResult.UnknownFailure => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500),
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

            var updateResult = await repository.UpdateAsync(entity);

            if (updateResult is UpdateResult.FailureReferenceNotFound failure)
            {
                throw new ValidationException(
                    new[] { new ValidationFailure(failure.ReferenceName, $"Reference '{failure.ReferenceName}' does not exist.") }
                );
            }

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
