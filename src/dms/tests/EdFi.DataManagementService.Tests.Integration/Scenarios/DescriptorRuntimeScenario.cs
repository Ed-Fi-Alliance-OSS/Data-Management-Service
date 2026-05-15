// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class DescriptorRuntimeScenario
{
    private const string DescriptorEndpoint = "/data/ed-fi/schoolTypeDescriptors";
    private const string MergeItemsEndpoint = "/data/ed-fi/profileRootOnlyMergeItems";
    private const string StandardJsonContentType = "application/json";
    private const string ImmutableIdentityProblemType =
        "urn:ed-fi:api:bad-request:data-validation-failed:key-change-not-supported";

    public static async Task It_creates_and_reads_a_descriptor(ApiIntegrationHarness harness)
    {
        DescriptorValues descriptor = CreateDescriptorValues("create-read");

        (string locationPath, string etag) = await CreateDescriptorAsync(harness, descriptor);

        locationPath.Should().NotBeNullOrWhiteSpace("a descriptor POST must return a Location header");
        etag.Should().NotBeNullOrWhiteSpace("a descriptor POST must emit an ETag header");

        JsonObject returned = await GetJsonObjectAsync(harness, locationPath);
        AssertDescriptorFields(returned, descriptor);
        returned["id"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        returned["_etag"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        returned["_lastModifiedDate"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    public static async Task It_updates_descriptor_non_identity_fields_and_advances_metadata(
        ApiIntegrationHarness harness
    )
    {
        DescriptorValues initial = CreateDescriptorValues("changed-put");
        (string locationPath, string initialEtag) = await CreateDescriptorAsync(harness, initial);

        JsonObject created = await GetJsonObjectAsync(harness, locationPath);
        string resourceId = created["id"]!.GetValue<string>();
        DocumentMetadata initialMetadata = await ReadDocumentMetadataAsync(harness, resourceId);

        DescriptorValues updated = initial with
        {
            ShortDescription = $"{initial.ShortDescription}-updated",
            Description = $"{initial.Description}-updated",
            EffectiveBeginDate = "2025-02-03",
            EffectiveEndDate = "2025-12-30",
        };
        using HttpResponseMessage putResponse = await PutDescriptorAsync(
            harness,
            locationPath,
            resourceId,
            updated,
            initialEtag
        );
        string putBody = await putResponse.Content.ReadAsStringAsync();

        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, putBody);
        putResponse.TryReadRawEtag(out string putEtag).Should().BeTrue("PUT must return the new ETag");
        putEtag.Should().NotBe(initialEtag, "changed descriptor content must advance the ETag");

        JsonObject returned = await GetJsonObjectAsync(harness, locationPath);
        AssertDescriptorFields(returned, updated);

        DocumentMetadata updatedMetadata = await ReadDocumentMetadataAsync(harness, resourceId);
        updatedMetadata
            .ContentVersion.Should()
            .BeGreaterThan(initialMetadata.ContentVersion, "changed descriptor PUT must advance metadata");
        updatedMetadata
            .ContentLastModifiedAt.Should()
            .BeAfter(
                initialMetadata.ContentLastModifiedAt,
                "changed descriptor PUT must stamp a later modification time"
            );
    }

    public static async Task It_preserves_metadata_for_unchanged_descriptor_put(ApiIntegrationHarness harness)
    {
        DescriptorValues descriptor = CreateDescriptorValues("unchanged-put");
        (string locationPath, string initialEtag) = await CreateDescriptorAsync(harness, descriptor);

        JsonObject created = await GetJsonObjectAsync(harness, locationPath);
        string resourceId = created["id"]!.GetValue<string>();
        DocumentMetadata initialMetadata = await ReadDocumentMetadataAsync(harness, resourceId);

        using HttpResponseMessage putResponse = await PutDescriptorAsync(
            harness,
            locationPath,
            resourceId,
            descriptor,
            initialEtag
        );
        string putBody = await putResponse.Content.ReadAsStringAsync();

        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, putBody);
        putResponse
            .TryReadRawEtag(out string putEtag)
            .Should()
            .BeTrue("unchanged descriptor PUT must return the current ETag");
        putEtag
            .Should()
            .Be(initialEtag, "unchanged descriptor content should preserve the representation ETag");

        DocumentMetadata afterNoOpPutMetadata = await ReadDocumentMetadataAsync(harness, resourceId);
        afterNoOpPutMetadata
            .ContentVersion.Should()
            .Be(initialMetadata.ContentVersion, "unchanged descriptor PUT must not stamp content version");
        afterNoOpPutMetadata
            .ContentLastModifiedAt.Should()
            .Be(
                initialMetadata.ContentLastModifiedAt,
                "unchanged descriptor PUT must not stamp content modified time"
            );
    }

    public static async Task It_rejects_descriptor_identity_changes(ApiIntegrationHarness harness)
    {
        DescriptorValues descriptor = CreateDescriptorValues("identity-change");
        (string locationPath, string initialEtag) = await CreateDescriptorAsync(harness, descriptor);

        JsonObject created = await GetJsonObjectAsync(harness, locationPath);
        string resourceId = created["id"]!.GetValue<string>();
        DescriptorValues changedIdentity = descriptor with { CodeValue = $"{descriptor.CodeValue}-changed" };

        using HttpResponseMessage putResponse = await PutDescriptorAsync(
            harness,
            locationPath,
            resourceId,
            changedIdentity,
            initialEtag
        );
        string putBody = await putResponse.Content.ReadAsStringAsync();

        putResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest, putBody);
        JsonObject problem = JsonNode.Parse(putBody)!.AsObject();
        problem["type"]!.GetValue<string>().Should().Be(ImmutableIdentityProblemType);
        problem["title"]!.GetValue<string>().Should().Be("Key Change Not Supported");
        problem["status"]!.GetValue<int>().Should().Be(400);
        problem["detail"]!
            .GetValue<string>()
            .Should()
            .Be(
                "Identity of resource 'Ed-Fi.SchoolTypeDescriptor' cannot be changed. "
                    + "Descriptor identity fields (Namespace, CodeValue) are immutable on PUT."
            );
    }

    public static async Task It_filters_and_pages_descriptor_queries(ApiIntegrationHarness harness)
    {
        string namespaceName = CreateNamespace("query");
        DescriptorValues[] descriptors =
        [
            CreateDescriptorValues(
                "query-alpha",
                namespaceName,
                codeValue: "DMS-1025-Query-A",
                description: "DMS-1025 query description alpha",
                effectiveBeginDate: "2025-03-01",
                effectiveEndDate: "2025-08-31"
            ),
            CreateDescriptorValues(
                "query-bravo",
                namespaceName,
                codeValue: "DMS-1025-Query-B",
                description: "DMS-1025 query description bravo",
                effectiveBeginDate: "2025-03-02",
                effectiveEndDate: "2025-09-30"
            ),
            CreateDescriptorValues(
                "query-charlie",
                namespaceName,
                codeValue: "DMS-1025-Query-C",
                description: "DMS-1025 query description charlie",
                effectiveBeginDate: "2025-03-03",
                effectiveEndDate: "2025-10-31"
            ),
        ];

        foreach (DescriptorValues descriptor in descriptors)
        {
            await CreateDescriptorAsync(harness, descriptor);
        }

        JsonArray namespaceMatches = await GetJsonArrayAsync(
            harness,
            $"{DescriptorEndpoint}?namespace={Escape(namespaceName)}"
        );
        namespaceMatches.Count.Should().Be(descriptors.Length);
        namespaceMatches
            .Select(node => node!["codeValue"]!.GetValue<string>())
            .Should()
            .BeEquivalentTo(descriptors.Select(descriptor => descriptor.CodeValue));

        await AssertSingleQueryMatchAsync(harness, "codeValue", descriptors[1].CodeValue, descriptors[1]);
        await AssertSingleQueryMatchAsync(harness, "description", descriptors[2].Description, descriptors[2]);
        await AssertSingleQueryMatchAsync(
            harness,
            "effectiveBeginDate",
            descriptors[0].EffectiveBeginDate,
            descriptors[0]
        );
        await AssertSingleQueryMatchAsync(
            harness,
            "effectiveEndDate",
            descriptors[1].EffectiveEndDate,
            descriptors[1]
        );

        JsonArray firstPage = await GetJsonArrayAsync(
            harness,
            $"{DescriptorEndpoint}?namespace={Escape(namespaceName)}&offset=0&limit=2"
        );
        JsonArray secondPage = await GetJsonArrayAsync(
            harness,
            $"{DescriptorEndpoint}?namespace={Escape(namespaceName)}&offset=2&limit=2"
        );

        firstPage.Count.Should().Be(2, "limit=2 returns the first two descriptors in DocumentId ASC order");
        secondPage.Count.Should().Be(1, "offset=2 skips the first two descriptors in DocumentId ASC order");

        string[] firstPageIds = firstPage.Select(node => node!["id"]!.GetValue<string>()).ToArray();
        string[] secondPageIds = secondPage.Select(node => node!["id"]!.GetValue<string>()).ToArray();
        firstPageIds.Should().NotIntersectWith(secondPageIds, "page windows must not overlap");

        firstPage
            .Concat(secondPage)
            .Select(node => node!["codeValue"]!.GetValue<string>())
            .Should()
            .BeEquivalentTo(descriptors.Select(descriptor => descriptor.CodeValue));
    }

    public static async Task It_requires_descriptor_reference_resolution_before_resource_write(
        ApiIntegrationHarness harness
    )
    {
        DescriptorValues descriptor = CreateDescriptorValues("reference-resolution");
        string descriptorUri = $"{descriptor.Namespace}#{descriptor.CodeValue}";
        var payload = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = 1025,
            ["displayName"] = "DMS-1025 descriptor reference resolution",
            ["primarySchoolTypeDescriptor"] = descriptorUri,
            ["secondarySchoolTypeDescriptor"] = descriptorUri,
        };

        using HttpResponseMessage rejectedResponse = await PostJsonAsync(
            harness,
            MergeItemsEndpoint,
            payload
        );
        string rejectedBody = await rejectedResponse.Content.ReadAsStringAsync();

        rejectedResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest, rejectedBody);
        JsonObject problem = JsonNode.Parse(rejectedBody)!.AsObject();
        problem["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:bad-request");
        rejectedBody
            .Should()
            .Contain("validationErrors", "descriptor reference failures must explain the invalid path");
        rejectedBody.Should().Contain("primarySchoolTypeDescriptor");
        rejectedBody.Should().Contain(descriptorUri.ToLowerInvariant());
        rejectedBody.Should().NotContain("unresolved-reference");

        await CreateDescriptorAsync(harness, descriptor);

        using HttpResponseMessage createdResponse = await PostJsonAsync(harness, MergeItemsEndpoint, payload);
        string createdBody = await createdResponse.Content.ReadAsStringAsync();

        createdResponse
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Created,
                $"the rejected write must not create the resource before descriptor resolution succeeds. Body: {createdBody}"
            );
    }

    private static async Task<(string LocationPath, string Etag)> CreateDescriptorAsync(
        ApiIntegrationHarness harness,
        DescriptorValues descriptor
    )
    {
        using HttpResponseMessage createResponse = await PostJsonAsync(
            harness,
            DescriptorEndpoint,
            CreateDescriptorPayload(descriptor)
        );
        string createBody = await createResponse.Content.ReadAsStringAsync();

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, createBody);
        createResponse.Headers.Location.Should().NotBeNull();
        createResponse.TryReadRawEtag(out string etag).Should().BeTrue("descriptor POST must emit an ETag");

        return (ToPath(createResponse.Headers.Location!), etag);
    }

    private static async Task<HttpResponseMessage> PutDescriptorAsync(
        ApiIntegrationHarness harness,
        string locationPath,
        string resourceId,
        DescriptorValues descriptor,
        string ifMatch
    )
    {
        JsonObject payload = CreateDescriptorPayload(descriptor);
        payload["id"] = resourceId;

        using var putContent = new StringContent(
            payload.ToJsonString(),
            Encoding.UTF8,
            StandardJsonContentType
        );
        var request = new HttpRequestMessage(HttpMethod.Put, locationPath) { Content = putContent };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        return await harness.HttpClient.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType);
        return await harness.HttpClient.PostAsync(endpoint, content);
    }

    private static async Task<JsonObject> GetJsonObjectAsync(ApiIntegrationHarness harness, string endpoint)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(endpoint);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static async Task<JsonArray> GetJsonArrayAsync(ApiIntegrationHarness harness, string endpoint)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(endpoint);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsArray();
    }

    private static async Task AssertSingleQueryMatchAsync(
        ApiIntegrationHarness harness,
        string queryField,
        string value,
        DescriptorValues expected
    )
    {
        JsonArray matches = await GetJsonArrayAsync(
            harness,
            $"{DescriptorEndpoint}?{queryField}={Escape(value)}"
        );

        matches.Count.Should().Be(1, $"{queryField} should uniquely identify the seeded descriptor");
        AssertDescriptorFields(matches[0]!.AsObject(), expected);
    }

    private static async Task<DocumentMetadata> ReadDocumentMetadataAsync(
        ApiIntegrationHarness harness,
        string documentUuid
    )
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT "ContentVersion", "ContentLastModifiedAt"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid
            """;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@documentUuid";
        parameter.Value = Guid.Parse(documentUuid);
        command.Parameters.Add(parameter);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the descriptor document row must exist");

        return new DocumentMetadata(
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            ToDateTimeOffset(reader.GetValue(1))
        );
    }

    private static JsonObject CreateDescriptorPayload(DescriptorValues descriptor) =>
        new()
        {
            ["namespace"] = descriptor.Namespace,
            ["codeValue"] = descriptor.CodeValue,
            ["shortDescription"] = descriptor.ShortDescription,
            ["description"] = descriptor.Description,
            ["effectiveBeginDate"] = descriptor.EffectiveBeginDate,
            ["effectiveEndDate"] = descriptor.EffectiveEndDate,
        };

    private static DescriptorValues CreateDescriptorValues(
        string scenario,
        string? namespaceName = null,
        string? codeValue = null,
        string? description = null,
        string effectiveBeginDate = "2025-01-01",
        string effectiveEndDate = "2025-12-31"
    )
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string finalCodeValue = codeValue ?? $"DMS-1025-{scenario}-{suffix}";
        return new DescriptorValues(
            Namespace: namespaceName ?? CreateNamespace(scenario),
            CodeValue: finalCodeValue,
            ShortDescription: $"DMS-1025 {scenario} {suffix}",
            Description: description ?? $"DMS-1025 descriptor runtime {scenario} {suffix}",
            EffectiveBeginDate: effectiveBeginDate,
            EffectiveEndDate: effectiveEndDate
        );
    }

    private static string CreateNamespace(string scenario) =>
        $"uri://ed-fi.org/SchoolTypeDescriptor/DMS-1025/{scenario}/{Guid.NewGuid():N}";

    private static void AssertDescriptorFields(JsonObject actual, DescriptorValues expected)
    {
        actual["namespace"]!.GetValue<string>().Should().Be(expected.Namespace);
        actual["codeValue"]!.GetValue<string>().Should().Be(expected.CodeValue);
        actual["shortDescription"]!.GetValue<string>().Should().Be(expected.ShortDescription);
        actual["description"]!.GetValue<string>().Should().Be(expected.Description);
        actual["effectiveBeginDate"]!.GetValue<string>().Should().Be(expected.EffectiveBeginDate);
        actual["effectiveEndDate"]!.GetValue<string>().Should().Be(expected.EffectiveEndDate);
    }

    private static string ToPath(Uri location) =>
        location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static DateTimeOffset ToDateTimeOffset(object value) =>
        value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException(
                $"Unsupported ContentLastModifiedAt value type '{value.GetType().FullName}'."
            ),
        };

    private sealed record DescriptorValues(
        string Namespace,
        string CodeValue,
        string ShortDescription,
        string Description,
        string EffectiveBeginDate,
        string EffectiveEndDate
    );

    private sealed record DocumentMetadata(long ContentVersion, DateTimeOffset ContentLastModifiedAt);
}
