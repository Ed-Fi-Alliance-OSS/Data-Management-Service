// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DataManagementService.Core.ApiSchema;

public sealed class ApiSchemaAssetManifestException(
    string failureType,
    string message,
    Exception? innerException = null
) : InvalidOperationException(message, innerException)
{
    public string FailureType { get; } = failureType;
}

/// <summary>
/// Reads and validates the root bootstrap ApiSchema asset manifest contract.
/// </summary>
public static class ApiSchemaAssetManifestReader
{
    public const string ManifestFileName = "bootstrap-api-schema-manifest.json";
    public const int SupportedManifestVersion = 1;

    public static ApiSchemaAssetManifest ReadFromWorkspace(string workspaceRoot)
    {
        var manifestPath = Path.Combine(Path.GetFullPath(workspaceRoot), ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new ApiSchemaAssetManifestException(
                "Configuration",
                $"Bootstrap manifest file '{ManifestFileName}' was not found under the configured "
                    + $"ApiSchema workspace. Expected path: {manifestPath}."
            );
        }

        return ReadFromFile(workspaceRoot, manifestPath);
    }

    public static ApiSchemaAssetManifest ReadFromFile(string workspaceRoot, string manifestPath)
    {
        try
        {
            return ReadFromJson(File.ReadAllText(manifestPath), workspaceRoot);
        }
        catch (IOException ex)
        {
            throw new ApiSchemaAssetManifestException(
                "FileSystem",
                $"Error reading bootstrap manifest file '{manifestPath}'",
                ex
            );
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ApiSchemaAssetManifestException(
                "AccessDenied",
                $"Access denied to bootstrap manifest file '{manifestPath}'",
                ex
            );
        }
    }

    public static ApiSchemaAssetManifest ReadFromJson(string json, string workspaceRoot)
    {
        using JsonDocument document = ParseManifest(json);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Null)
        {
            throw new ApiSchemaAssetManifestException(
                "Configuration",
                $"Bootstrap manifest file '{ManifestFileName}' deserializes to null. "
                    + "The file may be empty or contain only a JSON null literal."
            );
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ApiSchemaAssetManifestException(
                "Configuration",
                $"Bootstrap manifest file '{ManifestFileName}' must contain a JSON object."
            );
        }

        var version = ReadManifestVersion(root);
        if (version != SupportedManifestVersion)
        {
            throw new ApiSchemaAssetManifestException(
                "Configuration",
                $"Bootstrap manifest version {version} is not supported. "
                    + $"Only version {SupportedManifestVersion} is accepted."
            );
        }

        if (
            !TryGetPropertyCaseInsensitive(root, "projects", out var projectsElement)
            || projectsElement.ValueKind != JsonValueKind.Array
            || projectsElement.GetArrayLength() == 0
        )
        {
            throw new ApiSchemaAssetManifestException(
                "Configuration",
                $"Bootstrap manifest '{ManifestFileName}' contains zero projects. "
                    + "A valid staged workspace must declare at least one project."
            );
        }

        List<ApiSchemaProject> projects = [];
        foreach (
            (JsonElement projectElement, int index) in projectsElement
                .EnumerateArray()
                .Select((p, i) => (p, i))
        )
        {
            projects.Add(ReadProject(projectElement, index));
        }

        ValidateSingleCoreProject(projects);
        ValidateManifestPaths(projects, workspaceRoot);

