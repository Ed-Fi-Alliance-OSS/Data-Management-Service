// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Loads and parses ApiSchema files.
/// </summary>
internal class ApiSchemaFileLoader(ILogger<ApiSchemaFileLoader> _logger, IOptions<AppSettings> appSettings)
    : IApiSchemaProvider
{
    /// <summary>
    /// Loads the resource with the given resourceName from the assembly as a JsonNode
    /// </summary>
    private static JsonNode LoadFromAssembly(string resourceName, Assembly assembly)
    {
        using Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Could not load assembly-bundled ApiSchema file '{resourceName}'"
            );

        using StreamReader reader = new(stream);

        string? jsonContent = reader.ReadToEnd();

        return JsonNode.Parse(jsonContent)
            ?? throw new InvalidOperationException(
                $"Unable to parse assembly-bundled ApiSchema file '{resourceName}'"
            );
    }

    /// <summary>
    /// Finds and reads all ApiSchema*.json files in the given directory path.
    /// Returns the parsed files as JsonNodes
    /// </summary>
    private static List<JsonNode> ReadApiSchemaFiles(string directoryPath)
    {
        List<JsonNode> fileContents = [];

        try
        {
            IEnumerable<string> matchingFilePaths = Directory.EnumerateFiles(
                directoryPath,
                "ApiSchema*.json",
                SearchOption.AllDirectories
            );

            foreach (string filePath in matchingFilePaths)
            {
                try
                {
                    // Read all text from the file into a string.
                    string fileContent = File.ReadAllText(filePath);

                    JsonNode parsedFileContent =
                        JsonNode.Parse(fileContent)
                        ?? throw new InvalidOperationException(
                            $"Unable to parse ApiSchema file at '{filePath}'"
                        );

                    fileContents.Add(parsedFileContent);
                }
                catch (IOException ex)
                {
                    throw new InvalidOperationException($"Error reading file '{filePath}'", ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new InvalidOperationException($"Access denied to file '{filePath}'", ex);
                }
            }
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new InvalidOperationException($"Directory not found: '{directoryPath}'", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Access denied to directory '{directoryPath}'", ex);
        }

        return fileContents;
    }

    /// <summary>
    /// Returns core and extension ApiSchema JsonNodes
    /// </summary>
    private readonly Lazy<ApiSchemaNodes> _apiSchemaNodes = new(() =>
    {
        if (appSettings.Value.UseApiSchemaPath)
        {
            string apiSchemaPath =
                appSettings.Value.ApiSchemaPath
                ?? throw new InvalidOperationException("No ApiSchemaPath configuration is set");
            List<JsonNode> apiSchemaNodes = ReadApiSchemaFiles(apiSchemaPath);

            JsonNode coreApiSchemaNode = apiSchemaNodes.First(node =>
                !node.SelectRequiredNodeFromPathAs<bool>("$.projectSchema.isExtensionProject", _logger)
            );

            JsonNode[] extensionApiSchemaNodes = apiSchemaNodes
                .Where(node =>
                    node.SelectRequiredNodeFromPathAs<bool>("$.projectSchema.isExtensionProject", _logger)
                )
                .ToArray();

            return new(coreApiSchemaNode, extensionApiSchemaNodes);
        }
        else
        {
            Assembly assembly =
                Assembly.GetAssembly(typeof(DataStandard51.ApiSchema.Marker))
                ?? throw new InvalidOperationException("Could not load assembly-bundled ApiSchema file");

            string? resourceName = assembly
                .GetManifestResourceNames()
                .Single(str => str.EndsWith("ApiSchema.json"));

            return new(LoadFromAssembly(resourceName, assembly), []);
        }
    });

    /// <summary>
    /// Returns core and extension ApiSchema JsonNodes
    /// </summary>
    public ApiSchemaNodes GetApiSchemaNodes()
    {
        return _apiSchemaNodes.Value;
    }
}
