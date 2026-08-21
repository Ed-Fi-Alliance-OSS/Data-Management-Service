// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;

namespace EdFi.DataManagementService.Core.Tests.Unit.OpenApi;

/// <summary>
/// Fixtures for cursor-paging OpenAPI assembly. Paths are declared as resource fragments rather than in
/// the base document, because only a path a resource fragment owns is eligible for augmentation.
/// </summary>
internal static class CursorPagingOpenApiTestBase
{
    internal const string CoreCollectionPath = "/ed-fi/academicWeeks";
    internal const string CoreItemPath = "/ed-fi/academicWeeks/{id}";
    internal const string CoreDeletesPath = "/ed-fi/academicWeeks/deletes";
    internal const string CoreKeyChangesPath = "/ed-fi/academicWeeks/keyChanges";
    internal const string CoreCompositePath = "/composites/ed-fi/enrollment/sections";
    internal const string CorePartitionPath = "/ed-fi/academicWeeks/partitions";
    internal const string CoreOperationId = "getAcademicWeeks";

    internal const string DescriptorCollectionPath = "/ed-fi/academicSubjectDescriptors";
    internal const string DescriptorPartitionPath = "/ed-fi/academicSubjectDescriptors/partitions";
    internal const string DescriptorOperationId = "getAcademicSubjects";

    internal const string ExtensionCollectionPath = "/tpdm/candidates";
    internal const string ExtensionPartitionPath = "/tpdm/candidates/partitions";
    internal const string ExtensionOperationId = "get_TPDMCandidates";

    internal const string ResourceExtensionCollectionPath = "/tpdm/schools";

    internal const string ExcludedDomainCollectionPath = "/ed-fi/accountabilityRatings";
    internal const string ExcludedDomainPartitionPath = "/ed-fi/accountabilityRatings/partitions";
    internal const string ExcludedDomainName = "Assessment";

    internal const string ManagementPath = "/management/claimSets";
    internal const string MetadataPath = "/metadata/specifications";

    internal const string CoreTag = "academicWeeks";
    internal const string CopiedFilterName = "weekIdentifier";

    /// <summary>
    /// A core resource collection and descriptor collection, a domain-tagged core resource, an extension
    /// resource collection, a resource-extension fragment that owns no endpoint, derived change-query and
    /// composite-shaped fragment paths, and two-segment management and metadata paths that arrive from the
    /// base document rather than from a resource.
    /// </summary>
    internal static ApiSchemaDocumentNodes ApiSchemaNodes()
    {
        return new ApiSchemaBuilder()
            .WithStartProject("ed-fi", "5.0.0")
            .WithOpenApiBaseDocuments(
                resourcesDoc: BaseDocument("Ed-Fi Resources API", includeNonResourcePaths: true),
                descriptorsDoc: BaseDocument("Ed-Fi Descriptors API", includeNonResourcePaths: false)
            )
            .WithStartResource("AcademicWeek")
            .WithResourceOpenApiFragments(
                "resources",
                schemas: new JsonObject { ["EdFi_AcademicWeek"] = ResourceSchema() },
                paths: CoreResourcePaths(),
                tags: Tags(CoreTag)
            )
            .WithEndResource()
            .WithStartResource("AccountabilityRating")
            .WithResourceOpenApiFragments(
                "resources",
                schemas: new JsonObject { ["EdFi_AccountabilityRating"] = ResourceSchema() },
                paths: ExcludedDomainPaths(),
                tags: Tags("accountabilityRatings")
            )
            .WithEndResource()
            .WithStartResource("AcademicSubjectDescriptor", isDescriptor: true)
            .WithResourceOpenApiFragments(
                "descriptors",
                schemas: new JsonObject { ["EdFi_AcademicSubjectDescriptor"] = ResourceSchema() },
                paths: DescriptorPaths(),
                tags: Tags("academicSubjectDescriptors")
            )
            .WithEndResource()
            .WithEndProject()
            .WithStartProject("TPDM", "5.0.0")
            .WithStartResource("Candidate")
            .WithResourceOpenApiFragments(
                "resources",
                schemas: new JsonObject { ["TPDM_Candidate"] = ResourceSchema() },
                paths: ExtensionResourcePaths(),
                tags: Tags("candidates")
            )
            .WithEndResource()
            .WithStartResource("School", isResourceExtension: true)
            .WithResourceOpenApiFragments(
                "resources",
                schemas: new JsonObject { ["TPDM_SchoolExtension"] = ResourceSchema() },
                paths: ResourceExtensionPaths(),
                tags: Tags("schools")
            )
            .WithEndResource()
            .WithEndProject()
            .AsApiSchemaNodes();
    }

