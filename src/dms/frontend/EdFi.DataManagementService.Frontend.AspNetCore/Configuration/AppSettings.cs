// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Configuration;

public class AppSettings
{
    public const int BytesPerMegabyte = 1024 * 1024;
    public const int DefaultMaxRequestBodySizeMegabytes = 10;
    public const int DefaultMaxRequestBodySizeBytes = DefaultMaxRequestBodySizeMegabytes * BytesPerMegabyte;

    public required string AuthenticationService { get; set; }
    public required string Datastore { get; set; }
    public int MaxRequestBodySizeBytes { get; set; }
    public string? StartupStatusFilePath { get; set; }
    public required string CorrelationIdHeader { get; set; }
    public string DomainsExcludedFromOpenApi { get; set; } = string.Empty;
    public string RouteQualifierSegments { get; set; } = string.Empty;

    /// <summary>
    /// When true, enables multi-tenancy mode where the tenant identifier is extracted from the URL route.
    /// </summary>
    public bool MultiTenancy { get; set; } = false;

    /// <summary>
    /// Gets the route qualifier segments as an array by splitting the comma-separated string.
    /// Returns empty array if RouteQualifierSegments is null or empty.
    /// </summary>
    public string[] GetRouteQualifierSegmentsArray() =>
        string.IsNullOrWhiteSpace(RouteQualifierSegments)
            ? []
            : RouteQualifierSegments.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
}

public class AppSettingsValidator : IValidateOptions<AppSettings>
{
    public ValidateOptionsResult Validate(string? name, AppSettings options)
    {
        if (string.IsNullOrWhiteSpace(options.AuthenticationService))
        {
            return ValidateOptionsResult.Fail("Missing required AppSettings value: AuthenticationService");
        }

        if (string.IsNullOrWhiteSpace(options.Datastore))
        {
            return ValidateOptionsResult.Fail("Missing required AppSettings value: Datastore");
        }

        if (
            !options.Datastore.Equals("postgresql", StringComparison.CurrentCultureIgnoreCase)
            && !options.Datastore.Equals("mssql", StringComparison.CurrentCultureIgnoreCase)
        )
        {
            return ValidateOptionsResult.Fail(
                "AppSettings value Datastore must be one of: postgresql, mssql"
            );
        }

        if (options.MaxRequestBodySizeBytes <= 0)
        {
            return ValidateOptionsResult.Fail(
                "AppSettings value MaxRequestBodySizeBytes must be greater than 0"
            );
        }

        return ValidateOptionsResult.Success;
    }
}
