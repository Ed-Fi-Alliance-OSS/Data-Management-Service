// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Utilities;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.AspNetCore.Routing;
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
    private const string MetadataRouteQualifierValueNamePrefix = "__metadataRouteQualifier";

    internal static string BuildRoutePattern(string[] routeQualifierSegments, bool multiTenancy)
    {
        var segments = new List<string>();

        if (multiTenancy)
        {
            segments.Add($"{{{TenantRouteValueName}}}");
        }

        for (int index = 0; index < routeQualifierSegments.Length; index++)
        {
            segments.Add($"{{{RouteQualifierValueName(index)}}}");
        }

        return segments.Count == 0 ? string.Empty : $"/{string.Join("/", segments)}";
    }

    public async Task<bool> ValidateAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default
    )
    {
        string[] qualifierSegments = appSettings.Value.GetRouteQualifierSegmentsArray();
        bool hasTenant = httpContext.Request.RouteValues.ContainsKey(TenantRouteValueName);
        bool hasQualifiers = HasAnyQualifierRouteValue(httpContext, qualifierSegments);

        // Only absent context keys identify an unqualified compatibility route.
        // Present-but-blank values must not bypass validation.
        if (!hasTenant && !hasQualifiers)
        {
            return true;
        }

        string tenant = ReadRouteValue(httpContext, TenantRouteValueName);
        if (
            ((hasTenant || appSettings.Value.MultiTenancy) && string.IsNullOrWhiteSpace(tenant))
            || (hasQualifiers && HasAnyBlankQualifierRouteValue(httpContext, qualifierSegments))
        )
        {
            await WriteNotFoundAsync(httpContext);
            return false;
        }

        Dictionary<RouteQualifierName, RouteQualifierValue> requestQualifiers = hasQualifiers
            ? ReadRouteQualifiers(httpContext, qualifierSegments)
            : [];

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
            await WriteNotFoundAsync(httpContext);
            return false;
        }

        return true;
    }

    private static bool HasAnyQualifierRouteValue(HttpContext httpContext, string[] routeQualifierSegments)
    {
        bool hasRouteEndpoint = httpContext.GetEndpoint() is RouteEndpoint;

        for (int index = 0; index < routeQualifierSegments.Length; index++)
        {
            if (
                httpContext.Request.RouteValues.ContainsKey(RouteQualifierValueName(index))
                || (
                    !hasRouteEndpoint
                    && httpContext.Request.RouteValues.ContainsKey(routeQualifierSegments[index])
                )
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAnyBlankQualifierRouteValue(
        HttpContext httpContext,
        string[] routeQualifierSegments
    )
    {
        for (int index = 0; index < routeQualifierSegments.Length; index++)
        {
            if (
                string.IsNullOrWhiteSpace(
                    ReadQualifierRouteValue(httpContext, routeQualifierSegments[index], index)
                )
            )
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<RouteQualifierName, RouteQualifierValue> ReadRouteQualifiers(
        HttpContext httpContext,
        string[] routeQualifierSegments
    )
    {
        var routeQualifiers = new Dictionary<RouteQualifierName, RouteQualifierValue>();

        for (int index = 0; index < routeQualifierSegments.Length; index++)
        {
            string value = ReadQualifierRouteValue(httpContext, routeQualifierSegments[index], index);
            if (!string.IsNullOrWhiteSpace(value))
            {
                routeQualifiers[new RouteQualifierName(routeQualifierSegments[index])] =
                    new RouteQualifierValue(value);
            }
        }

        return routeQualifiers;
    }

    private static string ReadQualifierRouteValue(
        HttpContext httpContext,
        string routeQualifierSegment,
        int index
    )
    {
        string value = ReadRouteValue(httpContext, RouteQualifierValueName(index));
        return string.IsNullOrWhiteSpace(value) ? ReadRouteValue(httpContext, routeQualifierSegment) : value;
    }

    private static string ReadRouteValue(HttpContext httpContext, string routeValueName)
    {
        return
            httpContext.Request.RouteValues[routeValueName] is string stringValue
            && !string.IsNullOrWhiteSpace(stringValue)
            ? stringValue
            : string.Empty;
    }

    private static string RouteQualifierValueName(int index) =>
        $"{MetadataRouteQualifierValueNamePrefix}{index}";

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
