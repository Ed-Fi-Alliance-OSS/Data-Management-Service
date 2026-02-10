// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test type effective schema set fixture builder.
/// </summary>
internal static class EffectiveSchemaSetFixtureBuilder
{
    private const string DefaultApiSchemaFormatVersion = "1.0.0";
    private const string DefaultRelationalMappingVersion = "1.0.0";
    private const string DefaultEffectiveSchemaHash = "deadbeef";
    private static readonly byte[] DefaultResourceKeySeedHash = { 0x01 };

    /// <summary>
    /// Create hand authored effective schema set.
    /// </summary>
    public static EffectiveSchemaSet CreateHandAuthoredEffectiveSchemaSet(
        bool reverseProjectOrder = false,
        bool reverseResourceOrder = false
    )
    {
        var coreProjectSchema = LoadProjectSchema("hand-authored-core-api-schema.json", reverseResourceOrder);
        var extensionProjectSchema = LoadProjectSchema(
            "hand-authored-extension-api-schema.json",
            reverseResourceOrder
        );

        var coreProject = CreateEffectiveProjectSchema(coreProjectSchema, false);
        var extensionProject = CreateEffectiveProjectSchema(extensionProjectSchema, true);

        EffectiveProjectSchema[] projects = reverseProjectOrder
            ? [extensionProject, coreProject]
            : [coreProject, extensionProject];

        return CreateEffectiveSchemaSet(projects);
    }

    /// <summary>
    /// Create an effective schema set from a single fixture file.
    /// </summary>
    public static EffectiveSchemaSet CreateEffectiveSchemaSetFromFixture(
        string fileName,
        bool isExtensionProject = false,
        bool reverseResourceOrder = false
    )
    {
        var projectSchema = LoadProjectSchema(fileName, reverseResourceOrder);
        var project = CreateEffectiveProjectSchema(projectSchema, isExtensionProject);

        return CreateEffectiveSchemaSet(new[] { project });
    }

    /// <summary>
    /// Create an effective schema set from multiple fixture files.
    /// </summary>
    public static EffectiveSchemaSet CreateEffectiveSchemaSetFromFixtures(
        IReadOnlyList<(string FileName, bool IsExtensionProject)> fixtures,
        bool reverseProjectOrder = false,
        bool reverseResourceOrder = false
    )
    {
        ArgumentNullException.ThrowIfNull(fixtures);

        if (fixtures.Count == 0)
        {
            throw new ArgumentException("At least one fixture must be provided.", nameof(fixtures));
        }

        List<EffectiveProjectSchema> projects = new(fixtures.Count);

        foreach (var fixture in fixtures)
        {
            var projectSchema = LoadProjectSchema(fixture.FileName, reverseResourceOrder);
            var project = CreateEffectiveProjectSchema(projectSchema, fixture.IsExtensionProject);
            projects.Add(project);
        }

        if (reverseProjectOrder)
        {
            projects.Reverse();
        }

        return CreateEffectiveSchemaSet(projects);
    }

    /// <summary>
    /// Create effective schema set.
    /// </summary>
    public static EffectiveSchemaSet CreateEffectiveSchemaSet(IReadOnlyList<EffectiveProjectSchema> projects)
    {
        var schemaComponents = projects
            .OrderBy(project => project.ProjectEndpointName, StringComparer.Ordinal)
            .Select(project => new SchemaComponentInfo(
                project.ProjectEndpointName,
                project.ProjectName,
                project.ProjectVersion,
                project.IsExtensionProject
            ))
            .ToArray();

        var resourceKeys = BuildResourceKeyEntries(projects);

        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            DefaultApiSchemaFormatVersion,
            DefaultRelationalMappingVersion,
            DefaultEffectiveSchemaHash,
            resourceKeys.Count,
            DefaultResourceKeySeedHash,
            schemaComponents,
            resourceKeys
        );

