// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Build;
using EdFi.DataManagementService.Backend.Tests.Common;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

internal static class RuntimePlanFixtureModelSetBuilder
{
    private const string ProjectFileName = "EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj";
    private const string RelationalMappingVersion = "1.0.0";
    private const string FixtureInputsPropertyName = "inputs";
    private const string FixtureInputsDirectoryName = "inputs";

    public static DerivedRelationalModelSet Build(string fixtureRelativePath, SqlDialect dialect)
    {
        return Build(fixtureRelativePath, dialect, reverseResourceSchemaOrder: false);
    }

    public static DerivedRelationalModelSet Build(
        string fixtureRelativePath,
        SqlDialect dialect,
        bool reverseResourceSchemaOrder
    )
    {
        return Build(
            fixtureRelativePath,
            dialect,
            reverseResourceSchemaOrder,
            reverseFixtureInputOrder: false
        );
    }

    public static DerivedRelationalModelSet Build(
        string fixtureRelativePath,
        SqlDialect dialect,
        bool reverseResourceSchemaOrder,
        bool reverseFixtureInputOrder
    )
    {
        var effectiveSchemaSet = LoadEffectiveSchemaSet(
            fixtureRelativePath,
            reverseResourceSchemaOrder,
            reverseFixtureInputOrder
        );
        return BuildDerivedModelSet(effectiveSchemaSet, dialect);
    }

    public static DerivedRelationalModelSet Build(
        IReadOnlyList<(string FixtureRelativePath, bool IsExtensionProject)> fixtureInputs,
        SqlDialect dialect
    )
    {
        return Build(
            fixtureInputs,
            dialect,
            reverseResourceSchemaOrder: false,
            reverseFixtureInputOrder: false
        );
    }

    public static DerivedRelationalModelSet Build(
        IReadOnlyList<(string FixtureRelativePath, bool IsExtensionProject)> fixtureInputs,
        SqlDialect dialect,
        bool reverseResourceSchemaOrder,
        bool reverseFixtureInputOrder
    )
    {
        ArgumentNullException.ThrowIfNull(fixtureInputs);

        var effectiveSchemaSet = LoadEffectiveSchemaSet(
            fixtureInputs,
            reverseResourceSchemaOrder,
            reverseFixtureInputOrder
        );

        return BuildDerivedModelSet(effectiveSchemaSet, dialect);
    }

