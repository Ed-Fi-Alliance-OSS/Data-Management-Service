// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using EdFi.DmsConfigurationService.Backend.Introspection;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.Token;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

/// <summary>
/// Endpoint module for OAuth token introspection (/oauth/token_info)
/// Provides detailed information about an access token including active status,
/// client details, authorized resources, and education organization context.
/// </summary>
public class TokenInfoModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // OAuth token introspection endpoint
        // Supports both JSON and form-encoded requests
        //
        endpoints
            .MapPost("/oauth/token_info", HandleJsonRequest)
            .Accepts<TokenInfoRequest>(contentType: "application/json")
            .Produces<TokenInfoResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization()
            .DisableAntiforgery();

        endpoints
            .MapPost("/oauth/token_info", HandleFormRequest)
            .Accepts<TokenInfoRequest>(contentType: "application/x-www-form-urlencoded")
            .Produces<TokenInfoResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization()
            .DisableAntiforgery();
    }

    /// <summary>
    /// Handles JSON-encoded token introspection requests
    /// </summary>
    private async Task<IResult> HandleJsonRequest(
        [FromBody] TokenInfoRequest request,
        TokenInfoRequest.Validator validator,
        ITokenInfoProvider tokenInfoProvider,
        HttpContext httpContext
    )
    {
        return await ProcessTokenInfoRequest(request, validator, tokenInfoProvider, httpContext);
    }

    /// <summary>
    /// Handles form-encoded token introspection requests
    /// </summary>
    private async Task<IResult> HandleFormRequest(
        [FromForm] TokenInfoRequest request,
        TokenInfoRequest.Validator validator,
        ITokenInfoProvider tokenInfoProvider,
        HttpContext httpContext
    )
    {
        return await ProcessTokenInfoRequest(request, validator, tokenInfoProvider, httpContext);
    }

    /// <summary>
    /// Common processing logic for token introspection requests
    /// </summary>
    private static async Task<IResult> ProcessTokenInfoRequest(
        TokenInfoRequest request,
        TokenInfoRequest.Validator validator,
        ITokenInfoProvider tokenInfoProvider,
        HttpContext httpContext
    )
    {
        // Validate request
        var validationResult = await validator.ValidateAsync(request);
        if (!validationResult.IsValid)
        {
            return Results.Json(
                FailureResponse.ForBadRequest("Invalid token_info request", httpContext.TraceIdentifier),
                statusCode: (int)HttpStatusCode.BadRequest
            );
        }

        // Get token information
        var tokenInfo = await tokenInfoProvider.GetTokenInfoAsync(request.Token!);

        if (tokenInfo == null)
        {
            return Results.Json(
                FailureResponse.ForNotFound(
                    "Token not found or invalid",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            );
        }

        // Return token information
        return Results.Ok(tokenInfo);
    }
}
