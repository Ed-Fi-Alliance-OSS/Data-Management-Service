// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstanceRouteContext;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class DmsInstanceRouteContextModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredPost("/v2/dmsinstanceroutecontexts/", InsertDmsInstanceRouteContext);
        endpoints.MapSecuredGet("/v2/dmsinstanceroutecontexts/", GetAll);
        endpoints.MapSecuredGet($"/v2/dmsinstanceroutecontexts/{{id}}", GetById);
        endpoints.MapSecuredPut($"/v2/dmsinstanceroutecontexts/{{id}}", Update);
        endpoints.MapSecuredDelete($"/v2/dmsinstanceroutecontexts/{{id}}", Delete);
    }

    private static async Task<IResult> InsertDmsInstanceRouteContext(
        DmsInstanceRouteContextInsertCommand command,
        DmsInstanceRouteContextInsertCommand.Validator validator,
        HttpContext httpContext,
    IDmsInstanceRouteContextRepository dmsInstanceRouteContextRepository,
        ILogger<DmsInstanceRouteContextModule> logger
    )
    {
        logger.LogDebug("Entering InsertDmsInstanceRouteContext");
        await validator.GuardAsync(command);

        var repositoryResult = await dmsInstanceRouteContextRepository.InsertDmsInstanceRouteContext(command);

        return repositoryResult switch
        {
            DmsInstanceRouteContextInsertResult.Success success =>
                Results.Created(
                    $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}{httpContext.Request.Path.Value?.TrimEnd('/')}/{success.Id}",
                    new { Id = success.Id }
                ),
            DmsInstanceRouteContextInsertResult.FailureInstanceNotFound => throw new ValidationException(
                new[] { new ValidationFailure("InstanceId", "Reference 'InstanceId' does not exist.") }
            ),
            DmsInstanceRouteContextInsertResult.FailureDuplicateDmsInstanceRouteContext duplicate => throw new ValidationException(
                new[]
                {
                    new ValidationFailure(
                        "ContextKey",
                        $"Dms instance route context with InstanceId '{duplicate.InstanceId}' and ContextKey '{duplicate.ContextKey}' already exists."
                    )
                }
            ),
            DmsInstanceRouteContextInsertResult.FailureUnknown failure =>
                FailureResults.Unknown(httpContext.TraceIdentifier),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier)
        };
    }

    private static async Task<IResult> GetAll(
    IDmsInstanceRouteContextRepository instanceRouteContextRepository,
        [AsParameters] PagingQuery query,
        HttpContext httpContext
    )
    {
        var getResult = await instanceRouteContextRepository.QueryInstanceRouteContext(query);
        return getResult switch
        {
            DmsInstanceRouteContextQueryResult.Success success => Results.Ok(success.DmsInstanceRouteContextResponses),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        long id,
        HttpContext httpContext,
        IDmsInstanceRouteContextRepository instanceRouteContextRepository,
        ILogger<DmsInstanceRouteContextModule> logger
    )
    {
        var getResult = await instanceRouteContextRepository.GetInstanceRouteContext(id);
        return getResult switch
        {
            DmsInstanceRouteContextGetResult.Success success => Results.Ok(success.DmsInstanceRouteContextResponse),
            DmsInstanceRouteContextGetResult.FailureNotFound => FailureResults.NotFound(
                "Instance route context not found",
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Update(
    long id,
    DmsInstanceRouteContextUpdateCommand.Validator validator,
    DmsInstanceRouteContextUpdateCommand command,
    HttpContext httpContext,
    IDmsInstanceRouteContextRepository dmsInstanceRouteContextRepository,
    ILogger<DmsInstanceRouteContextModule> logger
    )
    {
        await validator.GuardAsync(command);

        // Ensure the ID in the command matches the route parameter
        command.Id = id;

        var updateResult = await dmsInstanceRouteContextRepository.UpdateDmsInstanceRouteContext(command);

        return updateResult switch
        {
            DmsInstanceRouteContextUpdateResult.Success => Results.NoContent(),
            DmsInstanceRouteContextUpdateResult.FailureNotExists => FailureResults.NotFound(
                "Dms instance route context not found",
                httpContext.TraceIdentifier
            ),
            DmsInstanceRouteContextUpdateResult.FailureInstanceNotFound => throw new ValidationException(
                new[] { new ValidationFailure("InstanceId", "Reference 'InstanceId' does not exist.") }
            ),
            DmsInstanceRouteContextUpdateResult.FailureDuplicateDmsInstanceRouteContext duplicate => throw new ValidationException(
                new[]
                {
                    new ValidationFailure(
                        "ContextKey",
                        $"Dms instance route context with InstanceId '{duplicate.InstanceId}' and ContextKey '{duplicate.ContextKey}' already exists."
                    )
                }
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Delete(
        long id,
        HttpContext httpContext,
        IDmsInstanceRouteContextRepository instanceRouteContextRepository,
        ILogger<DmsInstanceRouteContextModule> logger
    )
    {
        logger.LogInformation("Deleting DmsInstanceRouteContext {id}", id);

        var deleteResult = await instanceRouteContextRepository.DeleteInstanceRouteContext(id);

        return deleteResult switch
        {
            InstanceRouteContextDeleteResult.Success => Results.NoContent(),
            InstanceRouteContextDeleteResult.FailureNotExists => FailureResults.NotFound(
                "Instance route context not found",
                httpContext.TraceIdentifier
            ),
            InstanceRouteContextDeleteResult.FailureUnknown unknown =>
                FailureResults.Unknown(httpContext.TraceIdentifier),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