        return new EffectiveSchemaSet(effectiveSchemaInfo, projects);
    }

    /// <summary>
    /// Create effective project schema.
    /// </summary>
    public static EffectiveProjectSchema CreateEffectiveProjectSchema(
        JsonObject projectSchema,
        bool isExtensionProject
    )
    {
        ArgumentNullException.ThrowIfNull(projectSchema);

        var detachedSchema = projectSchema.DeepClone();

        if (detachedSchema is not JsonObject detachedObject)
        {
            throw new InvalidOperationException("ProjectSchema must be an object.");
        }

        var projectName = RequireString(detachedObject, "projectName");
        var projectEndpointName = RequireString(detachedObject, "projectEndpointName");
        var projectVersion = RequireString(detachedObject, "projectVersion");

        return new EffectiveProjectSchema(
            projectEndpointName,
            projectName,
            projectVersion,
            isExtensionProject,
            detachedObject
        );
    }

    /// <summary>
    /// Build resource key entries.
    /// </summary>
    private static IReadOnlyList<ResourceKeyEntry> BuildResourceKeyEntries(
        IReadOnlyList<EffectiveProjectSchema> projects
    )
    {
        List<ResourceKeySeed> seeds = [];

        foreach (var project in projects)
        {
            var projectSchema =
                project.ProjectSchema
                ?? throw new InvalidOperationException("ProjectSchema must be provided.");

            AddResourceKeySeeds(
                seeds,
                RequireObject(projectSchema["resourceSchemas"], "projectSchema.resourceSchemas"),
                project.ProjectName,
                project.ProjectVersion,
                false,
                "projectSchema.resourceSchemas"
            );

            if (projectSchema["abstractResources"] is JsonObject abstractResources)
            {
                AddResourceKeySeeds(
                    seeds,
                    abstractResources,
                    project.ProjectName,
                    project.ProjectVersion,
                    true,
                    "projectSchema.abstractResources"
                );
            }
        }

        var orderedSeeds = seeds
            .OrderBy(seed => seed.Resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(seed => seed.Resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        List<ResourceKeyEntry> entries = new(orderedSeeds.Length);
        short nextId = 1;

        foreach (var seed in orderedSeeds)
        {
            entries.Add(
                new ResourceKeyEntry(nextId, seed.Resource, seed.ResourceVersion, seed.IsAbstractResource)
            );
            nextId++;
        }

        return entries;
    }

    /// <summary>
    /// Add resource key seeds.
    /// </summary>
    private static void AddResourceKeySeeds(
        ICollection<ResourceKeySeed> seeds,
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

            if (string.IsNullOrWhiteSpace(resourceSchemaEntry.Key))
            {
                throw new InvalidOperationException(
                    "Expected resource schema entry key to be non-empty, invalid ApiSchema."
                );
            }

            var resourceName = GetResourceName(resourceSchemaEntry.Key, resourceSchema);

            seeds.Add(
                new ResourceKeySeed(
                    new QualifiedResourceName(projectName, resourceName),
                    projectVersion,
                    isAbstractResource
                )
            );
        }
    }

    /// <summary>
    /// Load project schema.
    /// </summary>
    private static JsonObject LoadProjectSchema(string fileName, bool reverseResourceOrder)
    {
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "set-builder",
            fileName
        );

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fixture not found: {path}", path);
        }

        var root = JsonNode.Parse(File.ReadAllText(path));

        if (root is not JsonObject rootObject)
        {
            throw new InvalidOperationException($"Fixture {fileName} parsed null or non-object.");
        }

        var projectSchema = RequireObject(rootObject["projectSchema"], "projectSchema");

        return reverseResourceOrder ? ReverseResourceSchemas(projectSchema) : projectSchema;
    }

    /// <summary>
    /// Reverse resource schemas.
    /// </summary>
    private static JsonObject ReverseResourceSchemas(JsonObject projectSchema)
    {
        var resourceSchemas = RequireObject(
            projectSchema["resourceSchemas"],
            "projectSchema.resourceSchemas"
        );

        JsonObject reordered = new();

        foreach (
            var resource in resourceSchemas.OrderByDescending(entry => entry.Key, StringComparer.Ordinal)
        )
        {
            if (resource.Value is null)
            {
                throw new InvalidOperationException(
                    "Expected projectSchema.resourceSchemas entries to be non-null, invalid ApiSchema."
                );
            }

            reordered[resource.Key] = resource.Value.DeepClone();
        }

        projectSchema["resourceSchemas"] = reordered;

        return projectSchema;
    }

    /// <summary>
    /// Test type resource key seed.
    /// </summary>
    private sealed record ResourceKeySeed(
        QualifiedResourceName Resource,
        string ResourceVersion,
        bool IsAbstractResource
    );
}
