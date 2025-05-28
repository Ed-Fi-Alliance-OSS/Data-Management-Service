// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class ClaimSetModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredPost("/v2/claimSets/", InsertClaimSet);
        endpoints.MapLimitedAccess("/v2/claimSets/", GetAll);
        endpoints.MapSecuredGet($"/v2/claimSets/{{id}}", GetById);
        endpoints.MapSecuredGet($"/v2/claimSets/{{id}}/export", Export);
        endpoints.MapSecuredPut($"/v2/claimSets/{{id}}", Update);
        endpoints.MapSecuredDelete($"/v2/claimSets/{{id}}", Delete);
        endpoints.MapSecuredPost("/v2/claimSets/copy", Copy);
        endpoints.MapSecuredPost("/v2/claimSets/import", Import);
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
            ClaimSetInsertResult.FailureDuplicateClaimSetName => Results.Json(
                FailureResponse.ForDataValidation(
                    new[]
                    {
                        new ValidationFailure(
                            "Name",
                            "A claim set with this name already exists in the database. Please enter a unique name."
                        ),
                    },
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.BadRequest
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetAll(
        IClaimSetRepository repository,
        [AsParameters] PagingQuery query,
        HttpContext httpContext,
        [FromQuery] bool verbose = false
    )
    {
        ClaimSetQueryResult result = await repository.QueryClaimSet(query, verbose);

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
        ILogger<ClaimSetModule> logger,
        [FromQuery] bool verbose = false
    )
    {
        ClaimSetGetResult result = await repository.GetClaimSet(id, verbose);

        return result switch
        {
            ClaimSetGetResult.Success success => Results.Ok(success.ClaimSetResponse),
            ClaimSetGetResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"ClaimSet {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
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
            ClaimSetUpdateResult.FailureDuplicateClaimSetName => Results.Json(
                FailureResponse.ForDataValidation(
                    [
                        new ValidationFailure(
                            "Name",
                            "A claim set with this name already exists in the database. Please enter a unique name."
                        ),
                    ],
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.BadRequest
            ),
            ClaimSetUpdateResult.FailureMultiUserConflict => Results.Json(
                FailureResponse.ForConflict(
                    $"Unable to update claim set due to multi-user conflicts. Retry the request.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.Conflict
            ),
            ClaimSetUpdateResult.FailureMultipleHierarchiesFound => Results.Json(
                FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                statusCode: (int)HttpStatusCode.InternalServerError
            ),
            ClaimSetUpdateResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"ClaimSet {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            ClaimSetUpdateResult.FailureUnknown => Results.Json(
                FailureResponse.ForUnknown(httpContext.TraceIdentifier),
                statusCode: (int)HttpStatusCode.InternalServerError
            ),
            ClaimSetUpdateResult.FailureSystemReserved => Results.Json(
                FailureResponse.ForBadRequest(
                    "The specified claim set is system-reserved and cannot be updated.",
                    httpContext.TraceIdentifier
                ),
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
        ClaimSetDeleteResult result = await repository.DeleteClaimSet(id);

        return result switch
        {
            ClaimSetDeleteResult.Success => Results.NoContent(),
            ClaimSetDeleteResult.FailureSystemReserved => Results.Json(
                FailureResponse.ForBadRequest(
                    "The specified claim set is system-reserved and cannot be deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.BadRequest
            ),
            ClaimSetDeleteResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"ClaimSet {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
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
        ClaimSetExportResult result = await repository.Export(id);

        return result switch
        {
            ClaimSetExportResult.Success success => Results.Ok(success.ClaimSetExportResponse),
            ClaimSetExportResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"ClaimSet {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
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
                $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                null
            ),
            ClaimSetCopyResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"OriginalId {entity.OriginalId} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
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
            return Results.Json(FailureResponse.ForUnknown(httpContext.TraceIdentifier));
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

            return Results.Json(FailureResponse.ForUnknown(httpContext.TraceIdentifier));
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
        var insertResult = await claimSetRepository.Import(importCommand);

        var request = httpContext.Request;

        return insertResult switch
        {
            ClaimSetImportResult.Success success => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{GetClaimSetsPath()}/{success.Id}",
                null
            ),
            ClaimSetImportResult.FailureDuplicateClaimSetName => Results.Json(
                FailureResponse.ForDataValidation(
                    new[]
                    {
                        new ValidationFailure(
                            "Name",
                            "A claim set with this name already exists in the database. Please enter a unique name."
                        ),
                    },
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.BadRequest
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };

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

        string? GetClaimSetsPath()
        {
            const string ClaimSetsSegment = "/claimSets/";

            if (!request.Path.HasValue)
            {
                return null;
            }

            int claimSetsPos = request.Path.Value.IndexOf(
                ClaimSetsSegment,
                StringComparison.OrdinalIgnoreCase
            );

            if (claimSetsPos < 0)
            {
                return request.Path.Value.TrimEnd('/');
            }

            return request.Path.Value.Substring(0, claimSetsPos + ClaimSetsSegment.Length - 1);
        }
    }
}
