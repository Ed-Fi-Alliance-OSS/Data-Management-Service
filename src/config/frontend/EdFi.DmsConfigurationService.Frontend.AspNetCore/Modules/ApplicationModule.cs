// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
    }

    private static async Task<IResult> InsertApplication(
        ApplicationInsertCommandValidator validator,
        ApplicationInsertCommand command,
        HttpContext httpContext,
        IApplicationRepository applicationRepository,
        IClientRepository clientRepository,
        ILogger<ApplicationModule> logger
    )
    {
        logger.LogDebug("Entering UpsertApplication");
        validator.GuardAsync(command);

        Guid clientId = Guid.NewGuid();
        string clientSecret = Guid.NewGuid().ToString();
        if (
            await clientRepository.CreateClientAsync(
                clientId.ToString(),
                clientSecret,
                command.ApplicationName
            )
        )
        {
            var repositoryResult = await applicationRepository.InsertApplication(
                command,
                clientId,
                clientSecret
            );

            var request = httpContext.Request;

            if (repositoryResult is ApplicationInsertResult.FailureVendorNotFound failure)
            {
                throw new ValidationException(
                    new[] { new ValidationFailure("VendorId", $"Reference 'VendorId' does not exist.") }
                );
            }
            return repositoryResult switch
            {
                ApplicationInsertResult.Success success => Results.Created(
                    $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                    new ApplicationInsertResponse()
                    {
                        Id = success.Id,
                        Key = clientId.ToString(),
                        Secret = clientSecret,
                    }
                ),
                ApplicationInsertResult.FailureUnknown => Results.Problem(statusCode: 500),
                _ => Results.Problem(statusCode: 500),
            };
        }

        return Results.Problem(statusCode: 500);
    }

    private static async Task<IResult> GetAll(IApplicationRepository applicationRepository)
    {
        ApplicationQueryResult getResult = await applicationRepository.QueryApplication(
            new PagingQuery() { Limit = 25, Offset = 0 }
        );
        return getResult switch
        {
            ApplicationQueryResult.Success success => Results.Ok(success.ApplicationResponses),
            ApplicationQueryResult.FailureUnknown failure => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500),
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
            ApplicationGetResult.FailureNotFound => Results.NotFound(),
            ApplicationGetResult.FailureUnknown => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500),
        };
    }

    private static async Task<IResult> Update(
        long id,
        ApplicationUpdateCommandValidator validator,
        ApplicationUpdateCommand entity,
        HttpContext httpContext,
        IApplicationRepository repository
    )
    {
        validator.GuardAsync(entity);

        var vendorUpdateResult = await repository.UpdateApplication(entity);

        if (vendorUpdateResult is ApplicationVendorUpdateResult.FailureVendorNotFound failure)
        {
            throw new ValidationException(
                new[] { new ValidationFailure("VendorId", $"Reference 'VendorId' does not exist.") }
            );
        }

        return vendorUpdateResult switch
        {
            ApplicationVendorUpdateResult.Success success => Results.NoContent(),
            ApplicationVendorUpdateResult.FailureNotExists => Results.NotFound(),
            ApplicationVendorUpdateResult.FailureUnknown => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500),
        };
    }

    private static async Task<IResult> Delete(
        long id,
        HttpContext httpContext,
        IApplicationRepository repository
    )
    {
        ApplicationDeleteResult deleteResult = await repository.DeleteApplication(id);
        return deleteResult switch
        {
            ApplicationDeleteResult.Success => Results.NoContent(),
            ApplicationDeleteResult.FailureNotExists => Results.NotFound(),
            ApplicationDeleteResult.FailureUnknown => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500),
        };
    }
}
