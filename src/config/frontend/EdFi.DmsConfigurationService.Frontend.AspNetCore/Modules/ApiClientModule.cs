// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class ApiClientModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredPost("/v2/apiClients/", InsertApiClient);
        endpoints.MapSecuredPut($"/v2/apiClients/{{id}}", UpdateApiClient);
        // Limited access endpoints - accessible by service accounts for internal DMS operations
        endpoints.MapLimitedAccess("/v2/apiClients/", GetAll);
        endpoints.MapLimitedAccess("/v2/apiClients/{clientId}", GetByClientId);
    }

    private async Task<IResult> InsertApiClient(
        ApiClientInsertCommand command,
        ApiClientInsertCommand.Validator validator,
        HttpContext httpContext,
        IApiClientRepository apiClientRepository,
        IApplicationRepository applicationRepository,
        IVendorRepository vendorRepository,
        IDmsInstanceRepository dmsInstanceRepository,
        IIdentityProviderRepository clientRepository,
        IOptions<IdentitySettings> identitySettings,
        ILogger<ApiClientModule> logger
    )
    {
        logger.LogDebug("Entering InsertApiClient");
        await validator.GuardAsync(command);

        // Validate Application exists and get application details
        ApplicationGetResult applicationResult = await applicationRepository.GetApplication(
            command.ApplicationId
        );
        if (applicationResult is not ApplicationGetResult.Success applicationSuccess)
        {
            throw new ValidationException(
                [
                    new ValidationFailure(
                        "ApplicationId",
                        $"Application with ID {command.ApplicationId} not found."
                    ),
                ]
            );
        }

        ApplicationResponse application = applicationSuccess.ApplicationResponse;

        // Validate DmsInstanceIds exist (optimized single query)
        if (command.DmsInstanceIds.Length > 0)
        {
            var existingIdsResult = await dmsInstanceRepository.GetExistingDmsInstanceIds(
                command.DmsInstanceIds
            );
            if (existingIdsResult is DmsInstanceIdsExistResult.Success existingSuccess)
            {
                var notFoundIds = command
                    .DmsInstanceIds.Where(id => !existingSuccess.ExistingIds.Contains(id))
                    .ToList();

                if (notFoundIds.Count > 0)
                {
                    throw new ValidationException(
                        [
                            new ValidationFailure(
                                "DmsInstanceIds",
                                $"The following DmsInstanceIds were not found in database: {string.Join(", ", notFoundIds)}"
                            ),
                        ]
                    );
                }
            }
            else if (existingIdsResult is DmsInstanceIdsExistResult.FailureUnknown failure)
            {
                logger.LogError("Error validating DmsInstanceIds: {message}", failure.FailureMessage);
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            }
        }

        // Get vendor details for namespace prefixes
        string namespacePrefixes;
        switch (await vendorRepository.GetVendor(application.VendorId))
        {
            case VendorGetResult.Success success:
                namespacePrefixes = success.VendorResponse.NamespacePrefixes;
                break;
            default:
                throw new ValidationException(
                    [new ValidationFailure("VendorId", "Reference 'VendorId' does not exist.")]
                );
        }

        var clientId = Guid.NewGuid().ToString();
        var clientSecret = RandomNumberGenerator.GetString(
            "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ",
            32
        );

        // Create client in identity provider
        var clientCreateResult = await clientRepository.CreateClientAsync(
            clientId,
            clientSecret,
            identitySettings.Value.ClientRole,
            application.ApplicationName,
            application.ClaimSetName,
            namespacePrefixes,
            string.Join(",", application.EducationOrganizationIds),
            command.DmsInstanceIds
        );

        switch (clientCreateResult)
        {
            case ClientCreateResult.FailureUnknown failure:
                logger.LogError("Failure creating client {failure}", failure);
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            case ClientCreateResult.FailureIdentityProvider failureIdentityProvider:
                logger.LogError(
                    "Failure creating client: {failureMessage}",
                    failureIdentityProvider.IdentityProviderError.FailureMessage
                );
                return FailureResults.BadGateway(
                    failureIdentityProvider.IdentityProviderError.FailureMessage,
                    httpContext.TraceIdentifier
                );
            case ClientCreateResult.Success clientSuccess:
                var repositoryResult = await apiClientRepository.InsertApiClient(
                    command,
                    new ApiClientCommand
                    {
                        ClientId = clientId,
                        ClientUuid = clientSuccess.ClientUuid,
                        DmsInstanceIds = command.DmsInstanceIds,
                    }
                );

                switch (repositoryResult)
                {
                    case ApiClientInsertResult.Success success:
                        var request = httpContext.Request;
                        return Results.Created(
                            $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                            new ApiClientCredentialsResponse
                            {
                                Id = success.Id,
                                Key = clientId,
                                Secret = clientSecret,
                            }
                        );
                    case ApiClientInsertResult.FailureApplicationNotFound:
                        await clientRepository.DeleteClientAsync(clientSuccess.ClientUuid.ToString());
                        throw new ValidationException(
                            [
                                new ValidationFailure(
                                    "ApplicationId",
                                    $"Application with ID {command.ApplicationId} not found."
                                ),
                            ]
                        );
                    case ApiClientInsertResult.FailureDmsInstanceNotFound:
                        await clientRepository.DeleteClientAsync(clientSuccess.ClientUuid.ToString());
                        throw new ValidationException(
                            [new ValidationFailure("DmsInstanceId", "DMS instance does not exist.")]
                        );
                    case ApiClientInsertResult.FailureUnknown failure:
                        logger.LogError("Failure creating client {failure}", failure);
                        await clientRepository.DeleteClientAsync(clientSuccess.ClientUuid.ToString());
                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                }

                break;
        }

        logger.LogError("Failure creating client");
        return FailureResults.Unknown(httpContext.TraceIdentifier);
    }

    private static async Task<IResult> GetAll(
        IApiClientRepository apiClientRepository,
        [AsParameters] PagingQuery query,
        HttpContext httpContext
    )
    {
        ApiClientQueryResult getResult = await apiClientRepository.QueryApiClient(query);
        return getResult switch
        {
            ApiClientQueryResult.Success success => Results.Ok(success.ApiClientResponses),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetByClientId(
        string clientId,
        HttpContext httpContext,
        IApiClientRepository apiClientRepository
    )
    {
        ApiClientGetResult getResult = await apiClientRepository.GetApiClientByClientId(clientId);
        return getResult switch
        {
            ApiClientGetResult.Success success => Results.Ok(success.ApiClientResponse),
            ApiClientGetResult.FailureNotFound => FailureResults.NotFound(
                "ApiClient not found",
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    /// <summary>
    /// Sanitizes a string for safe logging by allowing only safe characters.
    /// Uses a whitelist approach to prevent log injection and log forging attacks.
    /// Allows: letters, digits, spaces, and safe punctuation (_-.:/)
    /// </summary>
    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }
        // Whitelist approach: only allow alphanumeric characters and specific safe symbols
        return new string(
            input
                .Where(c =>
                    char.IsLetterOrDigit(c)
                    || c == ' '
                    || c == '_'
                    || c == '-'
                    || c == '.'
                    || c == ':'
                    || c == '/'
                )
                .ToArray()
        );
    }

    private async Task<IResult> UpdateApiClient(
        long id,
        ApiClientUpdateCommand command,
        ApiClientUpdateCommand.Validator validator,
        HttpContext httpContext,
        IApiClientRepository apiClientRepository,
        IApplicationRepository applicationRepository,
        IVendorRepository vendorRepository,
        IDmsInstanceRepository dmsInstanceRepository,
        IIdentityProviderRepository identityProviderRepository,
        ILogger<ApiClientModule> logger
    )
    {
        logger.LogDebug("Entering UpdateApiClient for id: {Id}", SanitizeForLog(id.ToString()));

        // Set the ID from the route parameter
        command.Id = id;
        await validator.GuardAsync(command);

        // Get existing API client
        ApiClientGetResult existingResult = await apiClientRepository.GetApiClientById(id);
        if (existingResult is not ApiClientGetResult.Success existingSuccess)
        {
            return FailureResults.NotFound("ApiClient not found", httpContext.TraceIdentifier);
        }

        ApiClientResponse existingApiClient = existingSuccess.ApiClientResponse;

        // Validate Application exists and get application details
        ApplicationGetResult applicationResult = await applicationRepository.GetApplication(
            command.ApplicationId
        );
        if (applicationResult is not ApplicationGetResult.Success applicationSuccess)
        {
            throw new ValidationException(
                [
                    new ValidationFailure(
                        "ApplicationId",
                        $"Application with ID {command.ApplicationId} not found."
                    ),
                ]
            );
        }

        ApplicationResponse application = applicationSuccess.ApplicationResponse;

        // Validate DmsInstanceIds exist (optimized single query)
        if (command.DmsInstanceIds.Length > 0)
        {
            var existingIdsResult = await dmsInstanceRepository.GetExistingDmsInstanceIds(
                command.DmsInstanceIds
            );
            if (existingIdsResult is DmsInstanceIdsExistResult.Success existingIdsSuccess)
            {
                var notFoundIds = command
                    .DmsInstanceIds.Where(id => !existingIdsSuccess.ExistingIds.Contains(id))
                    .ToList();

                if (notFoundIds.Count > 0)
                {
                    throw new ValidationException(
                        [
                            new ValidationFailure(
                                "DmsInstanceIds",
                                $"The following DmsInstanceIds were not found in database: {string.Join(", ", notFoundIds)}"
                            ),
                        ]
                    );
                }
            }
            else if (existingIdsResult is DmsInstanceIdsExistResult.FailureUnknown failure)
            {
                logger.LogError(
                    "Error validating DmsInstanceIds: {message}",
                    SanitizeForLog(failure.FailureMessage)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            }
        }

        // Update client in identity provider
        var clientUpdateResult = await identityProviderRepository.UpdateClientAsync(
            existingApiClient.ClientUuid.ToString(),
            command.Name,
            application.ClaimSetName,
            string.Join(",", application.EducationOrganizationIds),
            command.DmsInstanceIds
        );

        switch (clientUpdateResult)
        {
            case ClientUpdateResult.FailureUnknown failure:
                logger.LogError("Failure updating client: {failure}", SanitizeForLog(failure.FailureMessage));
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            case ClientUpdateResult.FailureIdentityProvider failureIdentityProvider:
                logger.LogError(
                    "Failure updating client: {failureMessage}",
                    SanitizeForLog(failureIdentityProvider.IdentityProviderError.FailureMessage)
                );
                return FailureResults.BadGateway(
                    failureIdentityProvider.IdentityProviderError.FailureMessage,
                    httpContext.TraceIdentifier
                );
            case ClientUpdateResult.FailureNotFound notFound:
                logger.LogError(
                    "Client not found in identity provider: {failure}",
                    SanitizeForLog(notFound.FailureMessage)
                );
                return FailureResults.NotFound(
                    "ApiClient not found in identity provider",
                    httpContext.TraceIdentifier
                );
            case ClientUpdateResult.Success:
                var repositoryResult = await apiClientRepository.UpdateApiClient(command);

                switch (repositoryResult)
                {
                    case ApiClientUpdateResult.Success:
                        return Results.NoContent();
                    case ApiClientUpdateResult.FailureNotFound:
                        throw new ValidationException(
                            [new ValidationFailure("Id", $"ApiClient with ID {id} not found.")]
                        );
                    case ApiClientUpdateResult.FailureApplicationNotFound:
                        throw new ValidationException(
                            [
                                new ValidationFailure(
                                    "ApplicationId",
                                    $"Application with ID {command.ApplicationId} not found."
                                ),
                            ]
                        );
                    case ApiClientUpdateResult.FailureDmsInstanceNotFound:
                        throw new ValidationException(
                            [new ValidationFailure("DmsInstanceId", "DMS instance does not exist.")]
                        );
                    case ApiClientUpdateResult.FailureUnknown failure:
                        logger.LogError(
                            "Failure updating client: {failure}",
                            SanitizeForLog(failure.FailureMessage)
                        );
                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                }

                break;
        }

        logger.LogError("Failure updating client");
        return FailureResults.Unknown(httpContext.TraceIdentifier);
    }
}
