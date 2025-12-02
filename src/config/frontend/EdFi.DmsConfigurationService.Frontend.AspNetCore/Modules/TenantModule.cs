// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class TenantModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredPost("/v2/tenants/", InsertTenant);
        endpoints.MapSecuredGet("/v2/tenants/", GetAll);
        endpoints.MapSecuredGet($"/v2/tenants/{{id}}", GetById);
    }

    private static async Task<IResult> InsertTenant(
        TenantInsertCommand entity,
        TenantInsertCommand.Validator validator,
        HttpContext httpContext,
        ITenantRepository repository
    )
    {
        await validator.GuardAsync(entity);
        var insertResult = await repository.InsertTenant(entity);

        var request = httpContext.Request;
        return insertResult switch
        {
            TenantInsertResult.Success success => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                new
                {
                    Id = success.Id,
                    Status = 201,
                    Title = $"New Tenant {SanitizeForLog(entity.Name)} has been created successfully.",
                }
            ),
            TenantInsertResult.FailureDuplicateName => Results.Json(
                FailureResponse.ForDataValidation(
                    [
                        new ValidationFailure(
                            "Name",
                            "A tenant name already exists in the database. Please enter a unique name."
                        ),
                    ],
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.BadRequest
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetAll(
        ITenantRepository repository,
        [AsParameters] PagingQuery query,
        HttpContext httpContext
    )
    {
        TenantQueryResult getResult = await repository.QueryTenant(query);
        return getResult switch
        {
            TenantQueryResult.Success success => Results.Ok(success.TenantResponses),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(long id, HttpContext httpContext, ITenantRepository repository)
    {
        TenantGetResult getResult = await repository.GetTenant(id);
        return getResult switch
        {
            TenantGetResult.Success success => Results.Ok(success.TenantResponse),
            TenantGetResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"Tenant {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
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
}
