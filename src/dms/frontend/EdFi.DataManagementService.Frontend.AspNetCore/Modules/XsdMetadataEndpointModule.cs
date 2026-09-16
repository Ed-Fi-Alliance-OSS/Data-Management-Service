// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public class XsdMetadataEndpointModule(IOptions<AppSettings> appSettings) : IEndpointModule
{
    private readonly string ErrorResourcePath = "Invalid resource path";

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        string routePattern = FixedRoutePattern.Build(
            appSettings.Value.GetRouteQualifierSegmentsArray(),
            appSettings.Value.MultiTenancy
        );

        endpoints.MapGet($"{routePattern}/metadata/xsd", GetSections);
        endpoints.MapGet($"{routePattern}/metadata/xsd/{{section}}/files", GetXsdMetadataFiles);
        endpoints.MapGet(
            $"{routePattern}/metadata/xsd/{{section}}/{{fileName}}.xsd",
            GetXsdMetadataFileContent
        );
    }

    internal static async Task GetSections(
        HttpContext httpContext,
        IApiService apiService,
        IMetadataRouteValidator metadataRouteValidator
    )
    {
        if (!await metadataRouteValidator.ValidateAsync(httpContext))
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
        IMetadataRouteValidator metadataRouteValidator
    )
    {
        if (!await metadataRouteValidator.ValidateAsync(httpContext))
        {
            return;
        }

        string? section = httpContext.Request.RouteValues["section"] as string;
        if (string.IsNullOrEmpty(section))
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await httpContext.Response.WriteAsync(ErrorResourcePath);
            return;
        }

        if (!contentProvider.IsXsdSectionKnown(section))
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await httpContext.Response.WriteAsync(ErrorResourcePath);
            return;
        }

        const string fileListSegment = "/files";
        var url = httpContext.Request.UrlWithPathSegment();
        var baseUrl = url.EndsWith(fileListSegment, StringComparison.Ordinal)
            ? $"{url[..^fileListSegment.Length]}/"
            : url;

        var withFullPath = new List<string>();
        var xsdFiles = contentProvider.ListXsdFiles(section);

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

    internal async Task<IResult> GetXsdMetadataFileContent(
        HttpContext httpContext,
        IContentProvider contentProvider,
        IMetadataRouteValidator metadataRouteValidator
    )
    {
        if (!await metadataRouteValidator.ValidateAsync(httpContext))
        {
            return Results.Empty;
        }

        string? section = httpContext.Request.RouteValues["section"] as string;
        string? fileName = httpContext.Request.RouteValues["fileName"] as string;
        if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(fileName))
        {
            return Results.NotFound(ErrorResourcePath);
        }

        var fileFullName = $"{fileName}.xsd";
        var content = contentProvider.TryLoadXsdContent(fileFullName, section);
        if (content is not null)
        {
            return Results.File(content.Value, "application/xml");
        }
        else
        {
            return Results.NotFound(ErrorResourcePath);
        }
    }

}

public record XsdMetaDataSectionInfo(string description, string name, string version, string files);
