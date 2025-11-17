// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstanceDerivative;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class DmsInstanceDerivativeModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredPost("/v2/dmsInstanceDerivatives/", InsertDmsInstanceDerivative);
        endpoints.MapLimitedAccess("/v2/dmsInstanceDerivatives/", GetAll);
        endpoints.MapLimitedAccess($"/v2/dmsInstanceDerivatives/{{id}}", GetById);
        endpoints.MapSecuredPut($"/v2/dmsInstanceDerivatives/{{id}}", Update);
        endpoints.MapSecuredDelete($"/v2/dmsInstanceDerivatives/{{id}}", Delete);
    }

    private static async Task<IResult> InsertDmsInstanceDerivative(
        DmsInstanceDerivativeInsertCommand entity,
        DmsInstanceDerivativeInsertCommand.Validator validator,
        HttpContext httpContext,
        IDmsInstanceDerivativeRepository repository
    )
    {
        await validator.GuardAsync(entity);
        var insertResult = await repository.InsertDmsInstanceDerivative(entity);

        var request = httpContext.Request;
        return insertResult switch
        {
            DmsInstanceDerivativeInsertResult.Success success => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                new
                {
                    Id = success.Id,
                    Status = 201,
                    Title = $"New DmsInstanceDerivative of type {entity.DerivativeType} has been created successfully.",
                }
            ),
            DmsInstanceDerivativeInsertResult.FailureForeignKeyViolation => Results.Json(
                FailureResponse.ForBadRequest(
                    "The specified DmsInstance does not exist.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.BadRequest
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetAll(
        IDmsInstanceDerivativeRepository repository,
        [AsParameters] PagingQuery query,
        HttpContext httpContext
    )
    {
        DmsInstanceDerivativeQueryResult getResult = await repository.QueryDmsInstanceDerivative(query);
        return getResult switch
        {
            DmsInstanceDerivativeQueryResult.Success success => Results.Ok(
                success.DmsInstanceDerivativeResponses
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        long id,
        HttpContext httpContext,
        IDmsInstanceDerivativeRepository repository
    )
    {
        DmsInstanceDerivativeGetResult getResult = await repository.GetDmsInstanceDerivative(id);
        return getResult switch
        {
            DmsInstanceDerivativeGetResult.Success success => Results.Ok(
                success.DmsInstanceDerivativeResponse
            ),
            DmsInstanceDerivativeGetResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"DmsInstanceDerivative {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Update(
        long id,
        DmsInstanceDerivativeUpdateCommand command,
        DmsInstanceDerivativeUpdateCommand.Validator validator,
        HttpContext httpContext,
        IDmsInstanceDerivativeRepository repository
    )
    {
        await validator.GuardAsync(command);

        if (command.Id != id)
        {
            throw new ValidationException(
                new[] { new ValidationFailure("Id", "Request body id must match the id in the url.") }
            );
        }

        var updateResult = await repository.UpdateDmsInstanceDerivative(command);

        return updateResult switch
        {
            DmsInstanceDerivativeUpdateResult.Success => Results.NoContent(),
            DmsInstanceDerivativeUpdateResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"DmsInstanceDerivative {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            DmsInstanceDerivativeUpdateResult.FailureForeignKeyViolation => Results.Json(
                FailureResponse.ForBadRequest(
                    "The specified DmsInstance does not exist.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.BadRequest
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Delete(
        long id,
        HttpContext httpContext,
        IDmsInstanceDerivativeRepository repository
    )
    {
        DmsInstanceDerivativeDeleteResult deleteResult = await repository.DeleteDmsInstanceDerivative(id);
        return deleteResult switch
        {
            DmsInstanceDerivativeDeleteResult.Success => Results.NoContent(),
            DmsInstanceDerivativeDeleteResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"DmsInstanceDerivative {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
