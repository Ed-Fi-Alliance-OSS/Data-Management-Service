// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Api.Content;
using EdFi.DataManagementService.Api.Infrastructure.Extensions;
using Microsoft.Extensions.FileProviders;

namespace EdFi.DataManagementService.Api.Modules;

public class XsdMetaDataModule : IModule
{
    private readonly Regex PathExpressionRegex = new(@"\/(?<section>[^/]+)\/files?");
    private readonly Regex FilePathExpressionRegex = new(@"\/(?<section>[^/]+)\/(?<fileName>[^/]+).xsd?");

    private const string EdFi = "ed-fi";

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/metadata/xsd", GetSections);
        endpoints.MapGet("/metadata/xsd/{section}/files", GetXsdMetaDataFiles);
        endpoints.MapGet("/metadata/xsd/{section}/{fileName}.xsd", GetXsdMetaDataFileContent);
    }

    internal async Task GetSections(HttpContext httpContext)
    {
        var baseUrl = httpContext.Request.UrlWithPathSegment();
        List<XsdMetaDataSectionInfo> sections = [];

        sections.Add(
            new XsdMetaDataSectionInfo(
                description: "Core schema (Ed-Fi) files for the data model",
                name: "Ed-Fi",
                version: "1.0.0",
                files: $"{baseUrl}/{EdFi}/files"
            )
        );

        await httpContext.Response.WriteAsSerializedJsonAsync(sections);
    }

    internal async Task GetXsdMetaDataFiles(HttpContext httpContext, IContentProvider contentProvider)
    {
        var request = httpContext.Request;
        Match match = PathExpressionRegex.Match(request.Path);
        if (!match.Success)
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
        }

        string section = match.Groups["section"].Value;
        if (section.ToLower().Equals(EdFi))
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
            await httpContext.Response.WriteAsync("Path not found");
        }
    }

    internal async Task GetXsdMetaDataFileContent(HttpContext httpContext, IContentProvider contentProvider)
    {
        var request = httpContext.Request;
        Match match = FilePathExpressionRegex.Match(request.Path);
        if (!match.Success)
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
        }

        string section = match.Groups["section"].Value;
        if (section.ToLower().Equals(EdFi))
        {
            var fileName = match.Groups["fileName"].Value;

            var assembly =
                Assembly.GetAssembly(typeof(EdFi.ApiSchema.Marker))
                ?? throw new InvalidOperationException($"Could not load ApiSchema assembly");

            var fileProvider = new EmbeddedFileProvider(assembly);

            await httpContext.Response.SendFileAsync(fileProvider.GetFileInfo($"{fileName}.xsd"));
        }
        else
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await httpContext.Response.WriteAsync("Path not found");
        }
    }
}

public record XsdMetaDataSectionInfo(string description, string name, string version, string files);