    private static DerivedRelationalModelSet BuildDerivedModelSet(
        EffectiveSchemaSet effectiveSchemaSet,
        SqlDialect dialect
    )
    {
        ISqlDialectRules dialectRules = dialect switch
        {
            SqlDialect.Pgsql => new PgsqlDialectRules(),
            SqlDialect.Mssql => new MssqlDialectRules(),
            _ => throw new NotSupportedException($"Unsupported dialect '{dialect}'."),
        };

        return new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault()).Build(
            effectiveSchemaSet,
            dialect,
            dialectRules
        );
    }

    private static EffectiveSchemaSet LoadEffectiveSchemaSet(
        string fixtureRelativePath,
        bool reverseResourceSchemaOrder,
        bool reverseFixtureInputOrder
    )
    {
        var fixturePath = GetFixturePath(fixtureRelativePath);
        var root = ParseJsonObject(fixturePath, "Fixture");
        var fixtureProjects = LoadFixtureProjects(
            root,
            fixturePath,
            reverseResourceSchemaOrder,
            reverseFixtureInputOrder
        );
        return BuildEffectiveSchemaSet(fixtureProjects);
    }

    private static EffectiveSchemaSet LoadEffectiveSchemaSet(
        IReadOnlyList<(string FixtureRelativePath, bool IsExtensionProject)> fixtureInputs,
        bool reverseResourceSchemaOrder,
        bool reverseFixtureInputOrder
    )
    {
        if (fixtureInputs.Count == 0)
        {
            throw new InvalidOperationException(
                "Expected fixtureInputs to contain at least one ApiSchema input."
            );
        }

        var orderedFixtureInputs = reverseFixtureInputOrder
            ? fixtureInputs.Reverse().ToArray()
            : fixtureInputs.ToArray();
        List<FixtureProjectInput> fixtureProjects = new(orderedFixtureInputs.Length);

        foreach (var fixtureInput in orderedFixtureInputs)
        {
            var fixtureInputPath = GetFixturePath(fixtureInput.FixtureRelativePath);
            var fixtureInputRoot = ParseJsonObject(fixtureInputPath, "Fixture input");

            fixtureProjects.Add(
                LoadProjectInputFromApiSchemaRoot(
                    fixtureInputRoot,
                    reverseResourceSchemaOrder,
                    fixtureInput.IsExtensionProject
                )
            );
        }

        return BuildEffectiveSchemaSet(fixtureProjects);
    }

    private static EffectiveSchemaSet BuildEffectiveSchemaSet(
        IReadOnlyList<FixtureProjectInput> fixtureProjects
    )
    {
        var projectsInEndpointOrder = fixtureProjects
            .OrderBy(project => project.Project.ProjectEndpointName, StringComparer.Ordinal)
            .ToArray();
        var effectiveProjectsInEndpointOrder = projectsInEndpointOrder
            .Select(project => project.Project)
            .ToArray();
        var apiSchemaVersion = ResolveApiSchemaVersion(projectsInEndpointOrder);
        var resourceKeysInIdOrder = BuildResourceKeys(effectiveProjectsInEndpointOrder);
        var seedHash = BuildSeedHash(resourceKeysInIdOrder);
        var effectiveSchemaHash = BuildHashHex("effective-schema", seedHash);

        var schemaInfo = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: apiSchemaVersion,
            RelationalMappingVersion: RelationalMappingVersion,
            EffectiveSchemaHash: effectiveSchemaHash,
            ResourceKeyCount: checked((short)resourceKeysInIdOrder.Count),
            ResourceKeySeedHash: seedHash,
            SchemaComponentsInEndpointOrder: projectsInEndpointOrder
                .Select(project => new SchemaComponentInfo(
                    ProjectEndpointName: project.Project.ProjectEndpointName,
                    ProjectName: project.Project.ProjectName,
                    ProjectVersion: project.Project.ProjectVersion,
                    IsExtensionProject: project.Project.IsExtensionProject,
                    ProjectHash: project.ProjectHash
                ))
                .ToArray(),
            ResourceKeysInIdOrder: resourceKeysInIdOrder
        );

        return new EffectiveSchemaSet(schemaInfo, effectiveProjectsInEndpointOrder);
    }

    private static IReadOnlyList<FixtureProjectInput> LoadFixtureProjects(
        JsonObject root,
        string fixturePath,
        bool reverseResourceSchemaOrder,
        bool reverseFixtureInputOrder
    )
    {
        var hasProjectSchema = root.ContainsKey("projectSchema");
        var hasFixtureInputs = root.ContainsKey(FixtureInputsPropertyName);

        if (hasProjectSchema && hasFixtureInputs)
        {
            throw new InvalidOperationException(
                $"Fixture '{fixturePath}' cannot contain both projectSchema and {FixtureInputsPropertyName}."
            );
        }

        if (hasProjectSchema)
        {
            return [LoadProjectInputFromApiSchemaRoot(root, reverseResourceSchemaOrder, false)];
        }

        if (!hasFixtureInputs)
        {
            throw new InvalidOperationException(
                $"Fixture '{fixturePath}' must contain either projectSchema or {FixtureInputsPropertyName}."
            );
        }

        var fixtureInputEntries = ParseFixtureInputEntries(
            RequireArray(root[FixtureInputsPropertyName], FixtureInputsPropertyName)
        );

        if (reverseFixtureInputOrder)
        {
            fixtureInputEntries.Reverse();
        }

        List<FixtureProjectInput> projects = new(fixtureInputEntries.Count);

        foreach (var fixtureInputEntry in fixtureInputEntries)
        {
            var fixtureInputPath = GetFixtureInputPath(fixturePath, fixtureInputEntry.FileName);
            var fixtureInputRoot = ParseJsonObject(fixtureInputPath, "Fixture input");
            projects.Add(
                LoadProjectInputFromApiSchemaRoot(
                    fixtureInputRoot,
                    reverseResourceSchemaOrder,
                    fixtureInputEntry.IsExtensionProject
                )
            );
        }

        return projects;
    }

    private static string ResolveApiSchemaVersion(IReadOnlyList<FixtureProjectInput> projectsInEndpointOrder)
    {
        var apiSchemaVersions = projectsInEndpointOrder
            .Select(project => project.ApiSchemaVersion)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (apiSchemaVersions.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one apiSchemaVersion across fixture inputs, found {apiSchemaVersions.Length}."
            );
        }

        return apiSchemaVersions[0];
    }

    private static FixtureProjectInput LoadProjectInputFromApiSchemaRoot(
        JsonObject root,
        bool reverseResourceSchemaOrder,
        bool isExtensionProject
    )
    {
        var apiSchemaVersion = RequireString(root, "apiSchemaVersion");
        var projectSchema = PrepareProjectSchema(
            RequireObject(root["projectSchema"], "projectSchema"),
            reverseResourceSchemaOrder
        );
        var projectEndpointName = RequireString(projectSchema, "projectEndpointName");
        var projectName = RequireString(projectSchema, "projectName");
        var projectVersion = RequireString(projectSchema, "projectVersion");
        var projectHash = BuildHashHex("project", Encoding.UTF8.GetBytes(projectName));
        var effectiveProjectSchema = new EffectiveProjectSchema(
            projectEndpointName,
            projectName,
            projectVersion,
            isExtensionProject,
            projectSchema
        );

        return new FixtureProjectInput(apiSchemaVersion, effectiveProjectSchema, projectHash);
    }

    private static List<FixtureInputEntry> ParseFixtureInputEntries(JsonArray fixtureInputs)
    {
        if (fixtureInputs.Count == 0)
        {
            throw new InvalidOperationException(
                "Expected inputs to contain at least one fixture input entry."
            );
        }

        List<FixtureInputEntry> entries = new(fixtureInputs.Count);

        for (var i = 0; i < fixtureInputs.Count; i++)
        {
            var entryNode = fixtureInputs[i];
            var entryObject = RequireObject(entryNode, $"inputs[{i}]");
            entries.Add(
                new FixtureInputEntry(
                    RequireString(entryObject, "fileName"),
                    RequireBool(entryObject, "isExtensionProject")
                )
            );
        }

        return entries;
    }

    private static string GetFixtureInputPath(string fixturePath, string fixtureInputFileName)
    {
        var fixtureDirectory = Path.GetDirectoryName(fixturePath);

        if (fixtureDirectory is null)
        {
            throw new InvalidOperationException($"Fixture '{fixturePath}' directory could not be resolved.");
        }

        var path = Path.Combine(fixtureDirectory, FixtureInputsDirectoryName, fixtureInputFileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fixture input not found: {path}", path);
        }

        return path;
    }

    private static JsonObject ParseJsonObject(string path, string description)
    {
        var rootNode = JsonNode.Parse(File.ReadAllText(path));

        if (rootNode is not JsonObject root)
        {
            throw new InvalidOperationException($"{description} '{path}' parsed null or non-object.");
        }

        return root;
    }

    private static JsonObject PrepareProjectSchema(JsonObject projectSchema, bool reverseResourceSchemaOrder)
    {
        var schemaClone = (JsonObject)projectSchema.DeepClone();

        if (!reverseResourceSchemaOrder)
        {
            return schemaClone;
        }

        var resourceSchemas = RequireObject(schemaClone["resourceSchemas"], "projectSchema.resourceSchemas");
        JsonObject reversedResourceSchemas = [];

        foreach (var entry in resourceSchemas.Reverse())
        {
            if (entry.Value is null)
            {
                throw new InvalidOperationException(
                    "Expected projectSchema.resourceSchemas entries to be non-null, invalid ApiSchema."
                );
            }

            reversedResourceSchemas.Add(entry.Key, entry.Value.DeepClone());
        }

        schemaClone["resourceSchemas"] = reversedResourceSchemas;

        return schemaClone;
    }

    private static string GetFixturePath(string fixtureRelativePath)
    {
        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            ProjectFileName
        );
        var path = Path.Combine(projectRoot, fixtureRelativePath);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fixture not found: {path}", path);
        }

        return path;
    }

    private static IReadOnlyList<ResourceKeyEntry> BuildResourceKeys(
        IReadOnlyList<EffectiveProjectSchema> projectsInEndpointOrder
    )
    {
        List<FixtureResourceKeySeed> seeds = [];

        foreach (var project in projectsInEndpointOrder)
        {
            var projectSchema =
                project.ProjectSchema
                ?? throw new InvalidOperationException("Expected ProjectSchema to be provided.");

            AddResourceKeySeeds(
                seeds,
                RequireObject(projectSchema["resourceSchemas"], "projectSchema.resourceSchemas"),
                project.ProjectName,
                project.ProjectVersion,
                isAbstractResource: false,
                resourceSchemasPath: "projectSchema.resourceSchemas"
            );

            if (projectSchema["abstractResources"] is JsonObject abstractResources)
            {
                AddResourceKeySeeds(
                    seeds,
                    abstractResources,
                    project.ProjectName,
                    project.ProjectVersion,
                    isAbstractResource: true,
                    resourceSchemasPath: "projectSchema.abstractResources"
                );
            }
        }

        var orderedSeeds = seeds
            .OrderBy(seed => seed.Resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(seed => seed.Resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        for (var i = 1; i < orderedSeeds.Length; i++)
        {
            if (
                string.Equals(
                    orderedSeeds[i].Resource.ProjectName,
                    orderedSeeds[i - 1].Resource.ProjectName,
                    StringComparison.Ordinal
                )
                && string.Equals(
                    orderedSeeds[i].Resource.ResourceName,
                    orderedSeeds[i - 1].Resource.ResourceName,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidOperationException(
                    $"Duplicate resource key seed detected: ({orderedSeeds[i].Resource.ProjectName}, {orderedSeeds[i].Resource.ResourceName}). "
                        + "Each (ProjectName, ResourceName) pair must be unique in the fixture schema."
                );
            }
        }

        List<ResourceKeyEntry> keys = new(orderedSeeds.Length);
        short nextId = 1;

        foreach (var seed in orderedSeeds)
        {
            keys.Add(
                new ResourceKeyEntry(
                    ResourceKeyId: nextId,
                    Resource: seed.Resource,
                    ResourceVersion: seed.ResourceVersion,
                    IsAbstractResource: seed.IsAbstractResource
                )
            );
            nextId++;
        }

        return keys;
    }

    private static void AddResourceKeySeeds(
        ICollection<FixtureResourceKeySeed> seeds,
        JsonObject resourceSchemas,
        string projectName,
        string projectVersion,
        bool isAbstractResource,
        string resourceSchemasPath
    )
    {
        foreach (var resourceSchemaEntry in resourceSchemas)
        {
            if (resourceSchemaEntry.Value is null)
            {
                throw new InvalidOperationException(
                    $"Expected {resourceSchemasPath} entries to be non-null, invalid ApiSchema."
                );
            }

            if (resourceSchemaEntry.Value is not JsonObject resourceSchema)
            {
                throw new InvalidOperationException(
                    $"Expected {resourceSchemasPath} entries to be objects, invalid ApiSchema."
                );
            }

            var resourceName = ResolveResourceName(resourceSchemaEntry.Key, resourceSchema);
            seeds.Add(
                new FixtureResourceKeySeed(
                    new QualifiedResourceName(projectName, resourceName),
                    projectVersion,
                    isAbstractResource
                )
            );
        }
    }

    private static string ResolveResourceName(string endpointName, JsonObject resourceSchema)
    {
        if (!resourceSchema.TryGetPropertyValue("resourceName", out var resourceNameNode))
        {
            return RequireNonEmpty(endpointName, "resourceName");
        }

        return resourceNameNode switch
        {
            JsonValue jsonValue => RequireNonEmpty(jsonValue.GetValue<string>(), "resourceName"),
            null => throw new InvalidOperationException(
                "Expected resourceName to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                "Expected resourceName to be a string, invalid ApiSchema."
            ),
        };
    }

    private static byte[] BuildSeedHash(IReadOnlyList<ResourceKeyEntry> resourceKeysInIdOrder)
    {
        var seedInput = string.Join(
            '\n',
            resourceKeysInIdOrder.Select(resource =>
                $"{resource.ResourceKeyId}|{resource.Resource.ProjectName}|{resource.Resource.ResourceName}|"
                + $"{resource.ResourceVersion}|{resource.IsAbstractResource}"
            )
        );

        return SHA256.HashData(Encoding.UTF8.GetBytes(seedInput));
    }

    private static string BuildHashHex(string prefix, byte[] seed)
    {
        var input = $"{prefix}:{Convert.ToHexString(seed)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static JsonObject RequireObject(JsonNode? node, string propertyName)
    {
        return node switch
        {
            JsonObject jsonObject => jsonObject,
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be an object, invalid ApiSchema."
            ),
        };
    }

    private static JsonArray RequireArray(JsonNode? node, string propertyName)
    {
        return node switch
        {
            JsonArray jsonArray => jsonArray,
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be an array, invalid ApiSchema."
            ),
        };
    }

    private static string RequireString(JsonObject node, string propertyName)
    {
        var value = node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be a string, invalid ApiSchema."
            ),
        };

        return RequireNonEmpty(value, propertyName);
    }

    private static bool RequireBool(JsonObject node, string propertyName)
    {
        return node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<bool>(),
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be a bool, invalid ApiSchema."
            ),
        };
    }

    private static string RequireNonEmpty(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Expected {propertyName} to be non-empty, invalid ApiSchema."
            );
        }

        return value;
    }

    private sealed record FixtureInputEntry(string FileName, bool IsExtensionProject);

    private sealed record FixtureProjectInput(
        string ApiSchemaVersion,
        EffectiveProjectSchema Project,
        string ProjectHash
    );

    private sealed record FixtureResourceKeySeed(
        QualifiedResourceName Resource,
        string ResourceVersion,
        bool IsAbstractResource
    );
}
