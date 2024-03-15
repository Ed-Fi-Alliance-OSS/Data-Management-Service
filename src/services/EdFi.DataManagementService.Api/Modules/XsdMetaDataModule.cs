// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Api.Content;
using EdFi.DataManagementService.Api.Infrastructure.Extensions;

namespace EdFi.DataManagementService.Api.Modules;

public partial class XsdMetaDataModule : IModule
{
    [GeneratedRegex(@"\/(?<section>[^/]+)\/files?")]
    private static partial Regex PathExpressionRegex();

    [GeneratedRegex(@"\/(?<section>[^/]+)\/(?<fileName>[^/]+).xsd?")]
    private static partial Regex FilePathExpressionRegex();

    private readonly string ErrorResourcePath = "Invalid resource path";

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/metadata/xsd", GetSections);
        endpoints.MapGet("/metadata/xsd/{section}/files", GetXsdMetaDataFiles);
        endpoints.MapGet("/metadata/xsd/{section}/{fileName}.xsd", GetXsdMetaDataFileContent);
    }

    internal async Task GetSections(HttpContext httpContext, IDataModelProvider dataModelProvider)
    {
        var baseUrl = httpContext.Request.UrlWithPathSegment();
        List<XsdMetaDataSectionInfo> sections = [];

        foreach (var model in dataModelProvider.GetDataModels())
        {
            sections.Add(
                new XsdMetaDataSectionInfo(
                    description: $"Core schema ({model.name}) files for the data model",
                    name: model.name,
                    version: model.version,
                    files: $"{baseUrl}/{model.name}/files"
                )
            );
        }
        await httpContext.Response.WriteAsSerializedJsonAsync(sections);
    }

    internal async Task GetXsdMetaDataFiles(
        HttpContext httpContext,
        IContentProvider contentProvider,
        IDataModelProvider dataModelProvider
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
        var dataModels = dataModelProvider.GetDataModels();

        if (dataModels.Any(x => x.name.Equals(section, StringComparison.InvariantCultureIgnoreCase)))
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

    internal IResult GetXsdMetaDataFileContent(HttpContext httpContext, IContentProvider contentProvider)
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
