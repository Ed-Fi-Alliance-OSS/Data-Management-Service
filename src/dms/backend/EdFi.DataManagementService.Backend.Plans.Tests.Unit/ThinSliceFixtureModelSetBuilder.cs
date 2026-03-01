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

internal static class ThinSliceFixtureModelSetBuilder
{
    private const string ProjectFileName = "EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj";
    private const string RelationalMappingVersion = "1.0.0";

    public static DerivedRelationalModelSet Build(string fixtureRelativePath, SqlDialect dialect)
    {
        var effectiveSchemaSet = LoadEffectiveSchemaSet(fixtureRelativePath);
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

    private static EffectiveSchemaSet LoadEffectiveSchemaSet(string fixtureRelativePath)
    {
        var fixturePath = GetFixturePath(fixtureRelativePath);
        var rootNode = JsonNode.Parse(File.ReadAllText(fixturePath));

        if (rootNode is not JsonObject root)
        {
            throw new InvalidOperationException($"Fixture '{fixturePath}' parsed null or non-object.");
        }

        var apiSchemaVersion = RequireString(root, "apiSchemaVersion");
        var projectSchema = RequireObject(root["projectSchema"], "projectSchema");
        var projectEndpointName = RequireString(projectSchema, "projectEndpointName");
        var projectName = RequireString(projectSchema, "projectName");
        var projectVersion = RequireString(projectSchema, "projectVersion");
        var resourceKeysInIdOrder = BuildResourceKeys(projectSchema, projectName, projectVersion);
        var seedHash = BuildSeedHash(resourceKeysInIdOrder);
        var effectiveSchemaHash = BuildHashHex("effective-schema", seedHash);
        var projectHash = BuildHashHex("project", Encoding.UTF8.GetBytes(projectName));

        var effectiveProjectSchema = new EffectiveProjectSchema(
            projectEndpointName,
            projectName,
            projectVersion,
            false,
            (JsonObject)projectSchema.DeepClone()
        );

        var schemaInfo = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: apiSchemaVersion,
            RelationalMappingVersion: RelationalMappingVersion,
            EffectiveSchemaHash: effectiveSchemaHash,
            ResourceKeyCount: resourceKeysInIdOrder.Count,
            ResourceKeySeedHash: seedHash,
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo(
                    ProjectEndpointName: projectEndpointName,
                    ProjectName: projectName,
                    ProjectVersion: projectVersion,
                    IsExtensionProject: false,
                    ProjectHash: projectHash
                ),
            ],
            ResourceKeysInIdOrder: resourceKeysInIdOrder
        );

        return new EffectiveSchemaSet(schemaInfo, [effectiveProjectSchema]);
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
        JsonObject projectSchema,
        string projectName,
        string projectVersion
    )
    {
        var resourceSchemas = RequireObject(
            projectSchema["resourceSchemas"],
            "projectSchema.resourceSchemas"
        );
        var orderedResourceNames = resourceSchemas
            .Select(entry => new
            {
                ResourceName = ResolveResourceName(entry.Key, entry.Value),
                EndpointName = entry.Key,
            })
            .OrderBy(entry => entry.ResourceName, StringComparer.Ordinal)
            .ThenBy(entry => entry.EndpointName, StringComparer.Ordinal)
            .ToArray();

        List<ResourceKeyEntry> keys = new(orderedResourceNames.Length);
        short nextId = 1;

        foreach (var resource in orderedResourceNames)
        {
            keys.Add(
                new ResourceKeyEntry(
                    ResourceKeyId: nextId,
                    Resource: new QualifiedResourceName(projectName, resource.ResourceName),
                    ResourceVersion: projectVersion,
                    IsAbstractResource: false
                )
            );
            nextId++;
        }

        return keys;
    }

    private static string ResolveResourceName(string endpointName, JsonNode? resourceSchemaNode)
    {
        if (resourceSchemaNode is null)
        {
            throw new InvalidOperationException(
                "Expected projectSchema.resourceSchemas entries to be non-null, invalid ApiSchema."
            );
        }

        if (resourceSchemaNode is not JsonObject resourceSchema)
        {
            throw new InvalidOperationException(
                "Expected projectSchema.resourceSchemas entries to be objects, invalid ApiSchema."
            );
        }

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
}
