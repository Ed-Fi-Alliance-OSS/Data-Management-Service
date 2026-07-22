// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.Profile;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class ProfileModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredGet("/v3/profiles/", GetAll);
        endpoints.MapSecuredPost("/v3/profiles/", InsertProfile);
        endpoints.MapSecuredGet($"/v3/profiles/{{id}}", GetById);
        endpoints.MapSecuredPut($"/v3/profiles/{{id}}", Update);
        endpoints.MapSecuredDelete($"/v3/profiles/{{id}}", Delete);
    }

    private static bool IsProfileValid(
        ProfileResponse profile,
        ILogger<ProfileModule> logger,
        string contextMessage = ""
    )
    {
        var validationResult = ProfileValidationUtils.ValidateProfileXml(profile.Definition);
        if (!validationResult.IsValid)
        {
            logger.LogWarning(
                "Profile definition failed XSD validation. Context: {ContextMessage}, ProfileId: {ProfileId}, Name: {Name}, ValidationErrors: {ValidationErrors}",
                contextMessage,
                profile.Id,
                LoggingUtility.SanitizeForLog(profile.Name),
                LoggingUtility.SanitizeForLog(string.Join("; ", validationResult.Errors))
            );
            return false;
        }
        return true;
    }

    private static async Task<IResult> GetAll(
        IProfileRepository repository,
        [AsParameters] FrontendProfileQuery query,
        ProfilePagingQueryValidator validator,
        HttpContext httpContext
    )
    {
        await validator.GuardAsync(query);
        var results = await repository.QueryProfiles(query.ToQuery());
        var profiles = results
            .OfType<ProfileGetResult.Success>()
            .Select(r => new ProfileListResponse { Id = r.Profile.Id, Name = r.Profile.Name })
            .ToList();
        if (profiles.Count > 0)
        {
            return Results.Ok(profiles);
        }
        if (results.Any(r => r is ProfileGetResult.FailureUnknown))
        {
            return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
        return Results.Ok(Array.Empty<ProfileListResponse>());
    }

    private static async Task<IResult> InsertProfile(
        ProfileInsertCommand command,
        ProfileInsertCommand.Validator validator,
        HttpContext httpContext,
        IProfileRepository repository,
        ILogger<ProfileModule> logger
    )
    {
        logger.LogDebug("Entering Profile InsertProfile");
        await validator.GuardAsync(command);
        var result = await repository.InsertProfile(command);
        var request = httpContext.Request;
        return result switch
        {
            ProfileInsertResult.Success success => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                null
            ),
            ProfileInsertResult.FailureDuplicateName duplicate => FailureResults.NonUniqueIdentity(
                "The identifying value(s) of the item are the same as another item that already exists.",
                httpContext.TraceIdentifier,
                [$"Profile '{duplicate.Name}' already exists."]
            ),
            ProfileInsertResult.FailureUnknown _ => FailureResults.Unknown(httpContext.TraceIdentifier),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        long id,
        HttpContext httpContext,
        IProfileRepository repository,
        ILogger<ProfileModule> logger
    )
    {
        var result = await repository.GetProfile(id);

        return result switch
        {
            ProfileGetResult.Success success => IsProfileValid(success.Profile, logger)
                ? Results.Ok(success.Profile)
                : Results.Json(
                    FailureResponse.ForNotFound($"Profile {id} not found.", httpContext.TraceIdentifier),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.NotFound
                ),
            ProfileGetResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound($"Profile {id} not found.", httpContext.TraceIdentifier),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            ProfileGetResult.FailureUnknown => FailureResults.Unknown(httpContext.TraceIdentifier),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Update(
        long id,
        ProfileUpdateCommand command,
        ProfileUpdateCommand.Validator validator,
        HttpContext httpContext,
        IProfileRepository repository,
        ILogger<ProfileModule> logger
    )
    {
        logger.LogDebug("Entering Profile Update for id: {Id}", id);
        await validator.GuardAsync(command);
        if (command.Id != id)
        {
            throw new ValidationException([
                new ValidationFailure("Id", "Request body id must match the id in the url."),
            ]);
        }
        var result = await repository.UpdateProfile(command);
        return result switch
        {
            ProfileUpdateResult.Success => Results.NoContent(),
            ProfileUpdateResult.FailureDuplicateName => FailureResults.NonUniqueIdentity(
                "The identifying value(s) of the item are the same as another item that already exists.",
                httpContext.TraceIdentifier,
                ["A profile with this name already exists."]
            ),
            ProfileUpdateResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound($"Profile {id} not found.", httpContext.TraceIdentifier),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            ProfileUpdateResult.FailureUnknown => FailureResults.Unknown(httpContext.TraceIdentifier),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Delete(
        long id,
        HttpContext httpContext,
        IProfileRepository repository,
        ILogger<ProfileModule> logger
    )
    {
        logger.LogDebug("Entering Profile Delete for id: {Id}", id);
        var result = await repository.DeleteProfile(id);
        return result switch
        {
            ProfileDeleteResult.Success => Results.NoContent(),
            ProfileDeleteResult.FailureInUse => FailureResults.DependentItemExists(
                "The requested action cannot be performed because this item is referenced by existing item(s).",
                httpContext.TraceIdentifier,
                ["Profile is assigned to applications and cannot be deleted."]
            ),
            ProfileDeleteResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound($"Profile {id} not found.", httpContext.TraceIdentifier),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            ProfileDeleteResult.FailureUnknown => FailureResults.Unknown(httpContext.TraceIdentifier),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
