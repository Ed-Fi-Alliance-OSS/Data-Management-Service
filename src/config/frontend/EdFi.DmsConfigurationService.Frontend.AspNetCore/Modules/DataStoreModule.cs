// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class DataStoreModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredPost("/v3/dataStores/", InsertDataStore);
        endpoints.MapLimitedAccess("/v3/dataStores/", GetAll);
        endpoints.MapLimitedAccess($"/v3/dataStores/{{id}}", GetById);
        endpoints.MapSecuredPut($"/v3/dataStores/{{id}}", Update);
        endpoints.MapSecuredDelete($"/v3/dataStores/{{id}}", Delete);
        endpoints
            .MapSecuredGet($"/v3/dataStores/{{id}}/applications/", GetApplicationsByDataStore)
            .Produces<List<ApplicationResponse>>(200);
    }

    private static async Task<IResult> InsertDataStore(
        DataStoreInsertCommand entity,
        DataStoreInsertCommand.Validator validator,
        HttpContext httpContext,
        IDataStoreRepository repository
    )
    {
        await validator.GuardAsync(entity);
        var insertResult = await repository.InsertDataStore(entity);

        var request = httpContext.Request;
        return insertResult switch
        {
            DataStoreInsertResult.Success success => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                new
                {
                    Id = success.Id,
                    Status = 201,
                    Title = $"New DataStore {entity.Name} has been created successfully.",
                }
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetAll(
        IDataStoreRepository repository,
        [AsParameters] FrontendDataStoreQuery query,
        DataStorePagingQueryValidator validator,
        HttpContext httpContext
    )
    {
        await validator.GuardAsync(query);
        DataStoreQueryResult getResult = await repository.QueryDataStore(query.ToQuery());
        return getResult switch
        {
            DataStoreQueryResult.Success success => Results.Ok(success.DataStoreResponses),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        long id,
        HttpContext httpContext,
        IDataStoreRepository repository
    )
    {
        DataStoreGetResult getResult = await repository.GetDataStore(id);
        return getResult switch
        {
            DataStoreGetResult.Success success => Results.Ok(success.DataStoreResponse),
            DataStoreGetResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"DataStore {id} not found. It may have been recently deleted.",
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
        DataStoreUpdateCommand command,
        DataStoreUpdateCommand.Validator validator,
        HttpContext httpContext,
        IDataStoreRepository repository
    )
    {
        await validator.GuardAsync(command);

        if (command.Id != id)
        {
            throw new ValidationException(
                new[] { new ValidationFailure("Id", "Request body id must match the id in the url.") }
            );
        }

        var updateResult = await repository.UpdateDataStore(command);

        return updateResult switch
        {
            DataStoreUpdateResult.Success => Results.NoContent(),
            DataStoreUpdateResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound(
                    $"DataStore {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Delete(
        long id,
        HttpContext httpContext,
        IDataStoreRepository repository
    )
    {
        DataStoreDeleteResult deleteResult = await repository.DeleteDataStore(id);
        return deleteResult switch
        {
            DataStoreDeleteResult.Success => Results.NoContent(),
            DataStoreDeleteResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound(
                    $"DataStore {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetApplicationsByDataStore(
        long id,
        IDataStoreRepository repository,
        [AsParameters] PagingQuery query,
        HttpContext httpContext
    )
    {
        ApplicationByDataStoreQueryResult getResult = await repository.QueryApplicationByDataStore(id, query);
        return getResult switch
        {
            ApplicationByDataStoreQueryResult.Success success => Results.Ok(success.ApplicationResponse),
            ApplicationByDataStoreQueryResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound(
                    $"DataStore {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
