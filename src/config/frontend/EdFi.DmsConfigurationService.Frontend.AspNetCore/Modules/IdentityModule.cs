// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Model;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using Microsoft.Extensions.Options;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class IdentityModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/connect/register", RegisterClient);
        endpoints.MapPost("/connect/token", GetClientAccessToken);
    }

    public async Task<IResult> RegisterClient(RegisterRequest.Validator validator, RegisterRequest model,
        IClientRepository clientRepository, IOptions<IdentitySettings> identitySettings)
    {
        var allowRegistration = identitySettings.Value.AllowRegistration;
        if (allowRegistration)
        {
            await validator.GuardAsync(model);
            try
            {
                await clientRepository.CreateClientAsync(model.ClientId!, model.ClientSecret!, model.DisplayName!);
                return Results.Ok($"Registered client {model.ClientId} successfully.");
            }
            catch (Exception ex)
            {
                throw new IdentityException($"Client registration failed with: {ex.Message}");
            }
        }
        return Results.Forbid();
    }

    public async Task<IResult> GetClientAccessToken(TokenRequest.Validator validator, TokenRequest model, ITokenManager tokenManager)
    {
        await validator.GuardAsync(model);
        try
        {
            var response = await tokenManager.GetAccessTokenAsync([
                new KeyValuePair<string, string>("client_id", model.ClientId!),
                new KeyValuePair<string, string>("client_secret", model.ClientSecret!)
                ]);
            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(response);
            return Results.Ok(tokenResponse);
        }
        catch (Exception ex)
        {
            throw new IdentityException($"Client token generation failed with: {ex.Message}");
        }
    }
}
