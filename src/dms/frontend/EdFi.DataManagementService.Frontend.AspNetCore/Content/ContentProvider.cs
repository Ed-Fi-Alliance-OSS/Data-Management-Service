// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Content;

public interface IContentProvider
{
    /// <summary>
    /// Loads and parses the json file content.
    /// </summary>
    /// <param name="fileNamePattern"></param>
    /// <param name="hostUrl"></param>
    /// <param name="oAuthUrl"></param>
    /// <returns></returns>
    JsonNode LoadJsonContent(string fileNamePattern, string hostUrl, string oAuthUrl);

    /// <summary>
    /// Loads and parses the json file content.
    /// </summary>
    /// <param name="fileNamePattern"></param>
    /// <returns></returns>
    JsonNode LoadJsonContent(string fileNamePattern);

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
    /// <param name="section"></param>
    /// <returns></returns>
    IEnumerable<string> Files(string fileNamePattern, string fileExtension, string section);
}

/// <summary>
/// Loads and parses the file content.
/// </summary>
public class ContentProvider(
    ILogger<ContentProvider> _logger,
    IOptions<AppSettings> appSettings,
    IAssemblyLoader assemblyLoader
) : IContentProvider
{
    public IEnumerable<string> Files(string fileNamePattern, string fileExtension, string section)
    {
        string apiSchemaPath;
        string[] assemblies;
        var files = new List<string>();
        if (appSettings.Value.UseApiSchemaPath)
        {
            apiSchemaPath =
                appSettings.Value.ApiSchemaPath
                ?? throw new InvalidOperationException("ApiSchemaPath is not configured in AppSettings.");
        }
        else
        {
            apiSchemaPath = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        }

        assemblies = Directory
            .EnumerateFiles(apiSchemaPath, "*.*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).EndsWith(".ApiSchema.dll", StringComparison.OrdinalIgnoreCase))
            .GroupBy(Path.GetFileName)
            .Select(g => g.First())
            .OrderBy(f => f)
            .ToArray();

        foreach (var assemblyPath in assemblies)
        {
            _logger.LogInformation("assemblyPath is {AssemblyPath}", assemblyPath);
            var assembly = assemblyLoader.Load(assemblyPath);
            foreach (string resourceName in assembly.GetManifestResourceNames())
            {
                if (
                    Regex.IsMatch(resourceName, fileNamePattern, RegexOptions.IgnoreCase)
                    && resourceName.EndsWith(fileExtension)
                )
                {
                    var fileName = resourceName.Replace($"{assembly.GetName().Name}.xsd.", string.Empty);
                    if (!files.Contains(fileName))
                    {
                        files.Add(fileName);
                    }
                    _logger.LogInformation("fileName is {FileName}", fileName);
                }
            }
        }

        return files;
    }

    public JsonNode LoadJsonContent(string fileNamePattern, string hostUrl, string oAuthUrl)
    {
        var jsonNodeFromFile = LoadJsonContent(fileNamePattern);

        return ReplaceString(jsonNodeFromFile, $"{hostUrl}/data", oAuthUrl);

        static JsonNode ReplaceString(JsonNode jsonNodeFromFile, string hostUrl, string OauthUrl)
        {
            var stringValue = JsonSerializer.Serialize(jsonNodeFromFile);
            if (!string.IsNullOrEmpty(hostUrl))
            {
                stringValue = stringValue.Replace("HOST_URL/data/v3", hostUrl);
            }
            if (!string.IsNullOrEmpty(OauthUrl))
            {
                stringValue = stringValue.Replace("HOST_URL/oauth/token", OauthUrl);
            }
            return JsonNode.Parse(stringValue)!;
        }
    }

    public JsonNode LoadJsonContent(string fileNamePattern)
    {
        _logger.LogDebug("Entering Json FileLoader");

        var contentError = $"Unable to read and parse {fileNamePattern}.json";

        fileNamePattern = fileNamePattern.StartsWith("discovery")
            ? $"{fileNamePattern}-spec"
            : fileNamePattern;

        using StreamReader reader = new(GetStream(fileNamePattern, ".json"));
        var jsonContent = reader.ReadToEnd();

        JsonNode? jsonNodeFromFile = JsonNode.Parse(jsonContent);
        if (jsonNodeFromFile == null)
        {
            _logger.LogCritical(contentError);
            throw new InvalidOperationException(contentError);
        }

        return jsonNodeFromFile;
    }

    public Lazy<Stream> LoadXsdContent(string fileName)
    {
        _logger.LogDebug("Entering Xsd FileLoader");
        return new Lazy<Stream>(GetStream(fileName, ".xsd"));
    }

    public Stream GetStream(string fileNamePattern, string fileExtension)
    {
        string apiSchemaPath;
        string searchPattern;
        if (appSettings.Value.UseApiSchemaPath)
        {
            apiSchemaPath =
                appSettings.Value.ApiSchemaPath
                ?? throw new InvalidOperationException("ApiSchemaPath is not configured in AppSettings.");
        }
        else
        {
            apiSchemaPath = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        }

        searchPattern = "*.ApiSchema.dll";
        var assemblies = Directory.GetFiles(apiSchemaPath, searchPattern, SearchOption.AllDirectories);
        assemblies = assemblies.GroupBy(Path.GetFileName).Select(g => g.First()).ToArray();

        foreach (var assemblyPath in assemblies)
        {
            var assembly = assemblyLoader.Load(assemblyPath);
            var resourceName = assembly
                .GetManifestResourceNames()
                .SingleOrDefault(str =>
                    str.Contains(fileNamePattern, StringComparison.OrdinalIgnoreCase)
                    && str.EndsWith(fileExtension)
                );

            if (resourceName != null)
            {
                var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    return stream;
                }
            }
        }

        var error = $"Couldn't load find the resource";
        _logger.LogCritical(error);
        throw new InvalidOperationException(error);
    }
}

/// <summary>
/// Returns ApiSchemaAssemblyLoadContext for loading Assembly Context
/// </summary>
public class ApiSchemaAssemblyLoadContext : AssemblyLoadContext
{
    public ApiSchemaAssemblyLoadContext()
        : base(isCollectible: true) { }
}

public interface IAssemblyLoader
{
    Assembly Load(string path);
}

public class ApiSchemaAssemblyLoader : IAssemblyLoader
{
    public Assembly Load(string path)
    {
        var requestData = new ApiSchemaAssemblyLoadContext();
        return requestData.LoadFromAssemblyPath(path);
    }
}
