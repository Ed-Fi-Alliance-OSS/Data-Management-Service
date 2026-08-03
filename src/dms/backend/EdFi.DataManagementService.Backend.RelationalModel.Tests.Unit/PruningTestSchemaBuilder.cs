// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Declarative project-schema builder for SQL Server pruning topology fixtures (chains, diamonds,
/// convergences, cycles). Each resource declares scalar identity parts and references; identity
/// leaf paths propagate transitively through part-of-identity references, and equality constraints
/// drive key unification exactly as MetaEd merges do.
/// </summary>
internal static class PruningTestSchemaBuilder
{
    /// <summary>
    /// A reference from a resource to a target. The role name prefixes the reference object path
    /// and binding columns (defaults to the target name); part-of-identity references propagate
    /// the target's identity into the referencing resource.
    /// </summary>
    internal sealed record ReferenceSpec(
        string TargetName,
        bool Required = true,
        bool PartOfIdentity = false,
        string? RoleName = null
    )
    {
        public string EffectiveRoleName => RoleName ?? TargetName;
    }

    /// <summary>
    /// A resource with scalar identity parts, references, and optional equality constraints
    /// (pairs of document JSON paths whose values must match, producing unified storage).
    /// </summary>
    internal sealed record ResourceSpec(
        string Name,
        bool AllowIdentityUpdates = false,
        IReadOnlyList<string>? IdentityScalars = null,
        IReadOnlyList<ReferenceSpec>? References = null,
        IReadOnlyList<(string SourcePath, string TargetPath)>? EqualityConstraints = null
    );

    /// <summary>
    /// An identity leaf of a resource: the identity JSON path on the resource document and the
    /// leaf property name it flattens to under a reference object.
    /// </summary>
    private sealed record IdentityLeaf(string IdentityJsonPath, string LeafName);

    /// <summary>
    /// Builds an Ed-Fi project schema from the given resource topology.
    /// </summary>
    internal static JsonObject BuildProjectSchema(params ResourceSpec[] resources)
    {
        var specsByName = resources.ToDictionary(resource => resource.Name, StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<IdentityLeaf>> leavesByName = new(StringComparer.Ordinal);

        foreach (var resource in resources)
        {
            _ = ResolveIdentityLeaves(resource.Name, specsByName, leavesByName);
        }

        var resourceSchemas = new JsonObject();

        foreach (var resource in resources)
        {
            resourceSchemas[Camel(resource.Name) + "s"] = BuildResourceSchema(resource, leavesByName);
        }

        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = resourceSchemas,
        };
    }

    /// <summary>
    /// Resolves a resource's ordered identity leaves: scalar parts first, then the flattened
    /// leaves of each part-of-identity reference.
    /// </summary>
    private static IReadOnlyList<IdentityLeaf> ResolveIdentityLeaves(
        string resourceName,
        IReadOnlyDictionary<string, ResourceSpec> specsByName,
        Dictionary<string, IReadOnlyList<IdentityLeaf>> leavesByName
    )
    {
        if (leavesByName.TryGetValue(resourceName, out var cached))
        {
            return cached;
        }

        var spec = specsByName[resourceName];
        List<IdentityLeaf> leaves = [];

        foreach (var scalar in spec.IdentityScalars ?? [])
        {
            leaves.Add(new IdentityLeaf($"$.{scalar}", scalar));
        }

        foreach (var reference in spec.References ?? [])
        {
            if (!reference.PartOfIdentity)
            {
                continue;
            }

            var referencePrefix = $"$.{Camel(reference.EffectiveRoleName)}Reference";

            foreach (var targetLeaf in ResolveIdentityLeaves(reference.TargetName, specsByName, leavesByName))
            {
                leaves.Add(new IdentityLeaf($"{referencePrefix}.{targetLeaf.LeafName}", targetLeaf.LeafName));
            }
        }

        leavesByName[resourceName] = leaves;
        return leaves;
    }

    /// <summary>
    /// Builds one resource schema with document paths mapping, identity paths, insert schema, and
    /// equality constraints.
    /// </summary>
    private static JsonObject BuildResourceSchema(
        ResourceSpec resource,
        IReadOnlyDictionary<string, IReadOnlyList<IdentityLeaf>> leavesByName
    )
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        var documentPathsMapping = new JsonObject();
        var identityJsonPaths = new JsonArray();

        foreach (var scalar in resource.IdentityScalars ?? [])
        {
            properties[scalar] = new JsonObject { ["type"] = "integer" };
            required.Add(scalar);
            identityJsonPaths.Add($"$.{scalar}");
            documentPathsMapping[Pascal(scalar)] = new JsonObject
            {
                ["isReference"] = false,
                ["path"] = $"$.{scalar}",
            };
        }

        foreach (var reference in resource.References ?? [])
        {
            var roleName = reference.EffectiveRoleName;
            var referenceProperty = $"{Camel(roleName)}Reference";
            var referencePrefix = $"$.{referenceProperty}";
            var targetLeaves = leavesByName[reference.TargetName];

            var referenceProperties = new JsonObject();
            var referenceJsonPaths = new JsonArray();

            foreach (var targetLeaf in targetLeaves)
            {
                referenceProperties[targetLeaf.LeafName] = new JsonObject { ["type"] = "integer" };
                referenceJsonPaths.Add(
                    new JsonObject
                    {
                        ["identityJsonPath"] = targetLeaf.IdentityJsonPath,
                        ["referenceJsonPath"] = $"{referencePrefix}.{targetLeaf.LeafName}",
                    }
                );

                if (reference.PartOfIdentity)
                {
                    identityJsonPaths.Add($"{referencePrefix}.{targetLeaf.LeafName}");
                }
            }

            properties[referenceProperty] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = referenceProperties,
            };

            if (reference.Required)
            {
                required.Add(referenceProperty);
            }

            documentPathsMapping[roleName] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
                ["isRequired"] = reference.Required,
                ["projectName"] = "Ed-Fi",
                ["resourceName"] = reference.TargetName,
                ["referenceJsonPaths"] = referenceJsonPaths,
            };
        }

        var schema = new JsonObject
        {
            ["resourceName"] = resource.Name,
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = resource.AllowIdentityUpdates,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = identityJsonPaths,
            ["documentPathsMapping"] = documentPathsMapping,
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
            },
        };

        if (resource.EqualityConstraints is { Count: > 0 })
        {
            var equalityConstraints = new JsonArray();

            foreach (var (sourcePath, targetPath) in resource.EqualityConstraints)
            {
                equalityConstraints.Add(
                    new JsonObject { ["sourceJsonPath"] = sourcePath, ["targetJsonPath"] = targetPath }
                );
            }

            schema["equalityConstraints"] = equalityConstraints;
        }

        return schema;
    }

    private static string Camel(string name)
    {
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string Pascal(string name)
    {
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
