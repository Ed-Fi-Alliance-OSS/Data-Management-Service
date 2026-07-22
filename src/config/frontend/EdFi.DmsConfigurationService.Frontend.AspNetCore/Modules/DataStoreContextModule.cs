// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreContext;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class DataStoreContextModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredPost("/v3/dataStoreContexts/", InsertDataStoreContext);
        endpoints.MapLimitedAccess("/v3/dataStoreContexts/", GetAll);
        endpoints.MapLimitedAccess($"/v3/dataStoreContexts/{{id}}", GetById);
        endpoints.MapSecuredPut($"/v3/dataStoreContexts/{{id}}", Update);
        endpoints.MapSecuredDelete($"/v3/dataStoreContexts/{{id}}", Delete);
    }

    private static async Task<IResult> InsertDataStoreContext(
        DataStoreContextInsertCommand command,
        DataStoreContextInsertCommand.Validator validator,
        HttpContext httpContext,
        IDataStoreContextRepository dataStoreContextRepository,
        ILogger<DataStoreContextModule> logger
    )
    {
        logger.LogDebug("Entering InsertDataStoreContext");
        await validator.GuardAsync(command);

        var repositoryResult = await dataStoreContextRepository.InsertDataStoreContext(command);

        return repositoryResult switch
        {
            DataStoreContextInsertResult.Success success => Results.Created(
                $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}{httpContext.Request.Path.Value?.TrimEnd('/')}/{success.Id}",
                new { Id = success.Id }
            ),
            DataStoreContextInsertResult.FailureDataStoreNotFound => throw new ValidationException(
                new[] { new ValidationFailure("DataStoreId", "Reference 'DataStoreId' does not exist.") }
            ),
            DataStoreContextInsertResult.FailureDuplicateDataStoreContext duplicate =>
                FailureResults.NonUniqueIdentity(
                    "The identifying value(s) of the item are the same as another item that already exists.",
                    httpContext.TraceIdentifier,
                    [
                        $"Data store context with DataStoreId '{duplicate.DataStoreId}' and ContextKey '{duplicate.ContextKey}' already exists.",
                    ]
                ),
            DataStoreContextInsertResult.FailureUnknown => FailureResults.Unknown(
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetAll(
        IDataStoreContextRepository dataStoreContextRepository,
        [AsParameters] FrontendPagingQuery query,
        DataStoreContextPagingQueryValidator validator,
        HttpContext httpContext
    )
    {
        await validator.GuardAsync(query);
        var getResult = await dataStoreContextRepository.QueryDataStoreContext(query);
        return getResult switch
        {
            DataStoreContextQueryResult.Success success => Results.Ok(success.DataStoreContextResponses),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        long id,
        HttpContext httpContext,
        IDataStoreContextRepository dataStoreContextRepository,
        ILogger<DataStoreContextModule> logger
    )
    {
        logger.LogDebug("Entering DataStoreContext GetById for id: {Id}", id);
        var getResult = await dataStoreContextRepository.GetDataStoreContext(id);
        return getResult switch
        {
            DataStoreContextGetResult.Success success => Results.Ok(success.DataStoreContextResponse),
            DataStoreContextGetResult.FailureNotFound => FailureResults.NotFound(
                "Data store context not found",
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Update(
        long id,
        DataStoreContextUpdateCommand.Validator validator,
        DataStoreContextUpdateCommand command,
        HttpContext httpContext,
        IDataStoreContextRepository dataStoreContextRepository,
        ILogger<DataStoreContextModule> logger
    )
    {
        logger.LogDebug("Entering DataStoreContext Update for id: {Id}", id);
        await validator.GuardAsync(command);

        if (command.Id != id)
        {
            throw new ValidationException(
                new[] { new ValidationFailure("Id", "Request body id must match the id in the url.") }
            );
        }

        var updateResult = await dataStoreContextRepository.UpdateDataStoreContext(command);

        return updateResult switch
        {
            DataStoreContextUpdateResult.Success => Results.NoContent(),
            DataStoreContextUpdateResult.FailureNotExists => FailureResults.NotFound(
                "Data store context not found",
                httpContext.TraceIdentifier
            ),
            DataStoreContextUpdateResult.FailureDataStoreNotFound => throw new ValidationException(
                new[] { new ValidationFailure("DataStoreId", "Reference 'DataStoreId' does not exist.") }
            ),
            DataStoreContextUpdateResult.FailureDuplicateDataStoreContext duplicate =>
                FailureResults.NonUniqueIdentity(
                    "The identifying value(s) of the item are the same as another item that already exists.",
                    httpContext.TraceIdentifier,
                    [
                        $"Data store context with DataStoreId '{duplicate.DataStoreId}' and ContextKey '{duplicate.ContextKey}' already exists.",
                    ]
                ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Delete(
        long id,
        HttpContext httpContext,
        IDataStoreContextRepository dataStoreContextRepository,
        ILogger<DataStoreContextModule> logger
    )
    {
        logger.LogInformation("Deleting DataStoreContext {Id}", id);

        var deleteResult = await dataStoreContextRepository.DeleteDataStoreContext(id);

        return deleteResult switch
        {
            DataStoreContextDeleteResult.Success => Results.NoContent(),
            DataStoreContextDeleteResult.FailureNotExists => FailureResults.NotFound(
                "Data store context not found",
                httpContext.TraceIdentifier
            ),
            DataStoreContextDeleteResult.FailureUnknown => FailureResults.Unknown(
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