        return new ApiSchemaAssetManifest(version, projects);
    }

    public static string DescribeProject(ApiSchemaProject project, int index)
    {
        if (!string.IsNullOrWhiteSpace(project.ProjectName))
        {
            return $"'{project.ProjectName}' at index {index}";
        }

        if (!string.IsNullOrWhiteSpace(project.ProjectEndpointName))
        {
            return $"'{project.ProjectEndpointName}' at index {index}";
        }

        return $"at index {index}";
    }

    private static JsonDocument ParseManifest(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ApiSchemaAssetManifestException(
                "ParseError",
                $"Bootstrap manifest file '{ManifestFileName}' contains malformed JSON: {ex.Message}",
                ex
            );
        }
    }

    private static int ReadManifestVersion(JsonElement root)
    {
        if (
            TryGetPropertyCaseInsensitive(root, "version", out var versionElement)
            && versionElement.ValueKind == JsonValueKind.Number
            && versionElement.TryGetInt32(out int version)
        )
        {
            return version;
        }

        return 0;
    }

    private static ApiSchemaProject ReadProject(JsonElement projectElement, int index)
    {
        if (projectElement.ValueKind == JsonValueKind.Null)
        {
            throw new ApiSchemaAssetManifestException(
                "Configuration",
                $"Bootstrap manifest '{ManifestFileName}' contains a null project entry at index {index}."
            );
        }

        if (projectElement.ValueKind != JsonValueKind.Object)
        {
            throw new ApiSchemaAssetManifestException(
                "Configuration",
                $"Bootstrap manifest '{ManifestFileName}' contains a non-object project entry at index {index}."
            );
        }

        return new ApiSchemaProject(
            ReadRequiredString(projectElement, "projectName", index),
            ReadRequiredString(projectElement, "projectEndpointName", index),
            ReadRequiredBoolean(projectElement, index),
            ReadRequiredString(projectElement, "schemaPath", index),
            ReadOptionalString(projectElement, "discoverySpecPath", index),
            ReadOptionalString(projectElement, "xsdDirectory", index)
        );
    }

    private static string ReadRequiredString(JsonElement projectElement, string fieldName, int index)
    {
        if (
            !TryGetPropertyCaseInsensitive(projectElement, fieldName, out var valueElement)
            || valueElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(valueElement.GetString())
        )
        {
            throw new ApiSchemaAssetManifestException(
                "Configuration",
                $"Bootstrap manifest project {DescribeRawProject(projectElement, index)} must declare a "
                    + $"non-empty {fieldName}."
            );
        }

        return valueElement.GetString()!;
    }

    private static bool ReadRequiredBoolean(JsonElement projectElement, int index)
    {
        if (
            !TryGetPropertyCaseInsensitive(projectElement, "isExtensionProject", out var valueElement)
            || valueElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
        )
        {
            throw new ApiSchemaAssetManifestException(
                "Configuration",
                $"Bootstrap manifest project {DescribeRawProject(projectElement, index)} must declare "
                    + "isExtensionProject as a non-null boolean."
            );
        }

        return valueElement.GetBoolean();
    }

    private static string? ReadOptionalString(JsonElement projectElement, string fieldName, int index)
    {
        if (!TryGetPropertyCaseInsensitive(projectElement, fieldName, out var valueElement))
        {
            return null;
        }

        if (valueElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (
            valueElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(valueElement.GetString())
        )
        {
            throw new ApiSchemaAssetManifestException(
                "Configuration",
                $"Bootstrap manifest project {DescribeRawProject(projectElement, index)} declares {fieldName}, "
                    + "but it must be null, omitted, or a non-empty manifest-relative path."
            );
        }

        return valueElement.GetString();
    }

    private static void ValidateSingleCoreProject(IReadOnlyList<ApiSchemaProject> projects)
    {
        var coreProjectCount = projects.Count(p => !p.IsExtensionProject);

        if (coreProjectCount != 1)
        {
            throw new ApiSchemaAssetManifestException(
                "Configuration",
                $"Bootstrap manifest '{ManifestFileName}' must declare exactly one core project where "
                    + $"isExtensionProject is false; found {coreProjectCount}."
            );
        }
    }

    private static void ValidateManifestPaths(IReadOnlyList<ApiSchemaProject> projects, string workspaceRoot)
    {
        var pathResolver = new ApiSchemaWorkspacePathResolver(workspaceRoot);
        foreach ((ApiSchemaProject project, int index) in projects.Select((p, i) => (p, i)))
        {
            ValidateManifestRelativePath(pathResolver, project.SchemaPath, "schemaPath", project, index);
            ValidateOptionalManifestRelativePath(
                pathResolver,
                project.DiscoverySpecPath,
                "discoverySpecPath",
                project,
                index
            );
            ValidateOptionalManifestRelativePath(
                pathResolver,
                project.XsdDirectory,
                "xsdDirectory",
                project,
                index
            );
        }
    }

    private static void ValidateOptionalManifestRelativePath(
        ApiSchemaWorkspacePathResolver pathResolver,
        string? value,
        string fieldName,
        ApiSchemaProject project,
        int index
    )
    {
        if (value is null)
        {
            return;
        }

        ValidateManifestRelativePath(pathResolver, value, fieldName, project, index);
    }

    private static void ValidateManifestRelativePath(
        ApiSchemaWorkspacePathResolver pathResolver,
        string value,
        string fieldName,
        ApiSchemaProject project,
        int index
    )
    {
        try
        {
            pathResolver.ResolveManifestRelativePath(value);
        }
        catch (InvalidOperationException ex)
        {
            throw new ApiSchemaAssetManifestException(
                "Configuration",
                $"Bootstrap manifest project {DescribeProject(project, index)} has invalid "
                    + $"{fieldName} '{value}': {ex.Message}",
                ex
            );
        }
    }

    private static string DescribeRawProject(JsonElement projectElement, int index)
    {
        if (
            TryGetPropertyCaseInsensitive(projectElement, "projectName", out var projectNameElement)
            && projectNameElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(projectNameElement.GetString())
        )
        {
            return $"'{projectNameElement.GetString()}' at index {index}";
        }

        if (
            TryGetPropertyCaseInsensitive(projectElement, "projectEndpointName", out var endpointElement)
            && endpointElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(endpointElement.GetString())
        )
        {
            return $"'{endpointElement.GetString()}' at index {index}";
        }

        return $"at index {index}";
    }

    private static bool TryGetPropertyCaseInsensitive(
        JsonElement element,
        string propertyName,
        out JsonElement value
    )
    {
        foreach (var property in element.EnumerateObject())
        {
            if (
                property.NameEquals(propertyName)
                || property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
            )
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
