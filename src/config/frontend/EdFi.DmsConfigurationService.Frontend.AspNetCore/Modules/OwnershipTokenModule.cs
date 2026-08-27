// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.OwnershipToken;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class OwnershipTokenModule : IEndpointModule
{
    private const int MinimumOwnershipTokenId = 1;
    private const int MaximumOwnershipTokenId = 32767;
    private const int MinimumApiClientId = 1;

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredPost("/v3/ownershipTokens/", InsertOwnershipToken);
        endpoints
            .MapSecuredGet("/v3/ownershipTokens/", GetAllOwnershipTokens)
            .Produces<List<OwnershipTokenResponse>>(200);
        endpoints
            .MapSecuredGet("/v3/ownershipTokens/{id}", GetOwnershipTokenById)
            .Produces<OwnershipTokenResponse>(200);
        endpoints.MapSecuredPut("/v3/ownershipTokens/{id}", UpdateOwnershipToken);
        endpoints
            .MapSecuredGet("/v3/apiClients/{id}/ownership", GetApiClientOwnership)
            .Produces<ApiClientOwnershipResponse>(200);
        endpoints.MapSecuredPut("/v3/apiClients/{id}/ownership", UpdateApiClientOwnership);
    }

    private static async Task<IResult> InsertOwnershipToken(
        OwnershipTokenInsertCommand command,
        OwnershipTokenInsertCommand.Validator validator,
        HttpContext httpContext,
        IOwnershipTokenRepository repository,
        ILogger<OwnershipTokenModule> logger
    )
    {
        await validator.GuardAsync(command);

        OwnershipTokenInsertResult insertResult = await repository.InsertOwnershipToken(command);
        var request = httpContext.Request;

        return insertResult switch
        {
            OwnershipTokenInsertResult.Success success => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                new OwnershipTokenResponse { Id = success.Id, Description = command.Description }
            ),
            OwnershipTokenInsertResult.FailureUnknown failure => LogAndReturnUnknown(
                logger,
                failure.FailureMessage,
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetAllOwnershipTokens(
        IOwnershipTokenRepository repository,
        [AsParameters] FrontendOwnershipTokenQuery query,
        OwnershipTokenPagingQueryValidator validator,
        HttpContext httpContext,
        ILogger<OwnershipTokenModule> logger
    )
    {
        await validator.GuardAsync(query);
        OwnershipTokenQueryResult getResult = await repository.QueryOwnershipTokens(query.ToQuery());

        return getResult switch
        {
            OwnershipTokenQueryResult.Success success => Results.Ok(success.OwnershipTokens),
            OwnershipTokenQueryResult.FailureUnknown failure => LogAndReturnUnknown(
                logger,
                failure.FailureMessage,
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetOwnershipTokenById(
        int id,
        HttpContext httpContext,
        IOwnershipTokenRepository repository,
        ILogger<OwnershipTokenModule> logger
    )
    {
        ValidateOwnershipTokenRouteId(id);

        OwnershipTokenGetResult getResult = await repository.GetOwnershipToken(id);

        return getResult switch
        {
            OwnershipTokenGetResult.Success success => Results.Ok(success.OwnershipToken),
            OwnershipTokenGetResult.FailureNotFound => FailureResults.NotFound(
                $"OwnershipToken {id} not found.",
                httpContext.TraceIdentifier
            ),
            OwnershipTokenGetResult.FailureUnknown failure => LogAndReturnUnknown(
                logger,
                failure.FailureMessage,
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> UpdateOwnershipToken(
        int id,
        OwnershipTokenUpdateCommand command,
        OwnershipTokenUpdateCommand.Validator validator,
        HttpContext httpContext,
        IOwnershipTokenRepository repository,
        ILogger<OwnershipTokenModule> logger
    )
    {
        await validator.GuardAsync(command);
        ValidateOwnershipTokenRouteId(id);

        if (command.Id != id)
        {
            throw new ValidationException([
                new ValidationFailure("Id", "Request body id must match the id in the url."),
            ]);
        }

        OwnershipTokenUpdateResult updateResult = await repository.UpdateOwnershipToken(command);

        return updateResult switch
        {
            OwnershipTokenUpdateResult.Success => Results.NoContent(),
            OwnershipTokenUpdateResult.FailureNotFound => FailureResults.NotFound(
                $"OwnershipToken {id} not found.",
                httpContext.TraceIdentifier
            ),
            OwnershipTokenUpdateResult.FailureUnknown failure => LogAndReturnUnknown(
                logger,
                failure.FailureMessage,
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetApiClientOwnership(
        int id,
        HttpContext httpContext,
        IOwnershipTokenRepository repository,
        ILogger<OwnershipTokenModule> logger
    )
    {
        ValidateApiClientRouteId(id);

        ApiClientOwnershipGetResult getResult = await repository.GetApiClientOwnership(id);

        return getResult switch
        {
            ApiClientOwnershipGetResult.Success success => Results.Ok(success.Ownership),
            ApiClientOwnershipGetResult.FailureApiClientNotFound => FailureResults.NotFound(
                "ApiClient not found",
                httpContext.TraceIdentifier
            ),
            ApiClientOwnershipGetResult.FailureUnknown failure => LogAndReturnUnknown(
                logger,
                failure.FailureMessage,
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> UpdateApiClientOwnership(
        int id,
        ApiClientOwnershipUpdateCommand command,
        ApiClientOwnershipUpdateCommand.Validator validator,
        HttpContext httpContext,
        IOwnershipTokenRepository repository,
        ILogger<OwnershipTokenModule> logger
    )
    {
        ValidateApiClientRouteId(id);
        PutGuards.GuardRouteIdMatchesBodyId(id, command.ApiClientId);
        command.ApiClientId = id;
        await validator.GuardAsync(command);

        ApiClientOwnershipUpdateResult updateResult = await repository.UpdateApiClientOwnership(command);

        return updateResult switch
        {
            ApiClientOwnershipUpdateResult.Success => Results.NoContent(),
            ApiClientOwnershipUpdateResult.FailureApiClientNotFound => FailureResults.NotFound(
                "ApiClient not found",
                httpContext.TraceIdentifier
            ),
            ApiClientOwnershipUpdateResult.FailureOwnershipTokenNotFound => Results.Json(
                FailureResponse.ForUnresolvedReference(
                    "One or more ownership tokens were not found.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.Conflict
            ),
            ApiClientOwnershipUpdateResult.FailureUnknown failure => LogAndReturnUnknown(
                logger,
                failure.FailureMessage,
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static IResult LogAndReturnUnknown(
        ILogger<OwnershipTokenModule> logger,
        string failureMessage,
        string traceIdentifier
    )
    {
        logger.LogError("Ownership token repository failure: {Message}", SanitizeForLog(failureMessage));
        return FailureResults.Unknown(traceIdentifier);
    }

    private static void ValidateOwnershipTokenRouteId(int id)
    {
        if (id is < MinimumOwnershipTokenId or > MaximumOwnershipTokenId)
        {
            throw new ValidationException([
                new ValidationFailure(
                    "Id",
                    $"Route id must be between {MinimumOwnershipTokenId} and {MaximumOwnershipTokenId}."
                ),
            ]);
        }
    }

    private static void ValidateApiClientRouteId(int id)
    {
        if (id < MinimumApiClientId)
        {
            throw new ValidationException([
                new ValidationFailure(
                    "Id",
                    $"Route id must be greater than or equal to {MinimumApiClientId}."
                ),
            ]);
        }
    }

    private static string SanitizeForLog(string? input) => LoggingUtility.SanitizeForLog(input);
}
