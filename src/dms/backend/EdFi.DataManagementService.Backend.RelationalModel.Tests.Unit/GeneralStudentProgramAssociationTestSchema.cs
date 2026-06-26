// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Shared schema builder for the GeneralStudentProgramAssociation abstract resource.
/// Used by constraint derivation and trigger inventory pass tests.
/// </summary>
internal static class GeneralStudentProgramAssociationTestSchema
{
    /// <summary>
    /// Builds a project schema with <c>GeneralStudentProgramAssociation</c> as the abstract resource
    /// whose identity paths flow through EducationOrganization, Program (with a descriptor field), and
    /// Student references. Referenced resources (School as an EducationOrganization subclass, Program,
    /// Student, ProgramTypeDescriptor) are included so the schema passes validation.
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
                ["GeneralStudentProgramAssociation"] = new JsonObject
                {
                    ["identityJsonPaths"] = new JsonArray
                    {
                        "$.beginDate",
                        "$.educationOrganizationReference.educationOrganizationId",
                        "$.programReference.educationOrganizationId",
                        "$.programReference.programTypeDescriptor",
                        "$.studentReference.studentUniqueId",
                    },
                },
                ["EducationOrganization"] = new JsonObject
                {
                    ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["studentArtProgramAssociations"] = BuildStudentArtProgramAssociationSchema(),
                ["schools"] = BuildSchoolSchema(),
                ["programs"] = BuildProgramSchema(),
                ["students"] = BuildStudentSchema(),
                ["programTypeDescriptors"] = BuildProgramTypeDescriptorSchema(),
            },
        };
    }

    /// <summary>
    /// StudentArtProgramAssociation concrete subclass of GeneralStudentProgramAssociation;
    /// identity paths route through EducationOrganization, Program (descriptor-valued field), and Student.
    /// </summary>
    internal static JsonObject BuildStudentArtProgramAssociationSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["beginDate"] = new JsonObject { ["type"] = "string", ["format"] = "date" },
                ["educationOrganizationReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                    },
                },
                ["programReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                        ["programTypeDescriptor"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["maxLength"] = 306,
                        },
                    },
                },
                ["studentReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
                    },
                },
            },
            ["required"] = new JsonArray(
                "beginDate",
                "educationOrganizationReference",
                "programReference",
                "studentReference"
            ),
        };

        return new JsonObject
        {
            ["resourceName"] = "StudentArtProgramAssociation",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = true,
            ["subclassType"] = "association",
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "GeneralStudentProgramAssociation",
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray
            {
                "$.beginDate",
                "$.educationOrganizationReference.educationOrganizationId",
                "$.programReference.educationOrganizationId",
                "$.programReference.programTypeDescriptor",
                "$.studentReference.studentUniqueId",
            },
            ["documentPathsMapping"] = new JsonObject
            {
                ["BeginDate"] = new JsonObject { ["isReference"] = false, ["path"] = "$.beginDate" },
                ["EducationOrganization"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "EducationOrganization",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] =
                                "$.educationOrganizationReference.educationOrganizationId",
                        },
                    },
                },
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
                ["Student"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Student",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.studentUniqueId",
                            ["referenceJsonPath"] = "$.studentReference.studentUniqueId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    /// <summary>
    /// School as a domainObjectSubclass of EducationOrganization.
    /// </summary>
    internal static JsonObject BuildSchoolSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = true,
            ["subclassType"] = "domainObjectSubclass",
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "EducationOrganization",
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["path"] = "$.educationOrganizationId",
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
                },
                ["required"] = new JsonArray { "educationOrganizationId" },
            },
        };
    }

    /// <summary>
    /// Program resource with a scalar and a descriptor-valued identity path.
    /// </summary>
    internal static JsonObject BuildProgramSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Program",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId", "$.programTypeDescriptor" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
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
    /// Student resource with a scalar identity path.
    /// </summary>
    internal static JsonObject BuildStudentSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Student",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.studentUniqueId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["StudentUniqueId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["path"] = "$.studentUniqueId",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
                },
                ["required"] = new JsonArray { "studentUniqueId" },
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
