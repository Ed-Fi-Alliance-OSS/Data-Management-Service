// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

public sealed record IntegrationRelationalGetRequest(
    DocumentUuid DocumentUuid,
    BaseResourceInfo ResourceInfo,
    MappingSet MappingSet,
    IResourceAuthorizationHandler ResourceAuthorizationHandler,
    TraceId TraceId,
    RelationalGetRequestReadMode ReadMode = RelationalGetRequestReadMode.ExternalResponse,
    ReadableProfileProjectionContext? ReadableProfileProjectionContext = null
) : IRelationalGetRequest
{
    public ResourceName ResourceName => ResourceInfo.ResourceName;
}

public static class RelationalGetIntegrationTestHelper
{
    public static JsonObject CreateExpectedExternalResponse(
        string requestBodyJson,
        BaseResourceInfo resourceInfo,
        MappingSet mappingSet,
        Guid documentUuid,
        DateTimeOffset lastModifiedAt
    )
    {
        var expectedDocument =
            JsonNode.Parse(requestBodyJson)?.AsObject()
            ?? throw new InvalidOperationException("Expected request body JSON to parse into a JSON object.");

        expectedDocument["_etag"] = CreateExpectedEtag(requestBodyJson, resourceInfo, mappingSet);
        expectedDocument["id"] = documentUuid.ToString();
        expectedDocument["_lastModifiedDate"] = FormatExternalLastModifiedDate(lastModifiedAt);

        return expectedDocument;
    }

    public static string CreateExpectedEtag(
        string requestBodyJson,
        BaseResourceInfo resourceInfo,
        MappingSet mappingSet
    )
    {
        var expectedDocument =
            JsonNode.Parse(requestBodyJson)
            ?? throw new InvalidOperationException("Expected request body JSON to parse into a JSON object.");
        var readPlan = mappingSet.GetReadPlanOrThrow(
            new QualifiedResourceName(resourceInfo.ProjectName.Value, resourceInfo.ResourceName.Value)
        );
        var canonicalDocument = DocumentReconstituter.ReorderToReadPlanOrder(expectedDocument, readPlan);

        return DocumentComparer.GenerateContentHash(canonicalDocument);
    }

    public static string FormatExternalLastModifiedDate(DateTimeOffset lastModifiedAt) =>
        lastModifiedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    public static void AssertStudentSchoolAssociationExternalResponse(
        GetResult getResult,
        DocumentUuid expectedDocumentUuid,
        DateTimeOffset expectedLastModifiedAt,
        JsonNode expectedDocument,
        IReadOnlyList<int> expectedGraduationSchoolYears,
        IReadOnlyList<string> expectedEducationPlanDescriptors
    )
    {
        getResult.Should().BeOfType<GetResult.GetSuccess>();

        var success = (GetResult.GetSuccess)getResult;

        success.DocumentUuid.Should().Be(expectedDocumentUuid);
        success.LastModifiedTraceId.Should().BeNull();
        success.LastModifiedDate.Should().Be(expectedLastModifiedAt.UtcDateTime);
        success.EdfiDoc["id"]!.GetValue<string>().Should().Be(expectedDocumentUuid.Value.ToString());
        success.EdfiDoc["_etag"]!
            .GetValue<string>()
            .Should()
            .Be(expectedDocument["_etag"]!.GetValue<string>());
        success.EdfiDoc["_lastModifiedDate"]!
            .GetValue<string>()
            .Should()
            .Be(FormatExternalLastModifiedDate(expectedLastModifiedAt));
        success.EdfiDoc["alternativeGraduationPlans"]!
            .AsArray()
            .Select(plan =>
                plan?["alternativeGraduationPlanReference"]?["graduationSchoolYear"]?.GetValue<int>()
            )
            .Should()
            .Equal(expectedGraduationSchoolYears.Select(static year => (int?)year));
        success.EdfiDoc["educationPlans"]!
            .AsArray()
            .Select(plan => plan?["educationPlanDescriptor"]?.GetValue<string>())
            .Should()
            .Equal(expectedEducationPlanDescriptors);
        CanonicalizeJson(success.EdfiDoc).Should().Be(CanonicalizeJson(expectedDocument));
    }

    public static string CanonicalizeJson(JsonNode node) => NormalizeJsonNode(node)?.ToJsonString() ?? "null";

    private static JsonNode? NormalizeJsonNode(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonObject jsonObject => NormalizeJsonObject(jsonObject),
            JsonArray jsonArray => NormalizeJsonArray(jsonArray),
            _ => node.DeepClone(),
        };
    }

    private static JsonObject NormalizeJsonObject(JsonObject jsonObject)
    {
        JsonObject normalized = [];

        foreach (var property in jsonObject.OrderBy(static property => property.Key, StringComparer.Ordinal))
        {
            normalized[property.Key] = NormalizeJsonNode(property.Value);
        }

        return normalized;
    }

    private static JsonArray NormalizeJsonArray(JsonArray jsonArray)
    {
        JsonArray normalized = [];

        foreach (var item in jsonArray)
        {
            normalized.Add(NormalizeJsonNode(item));
        }

        return normalized;
    }
}
