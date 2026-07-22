// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreDerivative;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class DataStoreDerivativeModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredPost("/v3/dataStoreDerivatives/", InsertDataStoreDerivative);
        endpoints.MapLimitedAccess("/v3/dataStoreDerivatives/", GetAll);
        endpoints.MapLimitedAccess($"/v3/dataStoreDerivatives/{{id}}", GetById);
        endpoints.MapSecuredPut($"/v3/dataStoreDerivatives/{{id}}", Update);
        endpoints.MapSecuredDelete($"/v3/dataStoreDerivatives/{{id}}", Delete);
    }

    private static async Task<IResult> InsertDataStoreDerivative(
        DataStoreDerivativeInsertCommand entity,
        DataStoreDerivativeInsertCommand.Validator validator,
        HttpContext httpContext,
        IDataStoreDerivativeRepository repository
    )
    {
        await validator.GuardAsync(entity);
        var insertResult = await repository.InsertDataStoreDerivative(entity);

        var request = httpContext.Request;
        return insertResult switch
        {
            DataStoreDerivativeInsertResult.Success success => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                new
                {
                    Id = success.Id,
                    Status = 201,
                    Title = $"New DataStoreDerivative of type {entity.DerivativeType} has been created successfully.",
                }
            ),
            DataStoreDerivativeInsertResult.FailureForeignKeyViolation => FailureResults.UnresolvedReference(
                "One or more referenced items could not be resolved. See 'errors' for details.",
                httpContext.TraceIdentifier,
                ["The specified DataStore does not exist."]
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetAll(
        IDataStoreDerivativeRepository repository,
        [AsParameters] FrontendPagingQuery query,
        DataStoreDerivativePagingQueryValidator validator,
        HttpContext httpContext
    )
    {
        await validator.GuardAsync(query);
        DataStoreDerivativeQueryResult getResult = await repository.QueryDataStoreDerivative(query);
        return getResult switch
        {
            DataStoreDerivativeQueryResult.Success success => Results.Ok(
                success.DataStoreDerivativeResponses
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        long id,
        HttpContext httpContext,
        IDataStoreDerivativeRepository repository
    )
    {
        DataStoreDerivativeGetResult getResult = await repository.GetDataStoreDerivative(id);
        return getResult switch
        {
            DataStoreDerivativeGetResult.Success success => Results.Ok(success.DataStoreDerivativeResponse),
            DataStoreDerivativeGetResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"DataStoreDerivative {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Update(
        long id,
        DataStoreDerivativeUpdateCommand command,
        DataStoreDerivativeUpdateCommand.Validator validator,
        HttpContext httpContext,
        IDataStoreDerivativeRepository repository
    )
    {
        await validator.GuardAsync(command);

        if (command.Id != id)
        {
            throw new ValidationException(
                new[] { new ValidationFailure("Id", "Request body id must match the id in the url.") }
            );
        }

        var updateResult = await repository.UpdateDataStoreDerivative(command);

        return updateResult switch
        {
            DataStoreDerivativeUpdateResult.Success => Results.NoContent(),
            DataStoreDerivativeUpdateResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"DataStoreDerivative {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            DataStoreDerivativeUpdateResult.FailureForeignKeyViolation => FailureResults.UnresolvedReference(
                "One or more referenced items could not be resolved. See 'errors' for details.",
                httpContext.TraceIdentifier,
                ["The specified DataStore does not exist."]
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Delete(
        long id,
        HttpContext httpContext,
        IDataStoreDerivativeRepository repository
    )
    {
        DataStoreDerivativeDeleteResult deleteResult = await repository.DeleteDataStoreDerivative(id);
        return deleteResult switch
        {
            DataStoreDerivativeDeleteResult.Success => Results.NoContent(),
            DataStoreDerivativeDeleteResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"DataStoreDerivative {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
