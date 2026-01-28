// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel;

internal static class RelationalModelSetValidation
{
    internal sealed record EffectiveSchemaResourceIndex(
        IReadOnlySet<QualifiedResourceName> Resources,
        IReadOnlyDictionary<QualifiedResourceName, bool> IsAbstractByResource
    );

    internal static EffectiveSchemaResourceIndex BuildEffectiveSchemaResourceIndex(
        EffectiveSchemaSet effectiveSchemaSet
    )
    {
        if (effectiveSchemaSet.ProjectsInEndpointOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaSet.ProjectsInEndpointOrder must be provided."
            );
        }

        HashSet<QualifiedResourceName> resources = new();
        Dictionary<QualifiedResourceName, bool> isAbstractByResource = new();

        foreach (var project in effectiveSchemaSet.ProjectsInEndpointOrder)
        {
            if (project is null)
            {
                throw new InvalidOperationException(
                    "EffectiveSchemaSet.ProjectsInEndpointOrder must not contain null entries."
                );
            }

            if (project.ProjectSchema is null)
            {
                throw new InvalidOperationException(
                    "Expected projectSchema to be present in EffectiveProjectSchema."
                );
            }

            var projectName = RequireNonEmpty(project.ProjectName, "ProjectName");
            var resourceSchemas = RequireObject(
                project.ProjectSchema["resourceSchemas"],
                "projectSchema.resourceSchemas"
            );

            AddResourceEntries(
                resources,
                isAbstractByResource,
                resourceSchemas,
                projectName,
                "projectSchema.resourceSchemas",
                isAbstract: false
            );

            if (project.ProjectSchema["abstractResources"] is JsonObject abstractResources)
            {
                AddResourceEntries(
                    resources,
                    isAbstractByResource,
                    abstractResources,
                    projectName,
                    "projectSchema.abstractResources",
                    isAbstract: true
                );
            }
        }

