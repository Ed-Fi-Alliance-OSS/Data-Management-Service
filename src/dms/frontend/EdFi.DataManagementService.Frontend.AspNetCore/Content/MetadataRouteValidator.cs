// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.Extensions.Options;
using FrontendAppSettings = EdFi.DataManagementService.Frontend.AspNetCore.Configuration.AppSettings;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Content;

public interface IMetadataRouteValidator
{
    Task<bool> ValidateAsync(HttpContext httpContext, CancellationToken cancellationToken = default);
}

public class MetadataRouteValidator(
    ITenantValidator tenantValidator,
    IDataStoreProvider dataStoreProvider,
    IOptions<FrontendAppSettings> appSettings
) : IMetadataRouteValidator
{
    private const string TenantRouteValueName = "tenant";

    public async Task<bool> ValidateAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default
    )
    {
        string[] qualifierSegments = appSettings.Value.GetRouteQualifierSegmentsArray();
        bool hasTenant = httpContext.Request.RouteValues.ContainsKey(TenantRouteValueName);
        bool hasQualifiers = Array.Exists(qualifierSegments, httpContext.Request.RouteValues.ContainsKey);

        // Only absent context keys identify an unqualified compatibility route.
        // Present-but-blank values must not bypass validation.
        if (!hasTenant && !hasQualifiers)
        {
            return true;
        }

        string tenant = ReadRouteValue(httpContext, TenantRouteValueName);
        if (
            ((hasTenant || appSettings.Value.MultiTenancy) && string.IsNullOrWhiteSpace(tenant))
            || (
                hasQualifiers
                && Array.Exists(
                    qualifierSegments,
                    segment => string.IsNullOrWhiteSpace(ReadRouteValue(httpContext, segment))
                )
            )
        )
        {
            await WriteNotFoundAsync(httpContext);
            return false;
        }

        Dictionary<RouteQualifierName, RouteQualifierValue> requestQualifiers = ReadRouteQualifiers(
            httpContext,
            qualifierSegments
        );

        if (hasTenant && !await tenantValidator.ValidateTenantAsync(tenant))
        {
            await WriteNotFoundAsync(httpContext);
            return false;
        }

        if (requestQualifiers.Count == 0)
        {
            return true;
        }

        string? tenantKey = hasTenant ? tenant : null;
        try
        {
            await dataStoreProvider.RefreshInstancesIfExpiredAsync(tenantKey, cancellationToken);
        }
        catch
        {
            // Continue with the cached data stores when a refresh is unavailable.
        }

        IReadOnlyList<DataStore> dataStores = dataStoreProvider.GetAll(tenantKey);
        if (
            !dataStores.Any(dataStore =>
                RouteContextMatcher.IsMatch(dataStore.RouteContext, requestQualifiers)
            )
        )
        {
            try
            {
                await dataStoreProvider.LoadDataStores(tenantKey, cancellationToken);
            }
            catch
            {
                // The existing cache remains the final source for validation when reload fails.
            }

            dataStores = dataStoreProvider.GetAll(tenantKey);
            if (
                !dataStores.Any(dataStore =>
                    RouteContextMatcher.IsMatch(dataStore.RouteContext, requestQualifiers)
                )
            )
            {
                await WriteNotFoundAsync(httpContext);
                return false;
            }
        }

        return true;
    }

    private static Dictionary<RouteQualifierName, RouteQualifierValue> ReadRouteQualifiers(
        HttpContext httpContext,
        string[] routeQualifierSegments
    )
    {
        return httpContext
            .Request.RouteValues.Where(routeValue =>
                routeQualifierSegments.Contains(routeValue.Key, StringComparer.OrdinalIgnoreCase)
                && routeValue.Value is string value
                && !string.IsNullOrWhiteSpace(value)
            )
            .ToDictionary(
                routeValue => new RouteQualifierName(routeValue.Key),
                routeValue => new RouteQualifierValue((string)routeValue.Value!),
                EqualityComparer<RouteQualifierName>.Default
            );
    }

    private static string ReadRouteValue(HttpContext httpContext, string routeValueName)
    {
        return
            httpContext.Request.RouteValues[routeValueName] is string stringValue
            && !string.IsNullOrWhiteSpace(stringValue)
            ? stringValue
            : string.Empty;
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
