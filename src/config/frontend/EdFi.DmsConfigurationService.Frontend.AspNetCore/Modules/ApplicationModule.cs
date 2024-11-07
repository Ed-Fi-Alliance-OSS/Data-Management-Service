// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Application;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Model.Validator;
using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class ApplicationModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v2/applications/", InsertApplication).RequireAuthorizationWithPolicy();
        endpoints.MapGet("/v2/applications/", GetAll).RequireAuthorizationWithPolicy();
        endpoints.MapGet($"/v2/applications/{{id}}", GetById).RequireAuthorizationWithPolicy();
        endpoints.MapPut($"/v2/applications/{{id}}", Update).RequireAuthorizationWithPolicy();
        endpoints.MapDelete($"/v2/applications/{{id}}", Delete).RequireAuthorizationWithPolicy();
        endpoints
            .MapPut($"/v2/applications/{{id}}/reset-credential", ResetCredential)
            .RequireAuthorizationWithPolicy();
    }

    private async Task<IResult> InsertApplication(
        ApplicationInsertCommandValidator validator,
        ApplicationInsertCommand command,
        HttpContext httpContext,
        IApplicationRepository applicationRepository,
        IClientRepository clientRepository,
        ILogger<ApplicationModule> logger
    )
    {
        logger.LogDebug("Entering UpsertApplication");
        await validator.GuardAsync(command);

        var clientId = Guid.NewGuid().ToString();
        var clientSecret = RandomNumberGenerator.GetString(
            "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ",
            32
        );

        var clientCreateResult = await clientRepository.CreateClientAsync(
            clientId,
            clientSecret,
            command.ApplicationName
        );

        switch (clientCreateResult)
        {
            case ClientCreateResult.FailureUnknown failure:
                logger.LogError("Failure creating client {failure}", failure);
                return Results.Json(
                    FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                    statusCode: (int)HttpStatusCode.InternalServerError
                );
            case ClientCreateResult.FailureIdentityProvider failureIdentityProvider:
                logger.LogError(
                    "Failure creating client: {failureMessage}",
                    failureIdentityProvider.IdentityProviderError.FailureMessage
                );
                return Results.Json(
                    FailureResponse.ForBadGateway(
                        failureIdentityProvider.IdentityProviderError.FailureMessage,
                        httpContext.TraceIdentifier
                    ),
                    statusCode: (int)HttpStatusCode.BadGateway
                );
            case ClientCreateResult.Success clientSuccess:
                var repositoryResult = await applicationRepository.InsertApplication(
                    command,
                    new() { ClientId = clientId, ClientUuid = clientSuccess.ClientUuid }
                );

                switch (repositoryResult)
                {
                    case ApplicationInsertResult.Success success:
                        var request = httpContext.Request;
                        return Results.Created(
                            $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                            new ApplicationCredentialsResponse()
                            {
                                Id = success.Id,
                                Key = clientId,
                                Secret = clientSecret,
                            }
                        );
                    case ApplicationInsertResult.FailureVendorNotFound:
                        await clientRepository.DeleteClientAsync(clientSuccess.ClientUuid.ToString());
                        throw new ValidationException(
                            new[]
                            {
                                new ValidationFailure("VendorId", $"Reference 'VendorId' does not exist."),
                            }
                        );
                    case ApplicationInsertResult.FailureUnknown failure:
                        logger.LogError("Failure creating client {failure}", failure);
                        await clientRepository.DeleteClientAsync(clientSuccess.ClientUuid.ToString());
                        return Results.Json(
                            FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                            statusCode: (int)HttpStatusCode.InternalServerError
                        );
                }

                break;
        }

        logger.LogError("Failure creating client");
        return Results.Json(
            FailureResponse.ForUnknown(httpContext.TraceIdentifier),
            statusCode: (int)HttpStatusCode.InternalServerError
        );
    }

    private static async Task<IResult> GetAll(
        IApplicationRepository applicationRepository,
        HttpContext httpContext
    )
    {
        ApplicationQueryResult getResult = await applicationRepository.QueryApplication(
            new PagingQuery() { Limit = 9999, Offset = 0 }
        );
        return getResult switch
        {
            ApplicationQueryResult.Success success => Results.Ok(success.ApplicationResponses),
            _ => Results.Json(
                FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                statusCode: (int)HttpStatusCode.InternalServerError
            ),
        };
    }

    private static async Task<IResult> GetById(
        long id,
        HttpContext httpContext,
        IApplicationRepository applicationRepository,
        ILogger<ApplicationModule> logger
    )
    {
        ApplicationGetResult getResult = await applicationRepository.GetApplication(id);
        return getResult switch
        {
            ApplicationGetResult.Success success => Results.Ok(success.ApplicationResponse),
            ApplicationGetResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound("Application not found", httpContext.TraceIdentifier),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => Results.Json(
                FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                statusCode: (int)HttpStatusCode.InternalServerError
            ),
        };
    }

    private static async Task<IResult> Update(
        long id,
        ApplicationUpdateCommandValidator validator,
        ApplicationUpdateCommand command,
        HttpContext httpContext,
        IApplicationRepository repository
    )
    {
        await validator.GuardAsync(command);

        var applicationUpdateResult = await repository.UpdateApplication(command);

        if (applicationUpdateResult is ApplicationUpdateResult.FailureVendorNotFound)
        {
            throw new ValidationException(
                new[] { new ValidationFailure("VendorId", $"Reference 'VendorId' does not exist.") }
            );
        }

        return applicationUpdateResult switch
        {
            ApplicationUpdateResult.Success success => Results.NoContent(),
            ApplicationUpdateResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound("Application not found", httpContext.TraceIdentifier),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => Results.Json(
                FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                statusCode: (int)HttpStatusCode.InternalServerError
            ),
        };
    }

    private static async Task<IResult> Delete(
        long id,
        HttpContext httpContext,
        IApplicationRepository repository,
        IClientRepository clientRepository,
        ILogger<ApplicationModule> logger
    )
    {
        logger.LogInformation("Deleting Application {id}", id);

        var apiClientsResult = await repository.GetApplicationApiClients(id);
        switch (apiClientsResult)
        {
            case ApplicationApiClientsResult.Success success:
                foreach (var client in success.Clients)
                {
                    try
                    {
                        logger.LogInformation("Deleting client {clientId}", client.ClientId);
                        var clientDeleteResult = await clientRepository.DeleteClientAsync(
                            client.ClientUuid.ToString()
                        );
                        if (clientDeleteResult is ClientDeleteResult.FailureUnknown failureUnknown)
                        {
                            logger.LogError(
                                "Error deleting client {clientId} {clientUuid}: {failureMessage}",
                                client.ClientId,
                                client.ClientUuid,
                                failureUnknown.FailureMessage
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            "Error deleting client {clientId} {clientUuid}: {message}",
                            client.ClientId,
                            client.ClientUuid,
                            ex.Message
                        );
                        return Results.Json(
                            FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                            statusCode: (int)HttpStatusCode.InternalServerError
                        );
                    }
                }

                break;
            case ApplicationApiClientsResult.FailureUnknown failure:
                logger.LogError("Error fetching ApiClients: {failure}", failure);
                return Results.Json(
                    FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                    statusCode: (int)HttpStatusCode.InternalServerError
                );
        }

        ApplicationDeleteResult deleteResult = await repository.DeleteApplication(id);

        if (deleteResult is ApplicationDeleteResult.FailureUnknown unknown)
        {
            logger.LogError("Error deleting Application {id}: {message}", id, unknown.FailureMessage);
            return Results.Problem(statusCode: 500);
        }
        return deleteResult switch
        {
            ApplicationDeleteResult.Success => Results.NoContent(),
            ApplicationDeleteResult.FailureNotExists => Results.NotFound(),
            _ => Results.Json(
                FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                statusCode: (int)HttpStatusCode.InternalServerError
            ),
        };
    }

    private static async Task<IResult> ResetCredential(
        long id,
        HttpContext httpContext,
        IApplicationRepository repository,
        IClientRepository clientRepository,
        ILogger<ApplicationModule> logger
    )
    {
        var apiClientsResult = await repository.GetApplicationApiClients(id);
        switch (apiClientsResult)
        {
            case ApplicationApiClientsResult.Success success:
                var client = success.Clients.FirstOrDefault();
                if (client != null)
                {
                    try
                    {
                        logger.LogInformation("Resetting client {clientId}", client.ClientId);
                        var clientResetResult = await clientRepository.ResetCredentialsAsync(
                            client.ClientUuid.ToString()
                        );
                        switch (clientResetResult)
                        {
                            case ClientResetResult.Success resetSuccess:
                                return Results.Ok(
                                    new ApplicationCredentialsResponse()
                                    {
                                        Id = id,
                                        Key = client.ClientId,
                                        Secret = resetSuccess.ClientSecret,
                                    }
                                );
                            case ClientResetResult.FailureUnknown failure:
                                logger.LogError(
                                    "Error resetting client credentials {clientId} {clientUuid}: {message}",
                                    client.ClientId,
                                    client.ClientUuid,
                                    failure.FailureMessage
                                );
                                return Results.Problem(statusCode: 500);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            "Error resetting client credentials {clientId} {clientUuid}: {message}",
                            client.ClientId,
                            client.ClientUuid,
                            ex.Message
                        );
                        return Results.Json(
                            FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                            statusCode: (int)HttpStatusCode.InternalServerError
                        );
                    }
                }
                else
                {
                    return Results.NotFound();
                }
                break;
            case ApplicationApiClientsResult.FailureUnknown failure:
                logger.LogError("Error fetching ApiClients: {failure}", failure);
                return Results.Json(
                    FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                    statusCode: (int)HttpStatusCode.InternalServerError
                );
        }
        return Results.Json(
            FailureResponse.ForUnknown(httpContext.TraceIdentifier),
            statusCode: (int)HttpStatusCode.InternalServerError
        );
    }
}
