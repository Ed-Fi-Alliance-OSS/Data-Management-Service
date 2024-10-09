// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.Extensions;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Modules;

public partial class XsdMetadataEndpointModule : IEndpointModule
{
    [GeneratedRegex(@"\/(?<section>[^/]+)\/files?")]
    private static partial Regex PathExpressionRegex();

    [GeneratedRegex(@"\/(?<section>[^/]+)\/(?<fileName>[^/]+).xsd?")]
    private static partial Regex FilePathExpressionRegex();

    private readonly string ErrorResourcePath = "Invalid resource path";

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/metadata/xsd", GetSections);
        endpoints.MapGet("/metadata/xsd/{section}/files", GetXsdMetadataFiles);
        endpoints.MapGet("/metadata/xsd/{section}/{fileName}.xsd", GetXsdMetadataFileContent);
    }

    internal static async Task GetSections(HttpContext httpContext, IApiService apiService)
    {
        var baseUrl = httpContext.Request.UrlWithPathSegment();
        List<XsdMetaDataSectionInfo> sections = [];

        foreach (IDataModelInfo dataModelInfo in apiService.GetDataModelInfo())
        {
            sections.Add(
                new XsdMetaDataSectionInfo(
                    description: $"Core schema ({dataModelInfo.ProjectName}) files for the data model",
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
        IApiService apiService
    )
    {
        var request = httpContext.Request;
        Match match = PathExpressionRegex().Match(request.Path);
        if (!match.Success)
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await httpContext.Response.WriteAsync(ErrorResourcePath);
        }

        string section = match.Groups["section"].Value;
        IList<IDataModelInfo> dataModelInfos = apiService.GetDataModelInfo();

        if (dataModelInfos.Any(x => x.ProjectName.Equals(section, StringComparison.InvariantCultureIgnoreCase)))
        {
            var baseUrl = httpContext.Request.UrlWithPathSegment().Replace("files", "");
            var withFullPath = new List<string>();
            var xsdFiles = contentProvider.Files("ApiSchema.xsd", ".xsd");
            withFullPath.AddRange(from xsdFile in xsdFiles select $"{baseUrl}{xsdFile}");
            await httpContext.Response.WriteAsSerializedJsonAsync(withFullPath);
        }
        else
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await httpContext.Response.WriteAsync(ErrorResourcePath);
        }
    }

    internal IResult GetXsdMetadataFileContent(HttpContext httpContext, IContentProvider contentProvider)
    {
        var request = httpContext.Request;
        Match match = FilePathExpressionRegex().Match(request.Path);
        if (!match.Success)
        {
            return Results.NotFound(ErrorResourcePath);
        }
        var fileName = match.Groups["fileName"].Value;
        var fileFullName = $"{fileName}.xsd";
        var files = contentProvider.Files(fileFullName, ".xsd");
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
}

public record XsdMetaDataSectionInfo(string description, string name, string version, string files);
