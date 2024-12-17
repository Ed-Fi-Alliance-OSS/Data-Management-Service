// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.Register;
using EdFi.DmsConfigurationService.DataModel.Model.Token;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using static EdFi.DmsConfigurationService.Backend.IdentityProviderError;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class IdentityModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/connect/register", RegisterClient).DisableAntiforgery();
        endpoints.MapPost("/connect/token", GetClientAccessToken).DisableAntiforgery();
    }

    private async Task<IResult> RegisterClient(
        RegisterRequest.Validator validator,
        [FromForm] RegisterRequest model,
        IClientRepository clientRepository,
        IOptions<IdentitySettings> identitySettings,
        HttpContext httpContext
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
                    return FailureResults.Unknown(httpContext.TraceIdentifier);
                case ClientClientsResult.FailureIdentityProvider failureIdentityProvider:
                    return FailureResults.BadGateway(
                        failureIdentityProvider.IdentityProviderError.FailureMessage,
                        httpContext.TraceIdentifier
                    );
                case ClientClientsResult.Success clientSuccess:
                    if (IsUnique(clientSuccess))
                    {
                        var result = await clientRepository.CreateClientAsync(
                            model.ClientId!,
                            model.ClientSecret!,
                            identitySettings.Value.ConfigServiceRole,
                            model.DisplayName!
                        );
                        return result switch
                        {
                            ClientCreateResult.Success => Results.Json(
                                new
                                {
                                    Title = $"Registered client {model.ClientId} successfully.",
                                    Status = 200,
                                }
                            ),
                            ClientCreateResult.FailureIdentityProvider failureIdentityProvider =>
                                FailureResults.BadGateway(
                                    failureIdentityProvider.IdentityProviderError.FailureMessage,
                                    httpContext.TraceIdentifier
                                ),
                            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
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
        [FromForm] TokenRequest model,
        ITokenManager tokenManager,
        HttpContext httpContext
    )
    {
        await validator.GuardAsync(model);

        var tokenResult = await tokenManager.GetAccessTokenAsync(
            [
                new KeyValuePair<string, string>("client_id", model.client_id),
                new KeyValuePair<string, string>("client_secret", model.client_secret),
                new KeyValuePair<string, string>("grant_type", model.grant_type),
                new KeyValuePair<string, string>("scope", model.scope),
            ]
        );

        return tokenResult switch
        {
            TokenResult.Success tokenSuccess => Results.Ok(
                JsonSerializer.Deserialize<TokenResponse>(tokenSuccess.Token)
            ),
            TokenResult.FailureIdentityProvider failureIdentityProvider =>
                failureIdentityProvider.IdentityProviderError switch
                {
                    Unauthorized unauthorized => FailureResults.Unauthorized(
                        unauthorized.FailureMessage,
                        httpContext.TraceIdentifier
                    ),
                    Forbidden forbidden => FailureResults.Forbidden(
                        forbidden.FailureMessage,
                        httpContext.TraceIdentifier
                    ),
                    _ => FailureResults.BadGateway(
                        failureIdentityProvider.IdentityProviderError.FailureMessage,
                        httpContext.TraceIdentifier
                    ),
                },
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
