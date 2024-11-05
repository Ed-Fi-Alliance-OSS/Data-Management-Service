// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Model;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class IdentityModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/connect/register", RegisterClient);
        endpoints.MapPost("/connect/token", GetClientAccessToken);
    }

    private async Task<IResult> RegisterClient(
        RegisterRequest.Validator validator,
        RegisterRequest model,
        IClientRepository clientRepository,
        IOptions<IdentitySettings> identitySettings
    )
    {
        bool allowRegistration = identitySettings.Value.AllowRegistration;
        if (allowRegistration)
        {
            await validator.GuardAsync(model);

            await clientRepository.CreateClientAsync(
                model.ClientId!,
                model.ClientSecret!,
                model.DisplayName!
            );
            return Results.Ok($"Registered client {model.ClientId} successfully.");
        }
        return Results.Forbid();
    }

    private static async Task<IResult> GetClientAccessToken(
        TokenRequest.Validator validator,
        TokenRequest model,
        ITokenManager tokenManager
    )
    {
        await validator.GuardAsync(model);
        try
        {
            string response = string.Empty;
            var tokenResult = await tokenManager.GetAccessTokenAsync(
                [
                    new KeyValuePair<string, string>("client_id", model.ClientId!),
                    new KeyValuePair<string, string>("client_secret", model.ClientSecret!),
                ]
            );

            response = tokenResult switch
            {
                TokenResult.Success tokenSuccess => tokenSuccess.Token,
                TokenResult.FailureKeycloak failure => throw new KeycloakException(failure.KeycloakError),
                _ => response,
            };

            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(response);
            return Results.Ok(tokenResponse);
        }
        catch (KeycloakException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IdentityException(
                "Client registration failed with: Invalid client or Invalid client credentials." + ex.Message
            );
        }
    }
}
