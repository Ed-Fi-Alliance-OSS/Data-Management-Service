// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Shared schema builder methods used by both index and trigger inventory pass tests.
/// </summary>
internal static class CommonInventoryTestSchemaBuilder
{
    /// <summary>
    /// Build a descriptor resource schema (GradeLevelDescriptor).
    /// </summary>
    internal static JsonObject BuildDescriptorSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["namespace"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                ["codeValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
            },
            ["required"] = new JsonArray("namespace", "codeValue"),
        };

        return new JsonObject
        {
            ["resourceName"] = "GradeLevelDescriptor",
            ["isDescriptor"] = true,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build a Contact resource schema.
    /// </summary>
    internal static JsonObject BuildContactSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["contactUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
            },
            ["required"] = new JsonArray("contactUniqueId"),
        };

        return new JsonObject
        {
            ["resourceName"] = "Contact",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.contactUniqueId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["ContactUniqueId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.contactUniqueId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build a Contact extension resource schema.
    /// </summary>
    internal static JsonObject BuildContactExtensionSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["_ext"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["sample"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["nickname"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
                            },
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Contact",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = true,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// Build project schema with only a descriptor.
    /// </summary>
    internal static JsonObject BuildDescriptorOnlyProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["gradeLevelDescriptors"] = BuildDescriptorSchema() },
        };
    }

    /// <summary>
    /// Build core project schema for extension testing.
    /// </summary>
    internal static JsonObject BuildExtensionCoreProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["contacts"] = BuildContactSchema() },
        };
    }

    /// <summary>
    /// Build extension project schema for extension testing.
    /// </summary>
    internal static JsonObject BuildExtensionProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["contacts"] = BuildContactExtensionSchema() },
        };
    }
}
