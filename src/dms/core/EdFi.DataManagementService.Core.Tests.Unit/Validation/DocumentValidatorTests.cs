// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Threading;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Validation;
using FluentAssertions;
using Json.Schema;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Validation;

[TestFixture]
public class DocumentValidatorTests
{
    [Test]
    public void Validate_UsesCompiledSchemaCachePerReload()
    {
        var cache = new CountingCompiledSchemaCache();
        var validator = new DocumentValidator(cache);
        RequestInfo requestInfo = CreateRequestInfo();

        validator.Validate(requestInfo);
        validator.Validate(requestInfo);

        cache.FactoryInvocationCount.Should().Be(1);
    }

    private static RequestInfo CreateRequestInfo()
    {
        var schoolSchema = new JsonSchemaBuilder()
            .Type(SchemaValueType.Object)
            .AdditionalProperties(false)
            .Properties(("schoolId", new JsonSchemaBuilder().Type(SchemaValueType.Integer)))
            .Required("schoolId")
            .Build();

        var apiSchema = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("School")
            .WithJsonSchemaForInsert(schoolSchema)
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();

        var frontendRequest = new FrontendRequest(
            Path: "ed-fi/schools",
            Body: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("trace"),
            RouteQualifiers: []
        );

        var requestInfo = new RequestInfo(frontendRequest, RequestMethod.POST)
        {
            ApiSchemaDocuments = apiSchema,
            ApiSchemaReloadId = Guid.NewGuid(),
            ParsedBody = JsonNode.Parse("""{"schoolId":1}""")!,
            PathComponents = new(
                ProjectEndpointName: new("ed-fi"),
                EndpointName: new("schools"),
                DocumentUuid: No.DocumentUuid
            ),
        };
        requestInfo.ProjectSchema = requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
            new("ed-fi")
        )!;
        requestInfo.ResourceSchema = new ResourceSchema(
            requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools")) ?? new JsonObject()
        );
        return requestInfo;
    }

    private sealed class CountingCompiledSchemaCache : ICompiledSchemaCache
    {
        private readonly CompiledSchemaCache _inner = new();
        private int _factoryInvocationCount;

        public int FactoryInvocationCount => _factoryInvocationCount;

        public JsonSchema GetOrAdd(
            ProjectName projectName,
            ResourceName resourceName,
            RequestMethod method,
            Guid reloadId,
            Func<JsonSchema> schemaFactory
        )
        {
            return _inner.GetOrAdd(
                projectName,
                resourceName,
                method,
                reloadId,
                () =>
                {
                    Interlocked.Increment(ref _factoryInvocationCount);
                    return schemaFactory();
                }
            );
        }

        public void Prime(ApiSchemaDocuments documents, Guid reloadId) => _inner.Prime(documents, reloadId);
    }
}
