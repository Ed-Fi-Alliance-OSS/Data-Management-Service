// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstance;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class DmsInstanceModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredPost("/v2/dmsInstances/", InsertDmsInstance);
        endpoints.MapSecuredGet("/v2/dmsInstances/", GetAll);
        endpoints.MapSecuredGet($"/v2/dmsInstances/{{id}}", GetById);
        endpoints.MapSecuredPut($"/v2/dmsInstances/{{id}}", Update);
        endpoints.MapSecuredDelete($"/v2/dmsInstances/{{id}}", Delete);
    }

    private static async Task<IResult> InsertDmsInstance(
        DmsInstanceInsertCommand entity,
        DmsInstanceInsertCommand.Validator validator,
        HttpContext httpContext,
        IDmsInstanceRepository repository
    )
    {
        await validator.GuardAsync(entity);
        var insertResult = await repository.InsertDmsInstance(entity);

        var request = httpContext.Request;
        return insertResult switch
        {
            DmsInstanceInsertResult.Success success => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                new
                {
                    Id = success.Id,
                    Status = 201,
                    Title = $"New DmsInstance {entity.InstanceName} has been created successfully.",
                }
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetAll(
        IDmsInstanceRepository repository,
        [AsParameters] PagingQuery query,
        HttpContext httpContext
    )
    {
        DmsInstanceQueryResult getResult = await repository.QueryDmsInstance(query);
        return getResult switch
        {
            DmsInstanceQueryResult.Success success => Results.Ok(success.DmsInstanceResponses),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        long id,
        HttpContext httpContext,
        IDmsInstanceRepository repository
    )
    {
        DmsInstanceGetResult getResult = await repository.GetDmsInstance(id);
        return getResult switch
        {
            DmsInstanceGetResult.Success success => Results.Ok(success.DmsInstanceResponse),
            DmsInstanceGetResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"DmsInstance {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Update(
        long id,
        DmsInstanceUpdateCommand command,
        DmsInstanceUpdateCommand.Validator validator,
        HttpContext httpContext,
        IDmsInstanceRepository repository
    )
    {
        await validator.GuardAsync(command);

        if (command.Id != id)
        {
            throw new ValidationException(
                new[] { new ValidationFailure("Id", "Request body id must match the id in the url.") }
            );
        }

        var updateResult = await repository.UpdateDmsInstance(command);

        return updateResult switch
        {
            DmsInstanceUpdateResult.Success => Results.NoContent(),
            DmsInstanceUpdateResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound(
                    $"DmsInstance {id} not found. It may have been recently deleted.",
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
        IDmsInstanceRepository repository
    )
    {
        DmsInstanceDeleteResult deleteResult = await repository.DeleteDmsInstance(id);
        return deleteResult switch
        {
            DmsInstanceDeleteResult.Success => Results.NoContent(),
            DmsInstanceDeleteResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound(
                    $"DmsInstance {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
