// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public partial class XsdMetadataEndpointModule(IOptions<AppSettings> appSettings) : IEndpointModule
{
    [GeneratedRegex(@"\/(?<section>[^/]+)\/files?")]
    private static partial Regex PathExpressionRegex();

    [GeneratedRegex(@"\/(?<section>[^/]+)\/(?<fileName>[^/]+).xsd?")]
    private static partial Regex FilePathExpressionRegex();

    private readonly string ErrorResourcePath = "Invalid resource path";

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var tenantPrefix = appSettings.Value.MultiTenancy ? "/{tenant}" : "";

        endpoints.MapGet($"{tenantPrefix}/metadata/xsd", GetSections);
        endpoints.MapGet($"{tenantPrefix}/metadata/xsd/{{section}}/files", GetXsdMetadataFiles);
        endpoints.MapGet(
            $"{tenantPrefix}/metadata/xsd/{{section}}/{{fileName}}.xsd",
            GetXsdMetadataFileContent
        );
    }

    internal static async Task GetSections(
        HttpContext httpContext,
        IApiService apiService,
        IOptions<AppSettings> options,
        ITenantValidator tenantValidator
    )
    {
        // Validate tenant if multi-tenancy is enabled
        if (!await ValidateTenantAsync(httpContext, options, tenantValidator))
        {
            return;
        }

        var baseUrl = httpContext.Request.UrlWithPathSegment();
        List<XsdMetaDataSectionInfo> sections = [];

        foreach (IDataModelInfo dataModelInfo in apiService.GetDataModelInfo().OrderBy(x => x.ProjectName))
        {
            sections.Add(
                new XsdMetaDataSectionInfo(
                    description: dataModelInfo.IsCoreProject
                        ? $"Core schema ({dataModelInfo.ProjectName}) files for the data model"
                        : $"Extension ({dataModelInfo.ProjectName}) blended with Core schema files for the data model",
                    name: dataModelInfo.ProjectName.ToLower(),
                    version: dataModelInfo.ProjectVersion,
                    files: $"{baseUrl}/{dataModelInfo.ProjectName.ToLower()}/files"
                )
            );
        }
        await httpContext.Response.WriteAsSerializedJsonAsync(sections);
    }

    internal async Task GetXsdMetadataFiles(
        HttpContext httpContext,
        IContentProvider contentProvider,
        IApiService apiService,
        IOptions<AppSettings> options,
        ITenantValidator tenantValidator
    )
    {
        // Validate tenant if multi-tenancy is enabled
        if (!await ValidateTenantAsync(httpContext, options, tenantValidator))
        {
            return;
        }

        var request = httpContext.Request;
        Match match = PathExpressionRegex().Match(request.Path);
        if (!match.Success)
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await httpContext.Response.WriteAsync(ErrorResourcePath);
        }

        string section = match.Groups["section"].Value;
        IDataModelInfo? dataModelInfo = apiService
            .GetDataModelInfo()
            .FirstOrDefault(x => x.ProjectName.Equals(section, StringComparison.InvariantCultureIgnoreCase));

        if (dataModelInfo != null)
        {
            var baseUrl = httpContext.Request.UrlWithPathSegment().Replace("files", "");
            var withFullPath = new List<string>();
            var searchPattern = dataModelInfo.IsCoreProject
                ? @"EdFi\.DataStandard.*\.ApiSchema"
                : $@"EdFi\.DataStandard.*\.ApiSchema|EdFi.{section}.ApiSchema";
            var xsdFiles = contentProvider.Files(searchPattern, ".xsd", section);

            if (xsdFiles.Any())
            {
                withFullPath.AddRange(from xsdFile in xsdFiles select $"{baseUrl}{xsdFile}");
            }
            else
            {
                withFullPath.Add("No XSD files found for extension.");
            }
            await httpContext.Response.WriteAsSerializedJsonAsync(withFullPath);
        }
        else
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await httpContext.Response.WriteAsync(ErrorResourcePath);
        }
    }

    internal async Task<IResult> GetXsdMetadataFileContent(
        HttpContext httpContext,
        IContentProvider contentProvider,
        IOptions<AppSettings> options,
        ITenantValidator tenantValidator
    )
    {
        // Validate tenant if multi-tenancy is enabled
        if (!await ValidateTenantAsync(httpContext, options, tenantValidator))
        {
            return Results.Empty;
        }

        var request = httpContext.Request;
        Match match = FilePathExpressionRegex().Match(request.Path);
        if (!match.Success)
        {
            return Results.NotFound(ErrorResourcePath);
        }
        string section = match.Groups["section"].Value;
        var fileName = match.Groups["fileName"].Value;
        var fileFullName = $"{fileName}.xsd";
        var files = contentProvider.Files(fileFullName, ".xsd", section);
        if (files.Any())
        {
            var content = contentProvider.LoadXsdContent(fileFullName);
            return Results.File(content.Value, "application/xml");
        }
        else
        {
            return Results.NotFound(ErrorResourcePath);
        }
    }

    /// <summary>
    /// Validates the tenant if multi-tenancy is enabled.
    /// Returns true if validation passes or multi-tenancy is disabled.
    /// Returns false and writes 404 response if tenant is invalid.
    /// </summary>
    private static async Task<bool> ValidateTenantAsync(
        HttpContext httpContext,
        IOptions<AppSettings> options,
        ITenantValidator tenantValidator
    )
    {
        if (!options.Value.MultiTenancy)
        {
            return true;
        }

        string? tenant = ExtractTenantFromRoute(httpContext);
        if (tenant == null)
        {
            // No tenant in route - this shouldn't happen with multi-tenancy enabled
            // but we'll let it pass since the route wouldn't match without tenant
            return true;
        }

        bool isValid = await tenantValidator.ValidateTenantAsync(tenant);
        if (!isValid)
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await httpContext.Response.WriteAsSerializedJsonAsync(
                new
                {
                    detail = "The specified resource could not be found.",
                    type = "urn:ed-fi:api:not-found",
                    title = "Not Found",
                    status = 404,
                }
            );
            return false;
        }

        return true;
    }

    /// <summary>
    /// Extracts the tenant identifier from the route values.
    /// Returns null if tenant is not present in the route.
    /// </summary>
    private static string? ExtractTenantFromRoute(HttpContext httpContext)
    {
        if (
            httpContext.Request.RouteValues.TryGetValue("tenant", out object? value)
            && value is string tenant
            && !string.IsNullOrWhiteSpace(tenant)
        )
        {
            return tenant;
        }
        return null;
    }
}

public record XsdMetaDataSectionInfo(string description, string name, string version, string files);
