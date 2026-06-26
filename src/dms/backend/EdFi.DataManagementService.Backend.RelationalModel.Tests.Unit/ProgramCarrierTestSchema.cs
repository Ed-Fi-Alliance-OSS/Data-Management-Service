// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Shared schema builder for the ProgramCarrier abstract resource.
/// Used by abstract identity table and abstract union view derivation tests.
/// </summary>
internal static class ProgramCarrierTestSchema
{
    /// <summary>
    /// Builds a project schema with a ProgramCarrier abstract resource and a ProgramOffering member
    /// that references Program via $.programReference with both a scalar and a descriptor identity field.
    /// </summary>
    internal static JsonObject BuildProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["abstractResources"] = new JsonObject
            {
                ["ProgramCarrier"] = new JsonObject
                {
                    ["resourceName"] = "ProgramCarrier",
                    ["identityJsonPaths"] = new JsonArray
                    {
                        "$.programReference.educationOrganizationId",
                        "$.programReference.programTypeDescriptor",
                    },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["programOfferings"] = BuildProgramOfferingMemberSchema(),
                ["programs"] = BuildProgramTargetSchema(),
                ["programTypeDescriptors"] = BuildProgramTypeDescriptorSchema(),
            },
        };
    }

    /// <summary>
    /// ProgramOffering subclass of ProgramCarrier; references Program via $.programReference.
    /// </summary>
    internal static JsonObject BuildProgramOfferingMemberSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "ProgramOffering",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = true,
            ["subclassType"] = "association",
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "ProgramCarrier",
            ["superclassIdentityJsonPath"] = null,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                ["Program"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Program",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] = "$.programReference.educationOrganizationId",
                        },
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.programTypeDescriptor",
                            ["referenceJsonPath"] = "$.programReference.programTypeDescriptor",
                        },
                    },
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["equalityConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray
            {
                "$.programReference.educationOrganizationId",
                "$.programReference.programTypeDescriptor",
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["programReference"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["educationOrganizationId"] = new JsonObject
                            {
                                ["type"] = "integer",
                                ["format"] = "int64",
                            },
                            ["programTypeDescriptor"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["maxLength"] = 306,
                            },
                        },
                        ["required"] = new JsonArray { "educationOrganizationId", "programTypeDescriptor" },
                    },
                },
                ["required"] = new JsonArray { "programReference" },
            },
        };
    }

    /// <summary>
    /// Target Program resource with both a scalar and descriptor identity path.
    /// </summary>
    internal static JsonObject BuildProgramTargetSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Program",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId", "$.programTypeDescriptor" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["path"] = "$.educationOrganizationId",
                },
                ["ProgramTypeDescriptor"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = true,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "ProgramTypeDescriptor",
                    ["path"] = "$.programTypeDescriptor",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["educationOrganizationId"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["format"] = "int64",
                    },
                    ["programTypeDescriptor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 306 },
                },
                ["required"] = new JsonArray { "educationOrganizationId", "programTypeDescriptor" },
            },
        };
    }

    /// <summary>
    /// Minimal ProgramTypeDescriptor schema for descriptor binding.
    /// </summary>
    internal static JsonObject BuildProgramTypeDescriptorSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "ProgramTypeDescriptor",
            ["isDescriptor"] = true,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["codeValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                    ["namespace"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                },
                ["required"] = new JsonArray("codeValue", "namespace"),
            },
        };
    }
}
