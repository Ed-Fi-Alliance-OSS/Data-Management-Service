// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Mime;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
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
        ApplicationValidators.ApplicationInsertCommandValidator validator,
        ApplicationInsertCommand command,
        HttpContext httpContext,
        IApplicationRepository applicationRepository,
        IClientRepository clientRepository,
        ILogger<ApplicationModule> logger
    )
    {
        logger.LogDebug("Entering UpsertApplication");
        await validator.GuardAsync(command);

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
            new ApplicationQuery() { Limit = 25, Offset = 0 }
        );
        return getResult switch
        {
            ApplicationQueryResult.Success success => Results.Ok(success.ApplicationResponses),
            ApplicationQueryResult.FailureUnknown failure => Results.Problem(statusCode: 500),
            _ => Results.Problem(statusCode: 500),
        };
    }

    private static async Task<IResult> GetById(
        HttpContext httpContext,
        IApplicationRepository applicationRepository,
        ILogger<ApplicationModule> logger
    )
    {
        Match match = UtilityService.PathExpressionRegex().Match(httpContext.Request.Path);
        if (!match.Success)
        {
            logger.LogInformation("Request path did not match regex");
            return Results.Problem(statusCode: 500);
        }

        string idString = match.Groups["Id"].Value;
        if (!long.TryParse(idString, out long id))
        {
            return Results.NotFound();
        }

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
        ApplicationValidators.ApplicationUpdateCommandValidator validator,
        ApplicationUpdateCommand entity,
        HttpContext httpContext,
        IApplicationRepository repository
    )
    {
        await validator.GuardAsync(entity);
        Match match = UtilityService.PathExpressionRegex().Match(httpContext.Request.Path);

        string idString = match.Groups["Id"].Value;
        if (long.TryParse(idString, out long id))
        {
            var entityType = entity.GetType();
            var idProperty = entityType.GetProperty("Id");
            if (idProperty == null)
            {
                throw new InvalidOperationException("The entity does not contain an Id property.");
            }

            var entityId = idProperty.GetValue(entity) as long?;

            if (entityId != id)
            {
                throw new ValidationException(
                    new[] { new ValidationFailure("Id", "Request body id must match the id in the url.") }
                );
            }

            var updateResult = await repository.UpdateApplication(entity);

            if (updateResult is ApplicationUpdateResult.FailureVendorNotFound failure)
            {
                throw new ValidationException(
                    new[] { new ValidationFailure("VendorId", $"Reference 'VendorId' does not exist.") }
                );
            }

            return updateResult switch
            {
                ApplicationUpdateResult.Success success => Results.NoContent(),
                ApplicationUpdateResult.FailureNotExists => Results.NotFound(),
                ApplicationUpdateResult.FailureUnknown => Results.Problem(statusCode: 500),
                _ => Results.Problem(statusCode: 500),
            };
        }

        return Results.NotFound();
    }

    private static async Task<IResult> Delete(HttpContext httpContext, IApplicationRepository repository)
    {
        Match match = UtilityService.PathExpressionRegex().Match(httpContext.Request.Path);
        if (!match.Success)
        {
            return Results.Problem(statusCode: 500);
        }

        string idString = match.Groups["Id"].Value;

        if (!long.TryParse(idString, out long id))
        {
            return Results.NotFound();
        }

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
