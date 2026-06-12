// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;

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
    /// Provides xsd file stream for the requested metadata section.
    /// </summary>
    /// <param name="fileName"></param>
    /// <param name="section"></param>
    /// <returns></returns>
    Lazy<Stream> LoadXsdContent(string fileName, string section);

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
    IApiSchemaAssetManifestProvider manifestProvider
) : IContentProvider
{
    public IEnumerable<string> Files(string fileNamePattern, string fileExtension, string section)
    {
        return FilesFromManifest(fileNamePattern, fileExtension, section);
    }

    private IEnumerable<string> FilesFromManifest(
        string fileNamePattern,
        string fileExtension,
        string section
    )
    {
        // In file mode, only XSD files are listed from the manifest.
        // Return empty for any non-XSD file extension — the section-based listing is
        // only defined for XSD content.
        if (!fileExtension.Equals(".xsd", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        // XsdMetadataEndpointModule passes either an assembly-name regex (e.g.
        // "EdFi\.DataStandard.*\.ApiSchema") for listing all section XSDs, or a bare/
        // legacy-prefixed file name (e.g. "Ed-Fi-Core.xsd") for single-file lookup.
        // Assembly-name regex patterns do not end in ".xsd"; in file mode we ignore
        // them for filtering and use the section for manifest project selection instead.
        // File-name patterns ending in ".xsd" are normalized and matched exactly.
        bool patternIsFileNameFilter =
            fileNamePattern.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)
            || fileNamePattern.EndsWith(".xsd?", StringComparison.OrdinalIgnoreCase);

        var candidatePaths = ResolveXsdPathsForSection(section);

        // Extract bare file names for the listing
        var fileNames = candidatePaths
            .Select(p => Path.GetFileName(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!patternIsFileNameFilter)
        {
            // Assembly-name regex pattern — return the full section list
            return fileNames;
        }

        // Filter by the bare file-name pattern (exact match, case-insensitive)
        var normalizedPattern = NormalizeToBareXsdFileName(fileNamePattern);
        return fileNames.Where(f => f.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase));
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

    public Lazy<Stream> LoadXsdContent(string fileName, string section)
    {
        _logger.LogDebug("Entering Xsd FileLoader");
        return new Lazy<Stream>(GetXsdStreamFromManifest(fileName, section));
    }

    public Stream GetStream(string fileNamePattern, string fileExtension)
    {
        return GetStreamFromManifest(fileNamePattern, fileExtension);
    }

    private Stream GetStreamFromManifest(string fileNamePattern, string fileExtension)
    {
        if (fileExtension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return GetJsonStreamFromManifest(fileNamePattern);
        }

        if (fileExtension.Equals(".xsd", StringComparison.OrdinalIgnoreCase))
        {
            return GetXsdStreamFromManifest(fileNamePattern);
        }

        var error = $"Couldn't load find the resource";
        _logger.LogCritical(error);
        throw new InvalidOperationException(error);
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

        // Serve the first manifest project (in manifest order) that provides discoverySpecPath
        foreach (var project in manifest.Projects.Where(p => p.DiscoverySpecPath is not null))
        {
            var resolvedPath = manifestProvider.ResolveValidatedPath(project.DiscoverySpecPath!);
            if (File.Exists(resolvedPath))
            {
                _logger.LogDebug(
                    "Serving discovery-spec from manifest project {ProjectName}",
                    SanitizeForLog(project.ProjectName)
                );
                return File.OpenRead(resolvedPath);
            }
        }

        // No project provides a discovery spec: keep the legacy failure shape.
        var error = $"Couldn't load find the resource";
        _logger.LogCritical(error);
        throw new InvalidOperationException(error);
    }

    private Stream GetXsdStreamFromManifest(string fileNamePattern)
    {
        var bareFileName = NormalizeToBareXsdFileName(fileNamePattern);
        var manifest = manifestProvider.GetManifest();

        // Search core first, then extensions in manifest order; return the first matching file
        var matchedPath = manifest
            .Projects.OrderBy(p => p.IsExtensionProject ? 1 : 0)
            .SelectMany(manifestProvider.EnumerateValidatedXsdFiles)
            .FirstOrDefault(f =>
                Path.GetFileName(f).Equals(bareFileName, StringComparison.OrdinalIgnoreCase)
            );

        if (matchedPath is not null)
        {
            return File.OpenRead(matchedPath);
        }

        var error = $"Couldn't load find the resource";
        _logger.LogCritical(error);
        throw new InvalidOperationException(error);
    }

    private Stream GetXsdStreamFromManifest(string fileNamePattern, string section)
    {
        var bareFileName = NormalizeToBareXsdFileName(fileNamePattern);
        var matchedPath = ResolveXsdPathsForSection(section)
            .FirstOrDefault(f =>
                Path.GetFileName(f).Equals(bareFileName, StringComparison.OrdinalIgnoreCase)
            );

        if (matchedPath is not null)
        {
            return File.OpenRead(matchedPath);
        }

        var error = $"Couldn't load find the resource";
        _logger.LogCritical(error);
        throw new InvalidOperationException(error);
    }

    private IEnumerable<string> ResolveXsdPathsForSection(string section)
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

        var xsdPaths = manifestProvider.EnumerateValidatedXsdFiles(sectionProject).ToList();
        var coreProject = manifest.Projects.FirstOrDefault(p => !p.IsExtensionProject);

        if (coreProject is not null && !IsSameProject(sectionProject, coreProject))
        {
            xsdPaths.AddRange(manifestProvider.EnumerateValidatedXsdFiles(coreProject));
        }

        return xsdPaths;
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

    /// <summary>
    /// Normalizes a file-name argument to the staged bare XSD file name.
    /// Legacy assembly-resource-prefixed values look like:
    ///   EdFi.DataStandard52.ApiSchema.xsd.Ed-Fi-Core.xsd
    /// Staged bare names are like:
    ///   Ed-Fi-Core.xsd
    /// Strips everything up to and including the literal ".xsd." infix when present.
    /// </summary>
    private static string NormalizeToBareXsdFileName(string fileNamePattern)
    {
        const string xsdInfix = ".xsd.";
        var infixIndex = fileNamePattern.IndexOf(xsdInfix, StringComparison.OrdinalIgnoreCase);
        if (infixIndex >= 0)
        {
            return fileNamePattern[(infixIndex + xsdInfix.Length)..];
        }

        return fileNamePattern;
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
