// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Content;

public interface IMetadataRouteValidator
{
    Task<bool> ValidateAsync(HttpContext httpContext, CancellationToken cancellationToken = default);
}

public class MetadataRouteValidator(ITenantValidator tenantValidator, IDataStoreProvider dataStoreProvider)
    : IMetadataRouteValidator
{
    private const string TenantRouteValueName = "tenant";

    public async Task<bool> ValidateAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default
    )
    {
        string? tenant = ReadRouteValue(httpContext, TenantRouteValueName);
        Dictionary<RouteQualifierName, RouteQualifierValue> requestQualifiers = ReadRouteQualifiers(
            httpContext
        );

        if (tenant is null && requestQualifiers.Count == 0)
        {
            return true;
        }

        if (tenant is not null && !await tenantValidator.ValidateTenantAsync(tenant))
        {
            await WriteNotFoundAsync(httpContext);
            return false;
        }

        if (requestQualifiers.Count == 0)
        {
            return true;
        }

        IReadOnlyList<DataStore> dataStores = dataStoreProvider.GetAll(tenant);
        if (!dataStores.Any(dataStore => IsRouteContextMatch(dataStore.RouteContext, requestQualifiers)))
        {
            await WriteNotFoundAsync(httpContext);
            return false;
        }

        return true;
    }

    private static Dictionary<RouteQualifierName, RouteQualifierValue> ReadRouteQualifiers(
        HttpContext httpContext
    )
    {
        return httpContext
            .Request.RouteValues.Where(routeValue =>
                routeValue.Key != TenantRouteValueName
                && routeValue.Value is string value
                && !string.IsNullOrWhiteSpace(value)
            )
            .ToDictionary(
                routeValue => new RouteQualifierName(routeValue.Key),
                routeValue => new RouteQualifierValue((string)routeValue.Value!),
                EqualityComparer<RouteQualifierName>.Default
            );
    }

    private static string? ReadRouteValue(HttpContext httpContext, string routeValueName)
    {
        return
            httpContext.Request.RouteValues.TryGetValue(routeValueName, out object? value)
            && value is string stringValue
            && !string.IsNullOrWhiteSpace(stringValue)
            ? stringValue
            : null;
    }

    private static bool IsRouteContextMatch(
        Dictionary<RouteQualifierName, RouteQualifierValue> instanceRouteContext,
        Dictionary<RouteQualifierName, RouteQualifierValue> requestQualifiers
    )
    {
        if (instanceRouteContext.Count != requestQualifiers.Count)
        {
            return false;
        }

        if (instanceRouteContext.Count == 0)
        {
            return true;
        }

        if (!instanceRouteContext.Keys.All(requestQualifiers.ContainsKey))
        {
            return false;
        }

        foreach (KeyValuePair<RouteQualifierName, RouteQualifierValue> kvp in instanceRouteContext)
        {
            if (
                !requestQualifiers.TryGetValue(kvp.Key, out RouteQualifierValue requestValue)
                || !kvp.Value.Value.Equals(requestValue.Value, StringComparison.OrdinalIgnoreCase)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static Task WriteNotFoundAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
        return httpContext.Response.WriteAsSerializedJsonAsync(
            new
            {
                detail = "The specified resource could not be found.",
                type = "urn:ed-fi:api:not-found",
                title = "Not Found",
                status = 404,
            }
        );
    }
}
