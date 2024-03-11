// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Api.Content;

public interface IContentProvider
{
    /// <summary>
    /// Loads and parses the json file content.
    /// </summary>
    /// <param name="fileNamePattern"></param>
    /// <param name="hostUrl"></param>
    /// <returns></returns>
    Lazy<JsonNode> LoadJsonContent(string fileNamePattern, string? hostUrl = null);

    /// <summary>
    /// Provides xsd file stream.
    /// </summary>
    /// <param name="fileName"></param>
    /// <returns></returns>
    Lazy<Stream> LoadXsdContent(string fileName);

    /// <summary>
    /// Provides list of files.
    /// </summary>
    /// <param name="fileNamePattern"></param>
    /// <param name="fileExtension"></param>
    /// <returns></returns>
    IEnumerable<string> Files(string fileNamePattern, string fileExtension);
}

/// <summary>
/// Loads and parses the file content.
/// </summary>
public class ContentProvider : IContentProvider
{
    private readonly ILogger<ContentProvider> _logger;

    public ContentProvider(ILogger<ContentProvider> logger)
    {
        _logger = logger;
    }

    public IEnumerable<string> Files(string fileNamePattern, string fileExtension)
    {
        var files = new List<string>();
        foreach (string resourceName in GetApiSchemaAssembly().GetManifestResourceNames())
        {
            if (resourceName.Contains(fileNamePattern) && resourceName.EndsWith(fileExtension))
            {
                files.Add(resourceName);
            }
        }

        return files;
    }

    public Lazy<JsonNode> LoadJsonContent(string fileNamePattern, string? hostUrl = null)
    {
        _logger.LogDebug("Entering Json FileLoader");

        var contentError = "Unable to read and parse Api Schema file";

        using StreamReader reader = new(GetStream(fileNamePattern, ".json"));
        var jsonContent = reader.ReadToEnd();

        JsonNode? jsonNodeFromFile = JsonNode.Parse(jsonContent);
        if (jsonNodeFromFile == null)
        {
            _logger.LogCritical(contentError);
            throw new InvalidOperationException(contentError);
        }
        if (hostUrl != null)
        {
            ReplaceHostUrl(jsonNodeFromFile);
        }
        return new Lazy<JsonNode>(jsonNodeFromFile);

        void ReplaceHostUrl(JsonNode? jsonNodeFromFile)
        {
            var servers = jsonNodeFromFile?["servers"]?.AsArray();
            if (servers != null)
            {
                foreach (var server in servers)
                {
                    if (server != null)
                    {
                        var url = server["url"];
                        if (url != null)
                        {
                            server["url"] = url.GetValue<string>().Replace("HOST_URL", hostUrl);
                        }
                    }
                }
            }
        }
    }

    public Lazy<Stream> LoadXsdContent(string fileName)
    {
        _logger.LogDebug("Entering Xsd FileLoader");
        return new Lazy<Stream>(GetStream(fileName, ".xsd"));
    }

    private Stream GetStream(string fileNamePattern, string fileExtension)
    {
        var assembly = GetApiSchemaAssembly();
        var resourceName = assembly
            .GetManifestResourceNames()
            .Single(str => str.Contains(fileNamePattern) && str.EndsWith(fileExtension));

        Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Couldn't get resource at {resourceName}");

        return stream;
    }

    private Assembly GetApiSchemaAssembly()
    {
        var assembly =
            Assembly.GetAssembly(typeof(EdFi.ApiSchema.Marker))
            ?? throw new InvalidOperationException($"Could not load ApiSchema assembly");
        return assembly;
    }
}
