// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class ClaimSetModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v2/claimSets/", InsertClaimSet).RequireAuthorizationWithPolicy();
        endpoints.MapGet("/v2/claimSets/", GetAll).RequireAuthorizationWithPolicy();
        endpoints.MapGet($"/v2/claimSets/{{id}}", GetById).RequireAuthorizationWithPolicy();
        endpoints.MapGet($"/v2/claimSets/{{id}}/export", Export).RequireAuthorizationWithPolicy();
        endpoints.MapPut($"/v2/claimSets/{{id}}", Update).RequireAuthorizationWithPolicy();
        endpoints.MapDelete($"/v2/claimSets/{{id}}", Delete).RequireAuthorizationWithPolicy();
        endpoints.MapPost("/v2/claimSets/copy", Copy).RequireAuthorizationWithPolicy();
        endpoints.MapPost("/v2/claimSets/import", Import).RequireAuthorizationWithPolicy();
    }

    private static async Task<IResult> InsertClaimSet(
        ClaimSetInsertCommand entity,
        ClaimSetInsertCommand.Validator validator,
        HttpContext httpContext,
        IClaimSetRepository repository
    )
    {
        await validator.GuardAsync(entity);
        var insertResult = await repository.InsertClaimSet(entity);

        var request = httpContext.Request;
        return insertResult switch
        {
            ClaimSetInsertResult.Success success => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                null
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetAll(
        IClaimSetRepository repository,
        [AsParameters] PagingQuery query,
        HttpContext httpContext,
        [FromQuery] bool verbose = false
    )
    {
        ClaimSetQueryResult result = await repository.QueryClaimSet(query, verbose);
        return result switch
        {
            ClaimSetQueryResult.Success success => Results.Ok(success.ClaimSetResponses),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        long id,
        HttpContext httpContext,
        IClaimSetRepository repository,
        ILogger<ClaimSetModule> logger,
        [FromQuery] bool verbose = false
    )
    {
        ClaimSetGetResult result = await repository.GetClaimSet(id, verbose);
        return result switch
        {
            ClaimSetGetResult.Success success => Results.Ok(success.ClaimSetResponse),
            ClaimSetGetResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"ClaimSet {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Update(
        long id,
        ClaimSetUpdateCommand command,
        ClaimSetUpdateCommand.Validator validator,
        HttpContext httpContext,
        IClaimSetRepository repository
    )
    {
        await validator.GuardAsync(command);

        if (command.Id != id)
        {
            throw new ValidationException(
                new[] { new ValidationFailure("Id", "Request body id must match the id in the url.") }
            );
        }

        var result = await repository.UpdateClaimSet(command);

        return result switch
        {
            ClaimSetUpdateResult.Success => Results.NoContent(),
            ClaimSetUpdateResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"ClaimSet {id} not found. It may have been recently deleted.",
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
        IClaimSetRepository repository,
        ILogger<ClaimSetModule> logger
    )
    {
        ClaimSetDeleteResult result = await repository.DeleteClaimSet(id);
        return result switch
        {
            ClaimSetDeleteResult.Success => Results.NoContent(),
            ClaimSetDeleteResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"ClaimSet {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Export(
        long id,
        HttpContext httpContext,
        IClaimSetRepository repository,
        ILogger<ClaimSetModule> logger
    )
    {
        ClaimSetExportResult result = await repository.Export(id);
        return result switch
        {
            ClaimSetExportResult.Success success => Results.Ok(success.ClaimSetExportResponse),
            ClaimSetExportResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"ClaimSet {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Copy(
        ClaimSetCopyCommand entity,
        ClaimSetCopyCommand.Validator validator,
        HttpContext httpContext,
        IClaimSetRepository repository
    )
    {
        await validator.GuardAsync(entity);

        var result = await repository.Copy(entity);

        var request = httpContext.Request;

        return result switch
        {
            ClaimSetCopyResult.Success success => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                null
            ),
            ClaimSetCopyResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"OriginalId {entity.OriginalId} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Import(
        ClaimSetImportCommand entity,
        HttpContext httpContext,
        IClaimSetRepository repository,
        IClaimSetDataProvider provider
    )
    {
        var validator = new ClaimSetImportCommand.Validator(provider);
        await validator.GuardAsync(entity);

        var insertResult = await repository.Import(entity);

        var request = httpContext.Request;
        return insertResult switch
        {
            ClaimSetImportResult.Success success => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                null
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
