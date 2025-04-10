// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.CodeAnalysis;
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
    /// Finds and reads all *.ApiSchema.json files in the given directory path.
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

            if (!Directory.Exists(apiSchemaPath))
            {
                throw new InvalidOperationException($"The directory {apiSchemaPath} does not exist.");
            }

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
            if (string.IsNullOrEmpty(appSettings.Value.PluginFolder))
            {
                throw new InvalidOperationException("PluginFolder is not configured.");
            }
            JsonNode coreApiSchemaNode = new JsonObject();
            JsonNode[] extensionApiSchemaNodes = Array.Empty<JsonNode>();

            var projectDirectory = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../"));
            var relativeToProject = Path.GetFullPath(Path.Combine(projectDirectory, appSettings.Value.PluginFolder));

            if (!Directory.Exists(relativeToProject))
            {
                throw new InvalidOperationException("relativeToProject is not exists.");
            }

            string pluginApiSchemaPath = Path.GetFullPath(relativeToProject);
            var assemblies = Directory.GetFiles(pluginApiSchemaPath, "*.dll", SearchOption.AllDirectories);
            var pluginAssemblyLoadContext = new PluginAssemblyLoadContext();
            foreach (var assemblyPath in assemblies)
            {
                var assembly = pluginAssemblyLoadContext.LoadFromAssemblyPath(assemblyPath);

                var manifestResourceNames = assembly.GetManifestResourceNames();

                var coreSchemaResourceName = Array.Find(
                    manifestResourceNames,
                    str => str.EndsWith("ApiSchema.json")
                );

                var extensionSchemaResourceName = Array.Find(
                    manifestResourceNames,
                    str => str.Contains(".ApiSchema-") && str.EndsWith("EXTENSION.json")
                );

                if (coreSchemaResourceName != null)
                {
                    _logger.LogInformation("Loading {CoreSchemaResourceName} from assembly", coreSchemaResourceName);
                    coreApiSchemaNode = LoadFromAssembly(coreSchemaResourceName, assembly);
                }
                else if (extensionSchemaResourceName != null)
                {
                    _logger.LogInformation("Loading {ExtensionSchemaResourceName} from assembly", extensionSchemaResourceName);
                    var extensionNodes = LoadFromAssembly(extensionSchemaResourceName, assembly);
                    extensionApiSchemaNodes = extensionApiSchemaNodes.Concat(new[] { extensionNodes }).ToArray();
                }
            }
            return new ApiSchemaNodes(coreApiSchemaNode, extensionApiSchemaNodes);
        }
    });

    /// <summary>
    /// Returns core and extension ApiSchema JsonNodes
    /// </summary>
    public ApiSchemaNodes GetApiSchemaNodes()
    {
        return _apiSchemaNodes.Value;
    }

    /// <summary>
    /// Returns PluginAssemblyLoadContext for loading Assembly Context
    /// </summary>
    private sealed class PluginAssemblyLoadContext : AssemblyLoadContext
    {
        public PluginAssemblyLoadContext() : base(isCollectible: true) { }
    }
}
