// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.TokenInfo;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

/// <summary>
/// Endpoint module for OAuth token introspection
/// </summary>
public class TokenInfoModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapPost("/oauth/token_info", HandleTokenInfoRequest)
            .Accepts<TokenInfoRequest>(contentType: "application/json")
            .Produces<TokenInfoResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .DisableAntiforgery();

        endpoints
            .MapPost("/oauth/token_info", HandleTokenInfoRequest)
            .Accepts<TokenInfoRequest>(contentType: "application/x-www-form-urlencoded")
            .Produces<TokenInfoResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .DisableAntiforgery();
    }

    internal static async Task<IResult> HandleTokenInfoRequest(
        HttpContext context,
        ITokenInfoProvider tokenInfoProvider,
        ILogger<TokenInfoModule> logger
    )
    {
        try
        {
            // Extract and validate Authorization header
            if (
                !context.Request.Headers.TryGetValue("Authorization", out var authHeader)
                || !authHeader.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            )
            {
                logger.LogWarning("Token info request missing or invalid Authorization header");
                return Results.Unauthorized();
            }

            string headerToken = authHeader.ToString()["Bearer ".Length..].Trim();

            // Parse request body (support both JSON and form-encoded)
            TokenInfoRequest? request = null;

            if (context.Request.ContentType?.Contains("application/json") == true)
            {
                request = await context.Request.ReadFromJsonAsync<TokenInfoRequest>();
            }
            else if (context.Request.ContentType?.Contains("application/x-www-form-urlencoded") == true)
            {
                var form = await context.Request.ReadFormAsync();
                request = new TokenInfoRequest { Token = form["token"].ToString() };
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Token))
            {
                logger.LogWarning("Token info request missing token parameter");
                return Results.Unauthorized();
            }

            // Validate that Authorization header token matches body token (self-introspection)
            if (!string.Equals(headerToken, request.Token.Trim(), StringComparison.Ordinal))
            {
                logger.LogWarning("Authorization header token does not match body token");
                return Results.Unauthorized();
            }

            var result = await tokenInfoProvider.GetTokenInfoAsync(request.Token);

            if (result == null)
            {
                logger.LogInformation("Token introspection returned null (invalid or expired token)");
                return Results.Unauthorized();
            }

            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing token info request");
            return Results.Problem(
                statusCode: 500,
                title: "Internal Server Error",
                detail: "An error occurred while processing the token info request"
            );
        }
    }
}
