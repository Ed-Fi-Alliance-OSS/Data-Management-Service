// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Benchmarks;

[MemoryDiagnoser]
public class DocumentValidatorBenchmarks
{
    private readonly RequestInfo _requestInfo;
    private readonly DocumentValidator _validator;

    public DocumentValidatorBenchmarks()
    {
        _requestInfo = BuildRequestInfo();
        _validator = new DocumentValidator(new CompiledSchemaCache());
    }

    [Benchmark]
    public void ValidateDocument() => _validator.Validate(_requestInfo);

    private static RequestInfo BuildRequestInfo()
    {
        var apiSchema = BuildSchema();

        var frontendRequest = new FrontendRequest(
            Path: "ed-fi/students",
            Body: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("benchmark")
        );

        var requestInfo = new RequestInfo(frontendRequest, RequestMethod.POST)
        {
            ApiSchemaDocuments = apiSchema,
            ApiSchemaReloadId = Guid.NewGuid(),
            ParsedBody = JsonNode.Parse("""{"studentUniqueId":"123","birthDate":"2010-01-01"}""")!,
            PathComponents = new(
                ProjectEndpointName: new("ed-fi"),
                EndpointName: new("students"),
                DocumentUuid: No.DocumentUuid
            ),
        };
        requestInfo.ProjectSchema = requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
            new("ed-fi")
        )!;
        requestInfo.ResourceSchema = new ResourceSchema(
            requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("students"))
                ?? new JsonObject()
        );
        return requestInfo;
    }

    private static ApiSchemaDocuments BuildSchema()
    {
        JsonObject resourceSchema = new()
        {
            ["resourceName"] = "Student",
            ["allowIdentityUpdates"] = false,
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSchoolYearEnumeration"] = false,
            ["isSubclass"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["authorizationPathways"] = new JsonArray(),
            ["booleanJsonPaths"] = new JsonArray(),
            ["dateJsonPaths"] = new JsonArray("$.birthDate"),
            ["dateTimeJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["domains"] = new JsonArray(),
            ["equalityConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray("$.studentUniqueId"),
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["studentUniqueId"] = new JsonObject { ["type"] = "string" },
                    ["birthDate"] = new JsonObject { ["type"] = "string", ["format"] = "date" },
                },
                ["required"] = new JsonArray("studentUniqueId", "birthDate"),
            },
            ["numericJsonPaths"] = new JsonArray(),
            ["queryFieldMapping"] = new JsonObject(),
            ["securableElements"] = new JsonObject(),
        };

        JsonObject projectSchema = new()
        {
            ["projectName"] = "Ed-Fi",
            ["projectVersion"] = "1.0.0",
            ["projectEndpointName"] = "ed-fi",
            ["description"] = "Benchmark project",
            ["isExtensionProject"] = false,
            ["abstractResources"] = new JsonObject(),
            ["caseInsensitiveEndpointNameMapping"] = new JsonObject { ["students"] = "students" },
            ["resourceNameMapping"] = new JsonObject { ["Student"] = "students" },
            ["resourceSchemas"] = new JsonObject { ["students"] = resourceSchema },
            ["educationOrganizationHierarchy"] = new JsonObject(),
            ["educationOrganizationTypes"] = new JsonArray(),
            ["domains"] = new JsonArray(),
        };

        JsonObject apiSchemaRoot = new()
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = projectSchema,
        };

        ApiSchemaDocumentNodes nodes = new(apiSchemaRoot, Array.Empty<JsonNode>());
        return new ApiSchemaDocuments(nodes, NullLogger<ApiSchemaDocuments>.Instance);
    }
}