        return new EffectiveSchemaResourceIndex(resources, isAbstractByResource);
    }

    internal static void ValidateEffectiveSchemaInfo(
        EffectiveSchemaSet effectiveSchemaSet,
        EffectiveSchemaResourceIndex effectiveResources
    )
    {
        if (effectiveSchemaSet.EffectiveSchema is null)
        {
            throw new InvalidOperationException("EffectiveSchemaSet.EffectiveSchema must be provided.");
        }

        ValidateSchemaComponentsInEndpointOrder(effectiveSchemaSet);

        if (effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaInfo.ResourceKeysInIdOrder must be provided."
            );
        }

        if (
            effectiveSchemaSet.EffectiveSchema.ResourceKeyCount
            != effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.Count
        )
        {
            throw new InvalidOperationException(
                $"EffectiveSchemaInfo.ResourceKeyCount ({effectiveSchemaSet.EffectiveSchema.ResourceKeyCount}) "
                    + "does not match ResourceKeysInIdOrder count "
                    + $"({effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.Count})."
            );
        }

        if (effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.Any(entry => entry is null))
        {
            throw new InvalidOperationException(
                "EffectiveSchemaInfo.ResourceKeysInIdOrder must not contain null entries."
            );
        }

        var duplicateIds = effectiveSchemaSet
            .EffectiveSchema.ResourceKeysInIdOrder.GroupBy(entry => entry.ResourceKeyId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id)
            .ToArray();

        var duplicateResources = effectiveSchemaSet
            .EffectiveSchema.ResourceKeysInIdOrder.GroupBy(entry => entry.Resource)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(resource => resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        if (duplicateIds.Length > 0 || duplicateResources.Length > 0)
        {
            List<string> messageParts = new();

            if (duplicateIds.Length > 0)
            {
                messageParts.Add(
                    "Duplicate ResourceKeyId values detected: " + string.Join(", ", duplicateIds)
                );
            }

            if (duplicateResources.Length > 0)
            {
                messageParts.Add(
                    "Duplicate resource keys detected for: "
                        + string.Join(", ", duplicateResources.Select(FormatResource))
                );
            }

            throw new InvalidOperationException(string.Join(" ", messageParts));
        }

        var resourceKeysByResource = effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.ToDictionary(
            entry => entry.Resource
        );

        var missingResources = effectiveResources
            .Resources.Where(resource => !resourceKeysByResource.ContainsKey(resource))
            .OrderBy(resource => resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        var extraResources = resourceKeysByResource
            .Where(entry => !effectiveResources.Resources.Contains(entry.Key))
            .Select(entry => entry.Key)
            .OrderBy(resource => resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        if (missingResources.Length > 0 || extraResources.Length > 0)
        {
            List<string> messageParts = new();

            if (missingResources.Length > 0)
            {
                messageParts.Add(
                    "Missing resource keys for: " + string.Join(", ", missingResources.Select(FormatResource))
                );
            }

            if (extraResources.Length > 0)
            {
                messageParts.Add(
                    "Resource keys reference unknown resources: "
                        + string.Join(", ", extraResources.Select(FormatResource))
                );
            }

            throw new InvalidOperationException(string.Join(" ", messageParts));
        }

        ValidateResourceKeyAbstractness(
            effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder,
            effectiveResources.IsAbstractByResource
        );
    }

    private static void ValidateSchemaComponentsInEndpointOrder(EffectiveSchemaSet effectiveSchemaSet)
    {
        if (effectiveSchemaSet.EffectiveSchema.SchemaComponentsInEndpointOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaInfo.SchemaComponentsInEndpointOrder must be provided."
            );
        }

        if (effectiveSchemaSet.ProjectsInEndpointOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaSet.ProjectsInEndpointOrder must be provided."
            );
        }

        if (
            effectiveSchemaSet.EffectiveSchema.SchemaComponentsInEndpointOrder.Any(component =>
                component is null
            )
        )
        {
            throw new InvalidOperationException(
                "EffectiveSchemaInfo.SchemaComponentsInEndpointOrder must not contain null entries."
            );
        }

        var schemaComponentEndpointNames = effectiveSchemaSet
            .EffectiveSchema.SchemaComponentsInEndpointOrder.Select(component =>
                RequireNonEmpty(
                    component.ProjectEndpointName,
                    "SchemaComponentsInEndpointOrder.ProjectEndpointName"
                )
            )
            .ToArray();

        var duplicateSchemaComponents = schemaComponentEndpointNames
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (duplicateSchemaComponents.Length > 0)
        {
            throw new InvalidOperationException(
                "SchemaComponentsInEndpointOrder contains duplicate ProjectEndpointName values: "
                    + string.Join(", ", duplicateSchemaComponents)
            );
        }

        for (var index = 1; index < schemaComponentEndpointNames.Length; index++)
        {
            if (
                StringComparer.Ordinal.Compare(
                    schemaComponentEndpointNames[index - 1],
                    schemaComponentEndpointNames[index]
                ) > 0
            )
            {
                throw new InvalidOperationException(
                    "SchemaComponentsInEndpointOrder is not sorted by ProjectEndpointName: "
                        + $"'{schemaComponentEndpointNames[index - 1]}' appears before "
                        + $"'{schemaComponentEndpointNames[index]}'."
                );
            }
        }

        var projectEndpointNames = effectiveSchemaSet
            .ProjectsInEndpointOrder.Select(project =>
                RequireNonEmpty(project.ProjectEndpointName, "ProjectEndpointName")
            )
            .ToArray();

        var schemaComponentSet = new HashSet<string>(schemaComponentEndpointNames, StringComparer.Ordinal);
        var projectSet = new HashSet<string>(projectEndpointNames, StringComparer.Ordinal);

        var missing = projectSet
            .Except(schemaComponentSet)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var extra = schemaComponentSet
            .Except(projectSet)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (missing.Length == 0 && extra.Length == 0)
        {
            return;
        }

        List<string> messageParts = new();

        if (missing.Length > 0)
        {
            messageParts.Add(
                "SchemaComponentsInEndpointOrder is missing projects: " + string.Join(", ", missing)
            );
        }

        if (extra.Length > 0)
        {
            messageParts.Add(
                "SchemaComponentsInEndpointOrder contains unknown projects: " + string.Join(", ", extra)
            );
        }

        throw new InvalidOperationException(string.Join(" ", messageParts));
    }

    internal static void ValidateDocumentPathsMappingTargets(
        EffectiveSchemaSet effectiveSchemaSet,
        IReadOnlySet<QualifiedResourceName> effectiveResources
    )
    {
        if (effectiveSchemaSet.ProjectsInEndpointOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaSet.ProjectsInEndpointOrder must be provided."
            );
        }

        foreach (var project in effectiveSchemaSet.ProjectsInEndpointOrder)
        {
            if (project is null)
            {
                throw new InvalidOperationException(
                    "EffectiveSchemaSet.ProjectsInEndpointOrder must not contain null entries."
                );
            }

            if (project.ProjectSchema is null)
            {
                throw new InvalidOperationException(
                    "Expected projectSchema to be present in EffectiveProjectSchema."
                );
            }

            var projectName = RequireNonEmpty(project.ProjectName, "ProjectName");
            var resourceSchemas = RequireObject(
                project.ProjectSchema["resourceSchemas"],
                "projectSchema.resourceSchemas"
            );

            ValidateDocumentPathsMappingTargetsForResourceSchemas(
                projectName,
                resourceSchemas,
                effectiveResources,
                "projectSchema.resourceSchemas"
            );

            if (project.ProjectSchema["abstractResources"] is JsonObject abstractResources)
            {
                ValidateDocumentPathsMappingTargetsForResourceSchemas(
                    projectName,
                    abstractResources,
                    effectiveResources,
                    "projectSchema.abstractResources"
                );
            }
        }
    }

    private static void ValidateDocumentPathsMappingTargetsForResourceSchemas(
        string projectName,
        JsonObject resourceSchemas,
        IReadOnlySet<QualifiedResourceName> effectiveResources,
        string resourceSchemasPath
    )
    {
        foreach (var resourceSchemaEntry in OrderResourceSchemas(resourceSchemas, resourceSchemasPath))
        {
            ValidateDocumentPathsMappingTargetsForResource(
                projectName,
                resourceSchemaEntry.ResourceName,
                resourceSchemaEntry.ResourceSchema,
                effectiveResources
            );
        }
    }

    private static void ValidateDocumentPathsMappingTargetsForResource(
        string projectName,
        string resourceName,
        JsonObject resourceSchema,
        IReadOnlySet<QualifiedResourceName> effectiveResources
    )
    {
        if (resourceSchema["documentPathsMapping"] is not JsonObject documentPathsMapping)
        {
            return;
        }

        foreach (var mapping in documentPathsMapping.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (mapping.Value is null)
            {
                throw new InvalidOperationException(
                    "Expected documentPathsMapping entries to be non-null, invalid ApiSchema."
                );
            }

            if (mapping.Value is not JsonObject mappingObject)
            {
                throw new InvalidOperationException(
                    "Expected documentPathsMapping entries to be objects, invalid ApiSchema."
                );
            }

            var isReference =
                mappingObject["isReference"]?.GetValue<bool>()
                ?? throw new InvalidOperationException(
                    "Expected isReference to be on documentPathsMapping entry, invalid ApiSchema."
                );

            if (!isReference)
            {
                continue;
            }

            var targetProjectName = RequireString(mappingObject, "projectName");
            var targetResourceName = RequireString(mappingObject, "resourceName");
            var targetResource = new QualifiedResourceName(targetProjectName, targetResourceName);

            if (effectiveResources.Contains(targetResource))
            {
                continue;
            }

            var mappingLabel = FormatDocumentPathsMappingLabel(mapping.Key, mappingObject);

            throw new InvalidOperationException(
                $"documentPathsMapping {mappingLabel} on resource '{projectName}:{resourceName}' "
                    + $"references unknown resource '{targetProjectName}:{targetResourceName}'."
            );
        }
    }

    private static string FormatDocumentPathsMappingLabel(string mappingKey, JsonObject mappingObject)
    {
        if (string.IsNullOrWhiteSpace(mappingKey))
        {
            return "entry '<empty>'";
        }

        var path = TryGetOptionalString(mappingObject, "path");

        if (path is null)
        {
            return $"entry '{mappingKey}'";
        }

        return $"entry '{mappingKey}' (path '{path}')";
    }

    private static IReadOnlyList<ResourceSchemaEntry> OrderResourceSchemas(
        JsonObject resourceSchemas,
        string resourceSchemasPath
    )
    {
        List<ResourceSchemaEntry> entries = new(resourceSchemas.Count);

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

            entries.Add(new ResourceSchemaEntry(resourceSchemaEntry.Key, resourceName, resourceSchema));
        }

        return entries
            .OrderBy(entry => entry.ResourceName, StringComparer.Ordinal)
            .ThenBy(entry => entry.ResourceKey, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record ResourceSchemaEntry(
        string ResourceKey,
        string ResourceName,
        JsonObject ResourceSchema
    );

    private static void AddResourceEntries(
        HashSet<QualifiedResourceName> resources,
        Dictionary<QualifiedResourceName, bool> isAbstractByResource,
        JsonObject resourceSchemas,
        string projectName,
        string resourceSchemasPath,
        bool isAbstract
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
            var resource = new QualifiedResourceName(projectName, resourceName);

            if (resources.Add(resource))
            {
                isAbstractByResource[resource] = isAbstract;
                continue;
            }

            if (!isAbstractByResource.TryGetValue(resource, out var existingIsAbstract))
            {
                continue;
            }

            if (existingIsAbstract == isAbstract)
            {
                continue;
            }

            var existingLocation = existingIsAbstract
                ? "projectSchema.abstractResources"
                : "projectSchema.resourceSchemas";
            var duplicateLocation = isAbstract
                ? "projectSchema.abstractResources"
                : "projectSchema.resourceSchemas";

            throw new InvalidOperationException(
                $"Resource '{FormatResource(resource)}' is defined in both {existingLocation} "
                    + $"and {duplicateLocation}."
            );
        }
    }

    private static void ValidateResourceKeyAbstractness(
        IReadOnlyList<ResourceKeyEntry> resourceKeys,
        IReadOnlyDictionary<QualifiedResourceName, bool> isAbstractByResource
    )
    {
        foreach (var entry in resourceKeys)
        {
            if (!isAbstractByResource.TryGetValue(entry.Resource, out var expectedIsAbstract))
            {
                throw new InvalidOperationException(
                    $"Resource key entry for resource '{FormatResource(entry.Resource)}' "
                        + "does not correspond to a resource in the effective schema set."
                );
            }

            if (entry.IsAbstractResource == expectedIsAbstract)
            {
                continue;
            }

            var expectedLabel = expectedIsAbstract ? "abstract" : "concrete";
            var actualLabel = entry.IsAbstractResource ? "abstract" : "concrete";

            throw new InvalidOperationException(
                $"Resource key entry for resource '{FormatResource(entry.Resource)}' has "
                    + $"IsAbstractResource={entry.IsAbstractResource} ({actualLabel}) but expected "
                    + $"IsAbstractResource={expectedIsAbstract} ({expectedLabel})."
            );
        }
    }
}
