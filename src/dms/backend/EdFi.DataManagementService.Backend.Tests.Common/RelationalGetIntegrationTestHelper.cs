// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

public sealed record IntegrationRelationalGetRequest(
    DocumentUuid DocumentUuid,
    BaseResourceInfo ResourceInfo,
    MappingSet MappingSet,
    AuthorizationStrategyEvaluator[] AuthorizationStrategyEvaluators,
    TraceId TraceId,
    RelationalGetRequestReadMode ReadMode = RelationalGetRequestReadMode.ExternalResponse,
    ReadableProfileProjectionContext? ReadableProfileProjectionContext = null
) : IGetRequest
{
    public ResourceName ResourceName => ResourceInfo.ResourceName;

    public RelationalAuthorizationContext AuthorizationContext { get; init; } = new([]);
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

        expectedDocument["id"] = documentUuid.ToString();
        expectedDocument["_lastModifiedDate"] = FormatExternalLastModifiedDate(lastModifiedAt);

        return expectedDocument;
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
        AssertComposedEtag(success.EdfiDoc["_etag"]!.GetValue<string>());
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

    /// <summary>
    /// The composed strong-validator etag wire shape: "{ContentVersion}-{schemaEpoch}.{format}.{profileCode}.{linkFlag}".
    /// ContentVersion is digits; schemaEpoch is up to 8 lowercase hex; format is a single lowercase letter (j today);
    /// profileCode is "_" (no profile) or an 8-hex SHA-256 prefix; linkFlag is l/n. The exact ContentVersion is not
    /// asserted here because it is not derivable from the request body — callers assert monotonicity/parity separately.
    /// </summary>
    private const string ComposedEtagPattern = @"^\d+-[0-9a-f]{1,8}\.[a-z]\.(_|[0-9a-f]{8})\.[ln]$";

    public static void AssertComposedEtag(string? etag)
    {
        etag.Should().NotBeNullOrEmpty();
        etag.Should().MatchRegex(ComposedEtagPattern);
    }

    public static void AssertWriteResultEtagParity(UpsertResult writeResult, GetResult getResult) =>
        GetWriteResultEtag(writeResult).Should().Be(GetReadResultEtag(getResult));

    public static void AssertWriteResultEtagParity(UpdateResult writeResult, GetResult getResult) =>
        GetWriteResultEtag(writeResult).Should().Be(GetReadResultEtag(getResult));

    public static string CanonicalizeJson(JsonNode node) => NormalizeJsonNode(node)?.ToJsonString() ?? "null";

    private static string GetWriteResultEtag(UpsertResult writeResult) =>
        writeResult switch
        {
            UpsertResult.InsertSuccess { ETag: not null } insertSuccess => insertSuccess.ETag,
            UpsertResult.UpdateSuccess { ETag: not null } updateSuccess => updateSuccess.ETag,
            UpsertResult.InsertSuccess => throw new InvalidOperationException(
                "Expected upsert success to return an ETag for parity assertion."
            ),
            UpsertResult.UpdateSuccess => throw new InvalidOperationException(
                "Expected upsert success to return an ETag for parity assertion."
            ),
            _ => throw new InvalidOperationException(
                $"Expected upsert success result for ETag parity assertion, but got '{writeResult.GetType().Name}'."
            ),
        };

    private static string GetWriteResultEtag(UpdateResult writeResult) =>
        writeResult switch
        {
            UpdateResult.UpdateSuccess { ETag: not null } updateSuccess => updateSuccess.ETag,
            UpdateResult.UpdateSuccess => throw new InvalidOperationException(
                "Expected update success to return an ETag for parity assertion."
            ),
            _ => throw new InvalidOperationException(
                $"Expected update success result for ETag parity assertion, but got '{writeResult.GetType().Name}'."
            ),
        };

    private static string GetReadResultEtag(GetResult getResult) =>
        getResult switch
        {
            GetResult.GetSuccess(var _, var edfiDoc, _, _) when edfiDoc["_etag"] is not null => edfiDoc[
                "_etag"
            ]!.GetValue<string>(),
            GetResult.GetSuccess => throw new InvalidOperationException(
                "Expected GET success document to contain '_etag' for parity assertion."
            ),
            _ => throw new InvalidOperationException(
                $"Expected GET success result for ETag parity assertion, but got '{getResult.GetType().Name}'."
            ),
        };

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

        // Body comparisons are etag-agnostic: the composed _etag derives from ContentVersion (not the
        // body), so it cannot be reconstructed from an expected request body. Drop it here so callers
        // compare the resource state and assert the _etag shape separately via AssertComposedEtag.
        foreach (
            var property in jsonObject
                .Where(static property => !string.Equals(property.Key, "_etag", StringComparison.Ordinal))
                .OrderBy(static property => property.Key, StringComparer.Ordinal)
        )
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
