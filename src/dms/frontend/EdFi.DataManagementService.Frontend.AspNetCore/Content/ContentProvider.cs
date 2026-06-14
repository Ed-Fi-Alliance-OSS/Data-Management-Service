// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;

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
    /// Provides xsd file stream for the requested metadata section.
    /// </summary>
    /// <param name="fileName"></param>
    /// <param name="section"></param>
    /// <returns></returns>
    Lazy<Stream> LoadXsdContent(string fileName, string section);

    /// <summary>
    /// Provides xsd file stream for the requested metadata section, when found.
    /// </summary>
    /// <param name="fileName"></param>
    /// <param name="section"></param>
    /// <returns></returns>
    Lazy<Stream>? TryLoadXsdContent(string fileName, string section);

    /// <summary>
    /// Provides section-based xsd file list.
    /// </summary>
    /// <param name="section"></param>
    /// <returns></returns>
    IEnumerable<string> ListXsdFiles(string section);
}

/// <summary>
/// Loads and parses the file content.
/// </summary>
public class ContentProvider(
    ILogger<ContentProvider> _logger,
    IApiSchemaAssetManifestProvider manifestProvider
) : IContentProvider
{
    public IEnumerable<string> ListXsdFiles(string section)
    {
        var candidatePaths = ResolveXsdListingPathsForSection(section);

        return candidatePaths
            .Select(p => Path.GetFileName(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

        using StreamReader reader = new(GetJsonStreamFromManifest(fileNamePattern));
        var jsonContent = reader.ReadToEnd();

        JsonNode? jsonNodeFromFile = JsonNode.Parse(jsonContent);
        if (jsonNodeFromFile == null)
        {
            _logger.LogCritical(contentError);
            throw new InvalidOperationException(contentError);
        }

        return jsonNodeFromFile;
    }

    public Lazy<Stream> LoadXsdContent(string fileName, string section)
    {
        return TryLoadXsdContent(fileName, section) ?? throw CreateXsdNotFoundException();
    }

    public Lazy<Stream>? TryLoadXsdContent(string fileName, string section)
    {
        _logger.LogDebug("Entering Xsd FileLoader");
        var matchedPath = ResolveXsdPathForSection(fileName, section);

        if (matchedPath is null)
        {
            return null;
        }

        return new Lazy<Stream>(() => File.OpenRead(matchedPath));
    }

    private Stream GetJsonStreamFromManifest(string fileNamePattern)
    {
        // Only discovery-spec JSON is served from the manifest.
        // Other section names (resources-spec, descriptors-spec) come from IApiService, not ContentProvider.
        // For unknown JSON sections in file mode, keep the legacy failure shape.
        if (!fileNamePattern.Equals("discovery-spec", StringComparison.OrdinalIgnoreCase))
        {
            var unknownError = $"Unable to read and parse {fileNamePattern}.json";
            _logger.LogCritical(unknownError);
            throw new InvalidOperationException(unknownError);
        }

        var manifest = manifestProvider.GetManifest();
        var project = GetCoreProject(manifest);

        // Serve discovery-spec only from the core manifest project. Extension discovery specs are
        // package-local assets, not the global DMS discovery document.
        if (project.DiscoverySpecPath is not null)
        {
            var resolvedPath = manifestProvider.ResolveValidatedPath(project.DiscoverySpecPath);
            if (!File.Exists(resolvedPath))
            {
                var invalidWorkspaceError =
                    $"Manifest project '{project.ProjectName}' declares discoverySpecPath "
                    + $"'{project.DiscoverySpecPath}', but the resolved file '{resolvedPath}' does not exist.";
                _logger.LogCritical(
                    "Invalid ApiSchema workspace: {Error}",
                    SanitizeForLog(invalidWorkspaceError)
                );
                throw new InvalidOperationException(invalidWorkspaceError);
            }

            _logger.LogDebug(
                "Serving discovery-spec from manifest project {ProjectName}",
                SanitizeForLog(project.ProjectName)
            );
            return File.OpenRead(resolvedPath);
        }

        // No project provides a discovery spec: keep the legacy failure shape.
        var error = $"Couldn't load find the resource";
        _logger.LogCritical(error);
        throw new InvalidOperationException(error);
    }

    private string? ResolveXsdPathForSection(string fileNamePattern, string section)
    {
        return ResolveXsdListingPathsForSection(section)
            .FirstOrDefault(f =>
                Path.GetFileName(f).Equals(fileNamePattern, StringComparison.OrdinalIgnoreCase)
            );
    }

    private IEnumerable<string> ResolveXsdListingPathsForSection(string section)
    {
        var manifest = manifestProvider.GetManifest();
        var sectionProject = FindProjectForSection(manifest, section);

        if (sectionProject is null)
        {
            return [];
        }

        if (!sectionProject.IsExtensionProject)
        {
            return manifestProvider.EnumerateValidatedXsdFiles(sectionProject);
        }

        var xsdPaths = new List<string>();
        var coreProject = GetCoreProject(manifest);

        if (!IsSameProject(sectionProject, coreProject))
        {
            xsdPaths.AddRange(manifestProvider.EnumerateValidatedXsdFiles(coreProject));
        }

        xsdPaths.AddRange(manifestProvider.EnumerateValidatedXsdFiles(sectionProject));

        return xsdPaths;
    }

    private static ApiSchemaProject GetCoreProject(ApiSchemaAssetManifest manifest)
    {
        return manifest.Projects.Single(p => !p.IsExtensionProject);
    }

    private static ApiSchemaProject? FindProjectForSection(ApiSchemaAssetManifest manifest, string section)
    {
        return manifest.Projects.FirstOrDefault(p =>
            p.ProjectName.Equals(section, StringComparison.OrdinalIgnoreCase)
            || p.ProjectEndpointName.Equals(section, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static bool IsSameProject(ApiSchemaProject left, ApiSchemaProject right)
    {
        return left.ProjectName.Equals(right.ProjectName, StringComparison.OrdinalIgnoreCase);
    }

    private InvalidOperationException CreateXsdNotFoundException()
    {
        var error = $"Couldn't load find the resource";
        _logger.LogCritical(error);
        return new InvalidOperationException(error);
    }

    /// <summary>
    /// Sanitizes a string for safe logging by allowing only safe characters.
    /// Uses a whitelist approach to prevent log injection and log forging attacks.
    /// Allows: letters, digits, spaces, and safe punctuation (_-.:/)
    /// </summary>
    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return new string(
            input
                .Where(c =>
                    char.IsLetterOrDigit(c)
                    || c == ' '
                    || c == '_'
                    || c == '-'
                    || c == '.'
                    || c == ':'
                    || c == '/'
                )
                .ToArray()
        );
    }
}
