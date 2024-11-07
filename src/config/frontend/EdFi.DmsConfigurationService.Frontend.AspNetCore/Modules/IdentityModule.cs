// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
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
                    return Results.Json(
                        FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                        statusCode: (int)HttpStatusCode.InternalServerError
                    );
                case ClientClientsResult.FailureIdentityProvider failureIdentityProvider:
                    return Results.Json(
                        FailureResponse.ForBadGateway(
                            failureIdentityProvider.IdentityProviderError.FailureMessage,
                            httpContext.TraceIdentifier
                        ),
                        statusCode: (int)HttpStatusCode.BadGateway
                    );
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
                            ClientCreateResult.FailureIdentityProvider failureIdentityProvider =>
                                Results.Json(
                                    FailureResponse.ForBadGateway(
                                        failureIdentityProvider.IdentityProviderError.FailureMessage,
                                        httpContext.TraceIdentifier
                                    ),
                                    statusCode: (int)HttpStatusCode.BadGateway
                                ),
                            _ => Results.Json(
                                FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                                statusCode: (int)HttpStatusCode.InternalServerError
                            ),
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
        ITokenManager tokenManager,
        HttpContext httpContext
    )
    {
        await validator.GuardAsync(model);

        string response = string.Empty;
        var tokenResult = await tokenManager.GetAccessTokenAsync(
            [
                new KeyValuePair<string, string>("client_id", model.ClientId!),
                new KeyValuePair<string, string>("client_secret", model.ClientSecret!),
            ]
        );

        if (tokenResult is TokenResult.Success tokenSuccess)
        {
            response = tokenSuccess.Token;
            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(response);
            return Results.Ok(tokenResponse);
        }

        if (tokenResult is TokenResult.FailureIdentityProvider failureIdentityProvider)
        {
            return failureIdentityProvider.IdentityProviderError switch
            {
                IdentityProviderError.Unauthorized => Results.Unauthorized(),
                IdentityProviderError.Forbidden => Results.Forbid(),
                _ => Results.Json(
                    FailureResponse.ForBadGateway(
                        failureIdentityProvider.IdentityProviderError.FailureMessage,
                        httpContext.TraceIdentifier
                    ),
                    statusCode: (int)HttpStatusCode.BadGateway
                ),
            };
        }

        return Results.Json(
            FailureResponse.ForUnknown(httpContext.TraceIdentifier),
            statusCode: (int)HttpStatusCode.InternalServerError
        );
    }
}
