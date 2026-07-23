// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class ClaimSetModule : IEndpointModule
{
    private static IResult DuplicateClaimSetName(HttpContext httpContext)
    {
        return Results.Json(
            FailureResponse.ForNonUniqueIdentity(
                "The identifying value(s) of the item are the same as another item that already exists.",
                httpContext.TraceIdentifier,
                ["A claim set with this name already exists."]
            ),
            contentType: "application/problem+json",
            statusCode: (int)HttpStatusCode.Conflict
        );
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredPost("/v3/claimSets/", InsertClaimSet);
        endpoints
            .MapLimitedAccess("/v3/claimSets/", GetAll)
            .WithQueryParameterValidation<FrontendClaimSetQuery>();
        endpoints.MapSecuredGet($"/v3/claimSets/{{id}}", GetById);
        endpoints.MapSecuredGet($"/v3/claimSets/{{id}}/export", Export);
        endpoints.MapSecuredPut($"/v3/claimSets/{{id}}", Update);
        endpoints.MapSecuredDelete($"/v3/claimSets/{{id}}", Delete);
        endpoints.MapSecuredPost("/v3/claimSets/copy", Copy);
        endpoints.MapSecuredPost("/v3/claimSets/import", Import);
    }

    private static async Task<IResult> InsertClaimSet(
        ClaimSetInsertCommand entity,
        ClaimSetInsertCommand.Validator validator,
        HttpContext httpContext,
        IClaimSetRepository repository
    )
    {
        await validator.GuardAsync(entity);
        var insertResult = await repository.InsertClaimSet(entity);

        var request = httpContext.Request;

        return insertResult switch
        {
            ClaimSetInsertResult.Success success => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                null
            ),
            ClaimSetInsertResult.FailureDuplicateClaimSetName => DuplicateClaimSetName(httpContext),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetAll(
        IClaimSetRepository repository,
        [AsParameters] FrontendClaimSetQuery query,
        ClaimSetPagingQueryValidator validator,
        HttpContext httpContext
    )
    {
        await validator.GuardQueryAsync(query);
        ClaimSetQueryResult result = await repository.QueryClaimSet(query.ToQuery());

        return result switch
        {
            ClaimSetQueryResult.Success success => Results.Ok(success.ClaimSetResponses),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        long id,
        HttpContext httpContext,
        IClaimSetRepository repository,
        ILogger<ClaimSetModule> logger
    )
    {
        logger.LogDebug("Entering ClaimSet GetById for id: {Id}", id);
        ClaimSetGetResult result = await repository.GetClaimSet(id);

        return result switch
        {
            ClaimSetGetResult.Success success => Results.Json(success.ClaimSetResponse),
            ClaimSetGetResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"ClaimSet {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Update(
        long id,
        ClaimSetUpdateCommand command,
        ClaimSetUpdateCommand.Validator validator,
        HttpContext httpContext,
        IClaimSetRepository repository
    )
    {
        await validator.GuardAsync(command);

        if (command.Id != id)
        {
            throw new ValidationException(
                new[] { new ValidationFailure("Id", "Request body id must match the id in the url.") }
            );
        }

        var result = await repository.UpdateClaimSet(command);

        return result switch
        {
            ClaimSetUpdateResult.Success => Results.NoContent(),
            ClaimSetUpdateResult.FailureDuplicateClaimSetName => DuplicateClaimSetName(httpContext),
            ClaimSetUpdateResult.FailureMultiUserConflict => Results.Json(
                FailureResponse.ForConflict(
                    $"Unable to update claim set due to multi-user conflicts. Retry the request.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.Conflict
            ),
            ClaimSetUpdateResult.FailureMultipleHierarchiesFound => Results.Json(
                FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.InternalServerError
            ),
            ClaimSetUpdateResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"ClaimSet {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            ClaimSetUpdateResult.FailureUnknown => Results.Json(
                FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.InternalServerError
            ),
            ClaimSetUpdateResult.FailureSystemReserved => Results.Json(
                FailureResponse.ForBadRequest(
                    "The specified claim set is system-reserved and cannot be updated.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.BadRequest
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Delete(
        long id,
        HttpContext httpContext,
        IClaimSetRepository repository,
        ILogger<ClaimSetModule> logger
    )
    {
        logger.LogDebug("Entering ClaimSet Delete for id: {Id}", id);
        ClaimSetDeleteResult result = await repository.DeleteClaimSet(id);

        return result switch
        {
            ClaimSetDeleteResult.Success => Results.NoContent(),
            ClaimSetDeleteResult.FailureSystemReserved => Results.Json(
                FailureResponse.ForBadRequest(
                    "The specified claim set is system-reserved and cannot be deleted.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.BadRequest
            ),
            ClaimSetDeleteResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"ClaimSet {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            ClaimSetDeleteResult.FailureMultiUserConflict => Results.Json(
                FailureResponse.ForConflict(
                    "Unable to delete claim set due to multi-user conflicts. Retry the request.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.Conflict
            ),
            ClaimSetDeleteResult.FailureMultipleHierarchiesFound => Results.Json(
                FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.InternalServerError
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Export(
        long id,
        HttpContext httpContext,
        IClaimSetRepository repository,
        ILogger<ClaimSetModule> logger
    )
    {
        logger.LogDebug("Entering ClaimSet Export for id: {Id}", id);
        ClaimSetExportResult result = await repository.Export(id);

        return result switch
        {
            ClaimSetExportResult.Success success => Results.Json(success.ClaimSetExportResponse),
            ClaimSetExportResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"ClaimSet {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Copy(
        ClaimSetCopyCommand entity,
        ClaimSetCopyCommand.Validator validator,
        HttpContext httpContext,
        IClaimSetRepository repository
    )
    {
        await validator.GuardAsync(entity);

        var result = await repository.Copy(entity);

        var request = httpContext.Request;

        return result switch
        {
            ClaimSetCopyResult.Success success => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{GetClaimSetsPath(request)}/{success.Id}",
                null
            ),
            ClaimSetCopyResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"OriginalId {entity.OriginalId} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.NotFound
            ),
            ClaimSetCopyResult.FailureDuplicateClaimSetName => DuplicateClaimSetName(httpContext),
            ClaimSetCopyResult.FailureMultiUserConflict => Results.Json(
                FailureResponse.ForConflict(
                    "Unable to copy claim set due to multi-user conflicts. Retry the request.",
                    httpContext.TraceIdentifier
                ),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.Conflict
            ),
            ClaimSetCopyResult.FailureMultipleHierarchiesFound => Results.Json(
                FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.InternalServerError
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Import(
        ClaimSetImportCommand importCommand,
        HttpContext httpContext,
        IClaimSetDataProvider claimSetDataProvider,
        IClaimSetRepository claimSetRepository,
        IClaimsHierarchyRepository claimsHierarchyRepository,
        ILogger<ClaimSetModule> logger
    )
    {
        List<string> actions;
        List<string> authorizationStrategies;

        // Load the supporting authorization metadata (authorization strategies and actions)
        try
        {
            actions = claimSetDataProvider.GetActions();
            authorizationStrategies = await claimSetDataProvider.GetAuthorizationStrategies();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error while loading supporting authorization strategies or actions");

            return Results.Json(
                FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.InternalServerError
            );
        }

        // Load the claims hierarchy
        var hierarchyResult = await claimsHierarchyRepository.GetClaimsHierarchy();

        if (hierarchyResult is not ClaimsHierarchyGetResult.Success hierarchySuccess)
        {
            logger.LogError(
                "Attempt to load claims hierarchy returned {HierarchyResult}. {Message}",
                hierarchyResult.GetType().Name,
                hierarchyResult switch
                {
                    ClaimsHierarchyGetResult.FailureHierarchyNotFound => "No claims hierarchy found",
                    ClaimsHierarchyGetResult.FailureMultipleHierarchiesFound =>
                        "Multiple claims hierarchies found and this is not yet supported",
                    ClaimsHierarchyGetResult.FailureUnknown failureUnknown => failureUnknown.FailureMessage,
                    _ => string.Empty,
                }
            );

            return Results.Json(
                FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                contentType: "application/problem+json",
                statusCode: (int)HttpStatusCode.InternalServerError
            );
        }

        // Extract the tuples of the hierarchy for use in validating the claim set import request
        var resourceClaimsHierarchyTuples = new Dictionary<string, string?>(
            ExtractClaimHierarchyTuples(hierarchySuccess.Claims)
        );

        // Validate the request
        var validator = new ClaimSetImportCommand.Validator();

        var validationContext = new ValidationContext<ClaimSetImportCommand>(importCommand)
        {
            RootContextData =
            {
                ["Actions"] = actions,
                ["AuthorizationStrategies"] = authorizationStrategies,
                ["ResourceClaimsHierarchyTuples"] = resourceClaimsHierarchyTuples,
            },
        };

        await validator.GuardAsync(validationContext);

        // Import the claim set
        var importResult = await claimSetRepository.Import(importCommand);

        var request = httpContext.Request;

        switch (importResult)
        {
            case ClaimSetImportResult.Success success:
                // Combine repository warnings with validator-detected warnings (skipped resources and parent mismatches)
                var validatorWarnings = new List<string>();

                if (
                    validationContext.RootContextData.TryGetValue("SkippedResourceClaims", out var skippedObj)
                    && skippedObj is List<string> skipped
                )
                {
                    validatorWarnings.AddRange(skipped);
                }

                if (
                    validationContext.RootContextData.TryGetValue("ParentWarnings", out var parentObj)
                    && parentObj is List<string> parentWarnings
                )
                {
                    validatorWarnings.AddRange(parentWarnings);
                }

                var combined = (success.Warnings ?? Enumerable.Empty<string>())
                    .Concat(validatorWarnings)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return Results.Created(
                    $"{request.Scheme}://{request.Host}{request.PathBase}{GetClaimSetsPath(request)}/{success.Id}",
                    new { Id = success.Id, Warnings = combined }
                );

            case ClaimSetImportResult.FailureDuplicateClaimSetName:
                return DuplicateClaimSetName(httpContext);

            case ClaimSetImportResult.FailureSystemReserved:
                return Results.Json(
                    FailureResponse.ForBadRequest(
                        "The specified claim set is system-reserved and cannot be imported.",
                        httpContext.TraceIdentifier
                    ),
                    contentType: "application/problem+json",
                    statusCode: (int)HttpStatusCode.BadRequest
                );

            default:
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }

        static IEnumerable<KeyValuePair<string, string?>> ExtractClaimHierarchyTuples(List<Claim> rootClaims)
        {
            foreach (var claim in rootClaims)
            {
                // Yield the current claim with its parent's name (or null if top-level)
                yield return new KeyValuePair<string, string?>(claim.Name, claim.Parent?.Name);

                // Recursively yield children
                foreach (var child in ExtractClaimHierarchyTuples(claim.Claims))
                {
                    yield return child;
                }
            }
        }
    }

    private static string GetClaimSetsPath(HttpRequest request)
    {
        const string ClaimSetsSegment = "/claimSets/";

        if (!request.Path.HasValue)
        {
            return string.Empty;
        }

        int claimSetsPos = request.Path.Value.IndexOf(ClaimSetsSegment, StringComparison.OrdinalIgnoreCase);

        if (claimSetsPos < 0)
        {
            return request.Path.Value.TrimEnd('/');
        }

        return request.Path.Value.Substring(0, claimSetsPos + ClaimSetsSegment.Length - 1);
    }
}
