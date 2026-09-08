// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Shared schema builder for the SchoolCarrier abstract resource whose concrete member (Campus) applies a
/// relational.nameOverrides entry to its reference identity column. The concrete physical column becomes
/// SchoolBase_CampusId (referenceBaseName "SchoolBase" + override "campusId"), while the abstract identity
/// column keeps the override-free convention name SchoolBase_SchoolId. Used by abstract identity table,
/// union view, and trigger derivation tests to prove the override is not propagated to the abstract
/// contract yet still bridges correctly into downstream consumers.
/// </summary>
internal static class SchoolCarrierOverrideTestSchema
{
    /// <summary>
    /// Builds a project schema with a SchoolCarrier abstract resource, a Campus member that references
    /// SchoolBase via $.schoolReference.schoolId with a column nameOverride, and the SchoolBase target.
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
                ["SchoolCarrier"] = new JsonObject
                {
                    ["resourceName"] = "SchoolCarrier",
                    ["identityJsonPaths"] = new JsonArray { "$.schoolReference.schoolId" },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["campuses"] = BuildCampusMemberSchema(),
                ["schoolBases"] = BuildSchoolBaseTargetSchema(),
            },
        };
    }

    /// <summary>
    /// Campus: subclass of SchoolCarrier, references SchoolBase via $.schoolReference.schoolId.
    /// Applies a nameOverride so the concrete column is SchoolBase_CampusId instead of SchoolBase_SchoolId.
    /// </summary>
    internal static JsonObject BuildCampusMemberSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Campus",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = true,
            ["subclassType"] = "association",
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "SchoolCarrier",
            ["superclassIdentityJsonPath"] = null,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolBase"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "SchoolBase",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.schoolId",
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                    },
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["equalityConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolReference.schoolId" },
            ["relational"] = new JsonObject
            {
                ["nameOverrides"] = new JsonObject { ["$.schoolReference.schoolId"] = "campusId" },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["schoolReference"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["schoolId"] = new JsonObject { ["type"] = "integer" },
                        },
                        ["required"] = new JsonArray { "schoolId" },
                    },
                },
                ["required"] = new JsonArray { "schoolReference" },
            },
        };
    }

    /// <summary>
    /// SchoolBase: standalone target resource with $.schoolId as identity.
    /// </summary>
    internal static JsonObject BuildSchoolBaseTargetSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "SchoolBase",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["path"] = "$.schoolId",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["schoolId"] = new JsonObject { ["type"] = "integer" } },
                ["required"] = new JsonArray { "schoolId" },
            },
        };
    }
}