    private static JsonObject CoreResourcePaths()
    {
        return new JsonObject
        {
            [CoreCollectionPath] = new JsonObject
            {
                ["get"] = CollectionGet(CoreOperationId, CoreTag, "EdFi_AcademicWeek"),
                ["post"] = new JsonObject { ["description"] = "academicWeek post" },
            },
            [CoreItemPath] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["description"] = "academicWeek by id",
                    ["operationId"] = "getAcademicWeeksById",
                },
            },
            [CoreDeletesPath] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["description"] = "academicWeek deletes",
                    ["operationId"] = "deletesAcademicWeeks",
                },
            },
            [CoreKeyChangesPath] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["description"] = "academicWeek key changes",
                    ["operationId"] = "keyChangesAcademicWeeks",
                },
            },
            [CoreCompositePath] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["description"] = "composite sections",
                    ["operationId"] = "getEnrollmentSections",
                },
            },
        };
    }

    private static JsonObject ExcludedDomainPaths()
    {
        JsonObject collection = new()
        {
            ["get"] = CollectionGet(
                "getAccountabilityRatings",
                "accountabilityRatings",
                "EdFi_AccountabilityRating"
            ),
            ["x-Ed-Fi-domains"] = new JsonArray(ExcludedDomainName),
        };

        return new JsonObject { [ExcludedDomainCollectionPath] = collection };
    }

    private static JsonObject DescriptorPaths()
    {
        return new JsonObject
        {
            [DescriptorCollectionPath] = new JsonObject
            {
                ["get"] = CollectionGet(
                    DescriptorOperationId,
                    "academicSubjectDescriptors",
                    "EdFi_AcademicSubjectDescriptor"
                ),
            },
        };
    }

    private static JsonObject ExtensionResourcePaths()
    {
        return new JsonObject
        {
            [ExtensionCollectionPath] = new JsonObject
            {
                ["get"] = CollectionGet(ExtensionOperationId, "candidates", "TPDM_Candidate"),
            },
        };
    }

    private static JsonObject ResourceExtensionPaths()
    {
        return new JsonObject
        {
            [ResourceExtensionCollectionPath] = new JsonObject
            {
                ["get"] = CollectionGet("get_TPDMSchools", "schools", "TPDM_SchoolExtension"),
            },
        };
    }

    /// <summary>
    /// A collection GET shaped like a published resource fragment: referenced traditional-paging, filter,
    /// and projection parameters, inline resource filters including one named for the partition count,
    /// security, tags, and the success and referenced failure responses a published collection declares.
    /// </summary>
    internal static JsonObject CollectionGet(string operationId, string tagName, string responseSchemaName)
    {
        return new JsonObject
        {
            ["description"] = $"{tagName} get description",
            ["operationId"] = operationId,
            ["parameters"] = new JsonArray(
                ParameterReference("offset"),
                ParameterReference("limit"),
                ParameterReference("MinChangeVersion"),
                ParameterReference("MaxChangeVersion"),
                ParameterReference("totalCount"),
                ParameterReference("queryExpression"),
                ParameterReference("fields"),
                InlineFilter(CopiedFilterName),
                InlineFilter("number"),
                HeaderParameter("Use-Snapshot")
            ),
            ["responses"] = new JsonObject
            {
                ["200"] = new JsonObject
                {
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject
                        {
                            ["schema"] = new JsonObject
                            {
                                ["items"] = new JsonObject
                                {
                                    ["$ref"] = $"#/components/schemas/{responseSchemaName}",
                                },
                                ["type"] = "array",
                            },
                        },
                    },
                    ["description"] = "The requested resource was successfully retrieved.",
                },
                ["304"] = ResponseReference("NotModified"),
                ["400"] = ResponseReference("BadRequest"),
                ["401"] = ResponseReference("Unauthorized"),
                ["403"] = ResponseReference("Forbidden"),
                ["404"] = ResponseReference("NotFoundUseSnapshot"),
                ["500"] = ResponseReference("Error"),
            },
            ["security"] = new JsonArray(new JsonObject { ["oauth2_client_credentials"] = new JsonArray() }),
            ["summary"] = $"Retrieves specific {tagName}.",
            ["tags"] = new JsonArray(tagName),
        };
    }

    internal static JsonObject ParameterReference(string componentName) =>
        new() { ["$ref"] = $"#/components/parameters/{componentName}" };

    internal static JsonObject ResponseReference(string componentName) =>
        new() { ["$ref"] = $"#/components/responses/{componentName}" };

    internal static JsonObject InlineFilter(string name) =>
        new()
        {
            ["description"] = $"{name} filter",
            ["in"] = "query",
            ["name"] = name,
            ["schema"] = new JsonObject { ["type"] = "string" },
        };

    private static JsonObject HeaderParameter(string name) =>
        new()
        {
            ["description"] = $"{name} header",
            ["in"] = "header",
            ["name"] = name,
            ["schema"] = new JsonObject { ["type"] = "string" },
        };

    private static JsonObject ResourceSchema() =>
        new()
        {
            ["description"] = "a resource",
            ["properties"] = new JsonObject(),
            ["type"] = "object",
        };

    private static JsonArray Tags(string tagName) =>
        [new JsonObject { ["description"] = $"{tagName} description", ["name"] = tagName }];

    private static JsonObject BaseDocument(string title, bool includeNonResourcePaths)
    {
        JsonObject paths = new();

        if (includeNonResourcePaths)
        {
            // Two-segment paths that no resource owns. They must never be augmented, which is why
            // eligibility is proven by resource-fragment provenance rather than by path shape alone.
            paths[ManagementPath] = new JsonObject
            {
                ["get"] = new JsonObject { ["description"] = "claim sets", ["operationId"] = "getClaimSets" },
            };
            paths[MetadataPath] = new JsonObject
            {
                ["get"] = new JsonObject
                {
                    ["description"] = "specifications",
                    ["operationId"] = "getSpecifications",
                },
            };
        }

        return new JsonObject
        {
            ["openapi"] = "3.0.1",
            ["info"] = new JsonObject { ["title"] = title, ["version"] = "5.0.0" },
            ["servers"] = new JsonArray(),
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["parameters"] = ParameterComponents(),
                ["responses"] = ResponseComponents(),
                ["schemas"] = new JsonObject(),
            },
            ["tags"] = new JsonArray(),
        };
    }

    /// <summary>
    /// The parameter components a published base document declares, including the four cursor-paging
    /// components OpenAPI assembly requires.
    /// </summary>
    internal static JsonObject ParameterComponents()
    {
        JsonObject parameters = ApiSchemaBuilder.CursorPagingParameterComponents();
        parameters["offset"] = QueryComponent("offset", "integer");
        parameters["totalCount"] = QueryComponent("totalCount", "boolean");
        parameters["MinChangeVersion"] = QueryComponent("minChangeVersion", "integer");
        parameters["MaxChangeVersion"] = QueryComponent("maxChangeVersion", "integer");
        parameters["fields"] = QueryComponent("fields", "string");
        parameters["queryExpression"] = QueryComponent("q", "string");
        return parameters;
    }

    /// <summary>
    /// The response components a published base document declares and every collection GET references.
    /// </summary>
    private static JsonObject ResponseComponents()
    {
        JsonObject responses = new();

        foreach (
            string componentName in new[]
            {
                "NotModified",
                "BadRequest",
                "Unauthorized",
                "Forbidden",
                "NotFoundUseSnapshot",
                "Error",
            }
        )
        {
            responses[componentName] = new JsonObject { ["description"] = $"{componentName} response" };
        }

        return responses;
    }

    private static JsonObject QueryComponent(string name, string type) =>
        new()
        {
            ["description"] = $"{name} description",
            ["in"] = "query",
            ["name"] = name,
            ["schema"] = new JsonObject { ["type"] = type },
        };
}
