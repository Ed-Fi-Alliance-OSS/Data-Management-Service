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
using FluentValidation;
using FluentValidation.Results;
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

            var clientResult = await clientRepository.GetAllClientsAsync();
            switch (clientResult)
            {
                case ClientClientsResult.FailureUnknown:
                    return Results.Problem(statusCode: 500);
                case ClientClientsResult.FailureIdentityProvider failureIdentityProvider:
                    throw new IdentityProviderException(failureIdentityProvider.IdentityProviderError);
                case ClientClientsResult.Success clientSuccess:
                    if (IsUnique(clientSuccess))
                    {
                        var result = await clientRepository.CreateClientAsync(
                            model.ClientId!,
                            model.ClientSecret!,
                            model.DisplayName!
                        );
                        return result switch
                        {
                            ClientCreateResult.Success => Results.Ok(
                                $"Registered client {model.ClientId} successfully."
                            ),
                            ClientCreateResult.FailureIdentityProvider ke =>
                                throw new IdentityProviderException(ke.IdentityProviderError),
                            _ => Results.Problem(statusCode: 500),
                        };
                    }
                    break;
            }
            bool IsUnique(ClientClientsResult.Success clientSuccess)
            {
                bool clientExists = clientSuccess.ClientList.Any(c =>
                    c.Equals(model.ClientId!, StringComparison.InvariantCultureIgnoreCase)
                );
                if (clientExists)
                {
                    var validationFailures = new List<ValidationFailure>
                    {
                        new()
                        {
                            PropertyName = "ClientId",
                            ErrorMessage =
                                "Client with the same Client Id already exists. Please provide different Client Id.",
                        },
                    };
                    throw new ValidationException(validationFailures);
                }
                return true;
            }
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
                TokenResult.FailureIdentityProvider failure => throw new IdentityProviderException(
                    failure.IdentityProviderError
                ),
                _ => response,
            };

            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(response);
            return Results.Ok(tokenResponse);
        }
        catch (IdentityProviderException)
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
