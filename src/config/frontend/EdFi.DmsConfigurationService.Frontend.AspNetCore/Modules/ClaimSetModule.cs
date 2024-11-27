// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class ClaimSet : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v2/claimSets/import", ImportClaimSet).RequireAuthorizationWithPolicy();
        endpoints.MapPost($"/v2/claimSets/{{id}}/copy", CopyClaimSet).RequireAuthorizationWithPolicy();
        endpoints.MapPost("/v2/claimSets/", InsertClaimSet).RequireAuthorizationWithPolicy();

        endpoints.MapPut($"/v2/claimSets/", UpdateClaimSets).RequireAuthorizationWithPolicy();

        endpoints.MapGet($"/v2/claimSets/{{id}}/export", ExportClaimSetById).RequireAuthorizationWithPolicy();
        endpoints.MapGet($"/v2/claimSets/{{id}}", GetClaimSetById).RequireAuthorizationWithPolicy();
        endpoints.MapGet($"/v2/claimSets/", GetAllClaimSets).RequireAuthorizationWithPolicy();
    }

    /// <summary>
    /// Import the provided ClaimSet
    /// </summary>
    /// <returns></returns>
    private static IResult ImportClaimSet()
    {
        // Check injected body of httpContext
        // SecurityRepository.InsertClaimSet(InsetClaimSetCommand)
        return Results.Created();
    }

    /// <summary>
    /// Copy the ClaimSet to the provided id.
    /// </summary>
    /// <param name="id">The Id of the ClaimSet to be copied.</param>
    /// <returns></returns>
    private static IResult CopyClaimSet(long id)
    {
        // Check injected body of httpContext
        // SecurityRepository.InsertClaimSetById(id, InsetClaimSetCommand)
        return Results.Created();
    }

    /// <summary>
    /// Insert the provided ClaimSet.
    /// </summary>
    private static IResult InsertClaimSet(long id)
    {
        // Verify there is an id here.
        return Results.Created();
    }

    /// <summary>
    /// Insert the provided ClaimSet.
    /// </summary>
    private static IResult UpdateClaimSets()
    {
        return Results.Ok();
    }


    /// <summary>
    /// Retrieve claim set definition.
    /// </summary>
    /// <param name="id">The id of the claimSet.</param>
    private static IResult GetClaimSetById(long id, HttpContext httpContext)
    {
        // Verify there is an id here.
        // Verify that ?verbose=true query param
        // SecurityRepository.GetClaimSetById(id, isVerbose)
        return Results.Ok();
    }

    /// <summary>
    /// Retrieve a specific claimSet based on the identifier.
    /// </summary>
    /// <param name="id">The id of the claimSet.</param>
    private static IResult GetAllClaimSets(long id, HttpContext httpContext)
    {
        // Verify there is an id here.
        // Verify that ?verbose=true
        // SecurityRepository.GetAllClaimSets(id, isVerbose)
        return Results.Ok();
    }

    /// <summary>
    /// Return the entire claim set definition, including resourceClaims array.
    /// </summary>
    /// <param name="id">The id of the claimSet.</param>
    private static IResult ExportClaimSetById(long id)
    {
        // Verify there is an id here.
        // SecurityRepository.ExportClaimSetById(id)
        return Results.Ok();
    }
}
