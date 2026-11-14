// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http.Extensions;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class DataStoreModule : IEndpointModule
{
    private const string DuplicateNameError = "A data store with this name already exists.";

    private static IResult DuplicateName(HttpContext httpContext) =>
        FailureResults.DataValidation(
            [new ValidationFailure("Name", DuplicateNameError)],
            httpContext.TraceIdentifier
        );

    private static IResult ManagedConflict(HttpContext httpContext, int manageId)
    {
        var request = httpContext.Request;
        string managedUrl = UriHelper.BuildAbsolute(
            request.Scheme,
            request.Host,
            new PathString(request.PathBase.Value?.TrimEnd('/')),
            new PathString("/v3/dataStores/manage/" + manageId.ToString(CultureInfo.InvariantCulture))
        );
        return Results.Json(
            FailureResponse.ForConflict(
                $"This data store is managed. Use {managedUrl} to manage it.",
                httpContext.TraceIdentifier
            ),
            contentType: "application/problem+json",
            statusCode: (int)HttpStatusCode.Conflict
        );
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredPost("/v3/dataStores/", InsertDataStore);
        endpoints
            .MapLimitedAccess("/v3/dataStores/", GetAll)
            .WithDescription(
                "Filters combine within the selected tenant before paging. Sort fields are id, name, and dataStoreType (case-insensitive). "
                    + "Name ordering uses the trimmed, ASCII-folded name; type ordering uses the exact stored text. Both use lexicographic UTF-8 byte order, then id in the same direction. "
                    + "Directions are asc, ascending, desc, and descending (case-insensitive); an explicit sort field defaults to ascending. "
                    + "Without orderBy, results use id ascending even when direction is supplied. An omitted limit returns all remaining rows, including with offset alone."
            );
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
            DataStoreInsertResult.FailureDuplicateName => DuplicateName(httpContext),
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
        int id,
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
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Update(
        int id,
        DataStoreUpdateCommand command,
        DataStoreUpdateCommand.Validator validator,
        HttpContext httpContext,
        IDataStoreRepository repository
    )
    {
        PutGuards.GuardRouteIdMatchesBodyId(id, command.Id);

        await validator.GuardAsync(command);

        var updateResult = await repository.UpdateDataStore(command);

        return updateResult switch
        {
            DataStoreUpdateResult.Success => Results.NoContent(),
            DataStoreUpdateResult.FailureManaged managed => ManagedConflict(httpContext, managed.ManageId),
            DataStoreUpdateResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound(
                    $"DataStore {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            DataStoreUpdateResult.FailureDuplicateName => DuplicateName(httpContext),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Delete(
        int id,
        HttpContext httpContext,
        IDataStoreRepository repository
    )
    {
        DataStoreDeleteResult deleteResult = await repository.DeleteDataStore(id);
        return deleteResult switch
        {
            DataStoreDeleteResult.Success => Results.NoContent(),
            DataStoreDeleteResult.FailureManaged managed => ManagedConflict(httpContext, managed.ManageId),
            DataStoreDeleteResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound(
                    $"DataStore {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetApplicationsByDataStore(
        int id,
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
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
