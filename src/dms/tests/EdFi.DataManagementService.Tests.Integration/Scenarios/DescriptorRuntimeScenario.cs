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

        (string locationPath, string postEtag) = await CreateDescriptorAsync(harness, descriptor);

        locationPath.Should().NotBeNullOrWhiteSpace("a descriptor POST must return a Location header");
        postEtag.Should().NotBeNullOrWhiteSpace("a descriptor POST must emit an ETag header");

        JsonObject returned = await GetJsonObjectAsync(harness, locationPath);
        AssertDescriptorFields(returned, descriptor);
        returned["id"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        returned["_etag"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        returned["_lastModifiedDate"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        returned["_etag"]!
            .GetValue<string>()
            .Should()
            .Be(postEtag, "POST ETag header must match the subsequent GET _etag");
    }

    public static async Task It_returns_not_modified_for_a_matching_descriptor_if_none_match(
        ApiIntegrationHarness harness
    )
    {
        DescriptorValues descriptor = CreateDescriptorValues("conditional-get");
        (string locationPath, _) = await CreateDescriptorAsync(harness, descriptor);

        using HttpResponseMessage initialGetResponse = await harness.HttpClient.GetAsync(locationPath);
        string initialGetBody = await initialGetResponse.Content.ReadAsStringAsync();
        initialGetResponse.StatusCode.Should().Be(HttpStatusCode.OK, initialGetBody);
        initialGetResponse
            .TryReadRawEtag(out string getEtag)
            .Should()
            .BeTrue("descriptor GET-by-id must emit an ETag header");
        JsonObject returned = JsonNode.Parse(initialGetBody)!.AsObject();
        returned["_etag"]!
            .GetValue<string>()
            .Should()
            .Be(getEtag, "the descriptor GET header and response body must use the same ETag");

        using var conditionalGetRequest = new HttpRequestMessage(HttpMethod.Get, locationPath);
        conditionalGetRequest.Headers.TryAddWithoutValidation("If-None-Match", $"\"{getEtag}\"");
        using HttpResponseMessage conditionalGetResponse = await harness.HttpClient.SendAsync(
            conditionalGetRequest
        );

        conditionalGetResponse.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await conditionalGetResponse.Content.ReadAsStringAsync())
            .Should()
            .BeEmpty("a 304 descriptor response must not carry a body");
        conditionalGetResponse
            .TryReadRawEtag(out string notModifiedEtag)
            .Should()
            .BeTrue("a 304 descriptor response must still carry the current ETag header");
        notModifiedEtag.Should().Be(getEtag);
    }

    public static async Task It_updates_descriptor_non_identity_fields_and_advances_metadata(
        ApiIntegrationHarness harness
    )
    {
        DescriptorValues initial = CreateDescriptorValues("changed-put");
        (string locationPath, string initialPostEtag) = await CreateDescriptorAsync(harness, initial);

        JsonObject created = await GetJsonObjectAsync(harness, locationPath);
        string resourceId = created["id"]!.GetValue<string>();
        string initialGetEtag = created["_etag"]!.GetValue<string>();
        string initialLastModifiedDate = created["_lastModifiedDate"]!.GetValue<string>();
        initialPostEtag.Should().Be(initialGetEtag, "POST ETag header must match the initial GET _etag");
        DocumentMetadata initialMetadata = await ReadDocumentMetadataAsync(harness, resourceId);

        DescriptorValues updated = initial with
        {
            ShortDescription = $"{initial.ShortDescription}-updated",
            Description = $"{initial.Description}-updated",
            EffectiveBeginDate = "2025-02-03",
            EffectiveEndDate = "2025-12-30",
        };

        // _lastModifiedDate is stamped at second precision, so wait for the UTC second to tick before the PUT.
        await WaitForNextWireSecondAsync();

        using HttpResponseMessage putResponse = await PutDescriptorAsync(
            harness,
            locationPath,
            resourceId,
            updated,
            initialPostEtag
        );
        string putBody = await putResponse.Content.ReadAsStringAsync();

        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, putBody);
        putResponse.TryReadRawEtag(out string putEtag).Should().BeTrue("PUT must return the new ETag");
        putEtag.Should().NotBe(initialPostEtag, "changed descriptor content must advance the ETag");

        JsonObject returned = await GetJsonObjectAsync(harness, locationPath);
        AssertDescriptorFields(returned, updated);
        string updatedGetEtag = returned["_etag"]!.GetValue<string>();
        updatedGetEtag
            .Should()
            .NotBe(initialGetEtag, "changed descriptor PUT must advance the GET response _etag");
        putEtag.Should().Be(updatedGetEtag, "PUT ETag header must match the subsequent GET _etag");
        returned["_lastModifiedDate"]!
            .GetValue<string>()
            .Should()
            .NotBe(
                initialLastModifiedDate,
                "changed descriptor PUT must advance the GET response _lastModifiedDate"
            );

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
        (string locationPath, string initialPostEtag) = await CreateDescriptorAsync(harness, descriptor);

        JsonObject created = await GetJsonObjectAsync(harness, locationPath);
        string resourceId = created["id"]!.GetValue<string>();
        string initialGetEtag = created["_etag"]!.GetValue<string>();
        string initialLastModifiedDate = created["_lastModifiedDate"]!.GetValue<string>();
        initialPostEtag.Should().Be(initialGetEtag, "POST ETag header must match the initial GET _etag");
        DocumentMetadata initialMetadata = await ReadDocumentMetadataAsync(harness, resourceId);

        using HttpResponseMessage putResponse = await PutDescriptorAsync(
            harness,
            locationPath,
            resourceId,
            descriptor,
            initialPostEtag
        );
        string putBody = await putResponse.Content.ReadAsStringAsync();

        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, putBody);
        putResponse
            .TryReadRawEtag(out string putEtag)
            .Should()
            .BeTrue("unchanged descriptor PUT must return the current ETag");
        putEtag
            .Should()
            .Be(initialPostEtag, "unchanged descriptor content should preserve the representation ETag");

        JsonObject afterNoOpPut = await GetJsonObjectAsync(harness, locationPath);
        AssertDescriptorFields(afterNoOpPut, descriptor);
        string afterNoOpGetEtag = afterNoOpPut["_etag"]!.GetValue<string>();
        afterNoOpGetEtag.Should().Be(initialGetEtag);
        afterNoOpPut["_lastModifiedDate"]!.GetValue<string>().Should().Be(initialLastModifiedDate);
        putEtag.Should().Be(afterNoOpGetEtag, "no-op PUT ETag header must match the subsequent GET _etag");

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
        await AssertDescriptorIdentityChangeRejectedAsync(
            harness,
            CreateDescriptorValues("identity-code-value-change", codeValue: "DMS-1025-code-a"),
            static descriptor => descriptor with { CodeValue = "DMS-1025-code-b" }
        );
        await AssertDescriptorIdentityChangeRejectedAsync(
            harness,
            CreateDescriptorValues("identity-namespace-change"),
            static descriptor => descriptor with { Namespace = CreateNamespace("identity-namespace-changed") }
        );
    }

    private static async Task AssertDescriptorIdentityChangeRejectedAsync(
        ApiIntegrationHarness harness,
        DescriptorValues descriptor,
        Func<DescriptorValues, DescriptorValues> mutateIdentity
    )
    {
        (string locationPath, string initialEtag) = await CreateDescriptorAsync(harness, descriptor);

        JsonObject created = await GetJsonObjectAsync(harness, locationPath);
        string resourceId = created["id"]!.GetValue<string>();
        string initialGetEtag = created["_etag"]!.GetValue<string>();
        string initialLastModifiedDate = created["_lastModifiedDate"]!.GetValue<string>();
        DocumentMetadata initialMetadata = await ReadDocumentMetadataAsync(harness, resourceId);
        DescriptorValues changedIdentity = mutateIdentity(descriptor);

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

        JsonObject afterRejection = await GetJsonObjectAsync(harness, locationPath);
        AssertDescriptorFields(afterRejection, descriptor);
        afterRejection["_etag"]!
            .GetValue<string>()
            .Should()
            .Be(initialGetEtag, "rejected identity-change PUT must not advance the GET _etag");
        afterRejection["_lastModifiedDate"]!
            .GetValue<string>()
            .Should()
            .Be(
                initialLastModifiedDate,
                "rejected identity-change PUT must not advance the GET _lastModifiedDate"
            );

        DocumentMetadata afterRejectionMetadata = await ReadDocumentMetadataAsync(harness, resourceId);
        afterRejectionMetadata
            .ContentVersion.Should()
            .Be(
                initialMetadata.ContentVersion,
                "rejected identity-change PUT must not stamp content version"
            );
        afterRejectionMetadata
            .ContentLastModifiedAt.Should()
            .Be(
                initialMetadata.ContentLastModifiedAt,
                "rejected identity-change PUT must not stamp content modified time"
            );
    }

    public static async Task It_filters_and_pages_descriptor_queries(ApiIntegrationHarness harness)
    {
        string namespaceName = CreateNamespace("query");
        string otherNamespaceName = CreateNamespace("other");
        DescriptorValues[] descriptors =
        [
            CreateDescriptorValues(
                "query-charlie",
                namespaceName,
                codeValue: "DMS-1025-Query-Charlie",
                description: "DMS-1025 query description charlie",
                effectiveBeginDate: "2025-03-03",
                effectiveEndDate: "2025-10-31"
            ),
            CreateDescriptorValues(
                "query-alpha",
                namespaceName,
                codeValue: "DMS-1025-Query-Alpha",
                description: "DMS-1025 query description alpha",
                effectiveBeginDate: "2025-03-01",
                effectiveEndDate: "2025-08-31"
            ),
            CreateDescriptorValues(
                "query-bravo",
                namespaceName,
                codeValue: "DMS-1025-Query-Bravo",
                description: "DMS-1025 query description bravo",
                effectiveBeginDate: "2025-03-02",
                effectiveEndDate: "2025-09-30"
            ),
        ];

        // Seeded in a different namespace, deliberately re-using descriptors[1].CodeValue so the
        // namespace-scoped assertions below prove the predicate excludes other-namespace rows both
        // standalone (?namespace=X) and when combined with codeValue (?namespace=X&codeValue=Y),
        // rather than relying on globally unique field values to mask a dropped namespace predicate.
        DescriptorValues otherNamespaceDescriptor = CreateDescriptorValues(
            "query-other-namespace",
            otherNamespaceName,
            codeValue: descriptors[1].CodeValue,
            description: "DMS-1025 query description foxtrot",
            effectiveBeginDate: "2025-03-04",
            effectiveEndDate: "2025-11-30"
        );
        await CreateDescriptorAsync(harness, otherNamespaceDescriptor);

        string[] insertionOrderIds = new string[descriptors.Length];
        for (int i = 0; i < descriptors.Length; i++)
        {
            (string descriptorPath, _) = await CreateDescriptorAsync(harness, descriptors[i]);
            insertionOrderIds[i] = descriptorPath.Split('/')[^1];
        }

        JsonArray namespaceMatches = await GetJsonArrayAsync(
            harness,
            $"{DescriptorEndpoint}?namespace={Escape(namespaceName)}"
        );
        namespaceMatches
            .Count.Should()
            .Be(
                descriptors.Length,
                "namespace filter must return exactly the in-namespace seed count, excluding the other-namespace distractor"
            );
        namespaceMatches
            .Select(node => node!["codeValue"]!.GetValue<string>())
            .Should()
            .BeEquivalentTo(descriptors.Select(descriptor => descriptor.CodeValue));

        JsonArray otherNamespaceMatches = await GetJsonArrayAsync(
            harness,
            $"{DescriptorEndpoint}?namespace={Escape(otherNamespaceName)}"
        );
        otherNamespaceMatches
            .Count.Should()
            .Be(1, "querying the other namespace must return only the descriptor seeded there");
        AssertDescriptorFields(otherNamespaceMatches[0]!.AsObject(), otherNamespaceDescriptor);

        await AssertSingleQueryMatchAsync(harness, namespaceName, "id", insertionOrderIds[1], descriptors[1]);
        await AssertSingleQueryMatchAsync(
            harness,
            namespaceName,
            "codeValue",
            descriptors[1].CodeValue,
            descriptors[1]
        );
        await AssertSingleQueryMatchAsync(
            harness,
            namespaceName,
            "shortDescription",
            descriptors[0].ShortDescription,
            descriptors[0]
        );
        await AssertSingleQueryMatchAsync(
            harness,
            namespaceName,
            "description",
            descriptors[2].Description,
            descriptors[2]
        );
        await AssertSingleQueryMatchAsync(
            harness,
            namespaceName,
            "effectiveBeginDate",
            descriptors[0].EffectiveBeginDate,
            descriptors[0]
        );
        await AssertSingleQueryMatchAsync(
            harness,
            namespaceName,
            "effectiveEndDate",
            descriptors[1].EffectiveEndDate,
            descriptors[1]
        );

        using HttpResponseMessage totalCountResponse = await harness.HttpClient.GetAsync(
            $"{DescriptorEndpoint}?namespace={Escape(namespaceName)}&totalCount=true"
        );
        string totalCountBody = await totalCountResponse.Content.ReadAsStringAsync();
        totalCountResponse.StatusCode.Should().Be(HttpStatusCode.OK, totalCountBody);
        totalCountResponse
            .Headers.TryGetValues("Total-Count", out IEnumerable<string>? totalCountHeader)
            .Should()
            .BeTrue("totalCount=true must emit the Total-Count response header");
        totalCountHeader!
            .Single()
            .Should()
            .Be(
                descriptors.Length.ToString(CultureInfo.InvariantCulture),
                "Total-Count must reflect the namespace-scoped match count"
            );

        JsonArray firstPage = await GetJsonArrayAsync(
            harness,
            $"{DescriptorEndpoint}?namespace={Escape(namespaceName)}&offset=0&limit=2"
        );
        JsonArray secondPage = await GetJsonArrayAsync(
            harness,
            $"{DescriptorEndpoint}?namespace={Escape(namespaceName)}&offset=2&limit=2"
        );

        string[] firstPageIds = firstPage.Select(node => node!["id"]!.GetValue<string>()).ToArray();
        string[] secondPageIds = secondPage.Select(node => node!["id"]!.GetValue<string>()).ToArray();

        firstPageIds
            .Should()
            .Equal(
                new[] { insertionOrderIds[0], insertionOrderIds[1] },
                "limit=2 must return the first two descriptors in DocumentId ASC (insertion) order, not codeValue order"
            );
        secondPageIds
            .Should()
            .Equal(
                new[] { insertionOrderIds[2] },
                "offset=2 must return the remaining descriptor in DocumentId ASC (insertion) order"
            );
    }

    public static async Task It_deletes_a_descriptor(ApiIntegrationHarness harness)
    {
        DescriptorValues descriptor = CreateDescriptorValues("delete");

        (string locationPath, _) = await CreateDescriptorAsync(harness, descriptor);

        JsonObject created = await GetJsonObjectAsync(harness, locationPath);
        string resourceId = created["id"]!.GetValue<string>();
        (await CountDocumentRowsAsync(harness, resourceId))
            .Should()
            .Be(1, "the descriptor document row must exist before DELETE");

        using HttpResponseMessage deleteResponse = await harness.HttpClient.DeleteAsync(locationPath);
        string deleteBody = await deleteResponse.Content.ReadAsStringAsync();
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, deleteBody);

        using HttpResponseMessage getAfterDelete = await harness.HttpClient.GetAsync(locationPath);
        getAfterDelete
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound, "GET after a successful DELETE must return 404");

        (await CountDocumentRowsAsync(harness, resourceId))
            .Should()
            .Be(0, "descriptor DELETE must remove the dms.Document row");
    }

    public static async Task It_rejects_descriptor_delete_when_referenced(ApiIntegrationHarness harness)
    {
        DescriptorValues descriptor = CreateDescriptorValues("referenced-delete");
        string descriptorUri = $"{descriptor.Namespace}#{descriptor.CodeValue}";
        (string descriptorLocationPath, _) = await CreateDescriptorAsync(harness, descriptor);

        var mergeItemPayload = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = 1025,
            ["displayName"] = "DMS-1025 descriptor referenced-delete",
            ["primarySchoolTypeDescriptor"] = descriptorUri,
        };
        using HttpResponseMessage mergeItemCreate = await PostJsonAsync(
            harness,
            MergeItemsEndpoint,
            mergeItemPayload
        );
        string mergeItemBody = await mergeItemCreate.Content.ReadAsStringAsync();
        mergeItemCreate.StatusCode.Should().Be(HttpStatusCode.Created, mergeItemBody);

        using HttpResponseMessage deleteResponse = await harness.HttpClient.DeleteAsync(
            descriptorLocationPath
        );
        string deleteBody = await deleteResponse.Content.ReadAsStringAsync();
        deleteResponse
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Conflict,
                "deleting a descriptor referenced by an existing resource must be rejected"
            );

        JsonObject problem = JsonNode.Parse(deleteBody)!.AsObject();
        problem["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:data-conflict:dependent-item-exists");
        problem["title"]!.GetValue<string>().Should().Be("Dependent Item Exists");
        problem["status"]!.GetValue<int>().Should().Be(409);
        problem["detail"]!
            .GetValue<string>()
            .Should()
            .Contain(
                "ProfileRootOnlyMergeItem",
                "the conflict detail must identify the dependent resource by PascalCase resource name"
            );

        using HttpResponseMessage descriptorAfterDelete = await harness.HttpClient.GetAsync(
            descriptorLocationPath
        );
        descriptorAfterDelete
            .StatusCode.Should()
            .Be(HttpStatusCode.OK, "the referenced descriptor must remain after the rejected DELETE");
    }

    public static async Task It_requires_descriptor_reference_resolution_before_resource_write(
        ApiIntegrationHarness harness
    )
    {
        DescriptorValues descriptor = CreateDescriptorValues("reference-resolution");
        string descriptorUri = $"{descriptor.Namespace}#{descriptor.CodeValue}";

        var payloadWithoutDescriptor = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = 1025,
            ["displayName"] = "DMS-1025 descriptor reference resolution",
        };

        using HttpResponseMessage missingFieldResponse = await PostJsonAsync(
            harness,
            MergeItemsEndpoint,
            payloadWithoutDescriptor
        );
        string missingFieldBody = await missingFieldResponse.Content.ReadAsStringAsync();

        missingFieldResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest, missingFieldBody);
        JsonObject missingFieldProblem = JsonNode.Parse(missingFieldBody)!.AsObject();
        missingFieldProblem["type"]!
            .GetValue<string>()
            .Should()
            .Be("urn:ed-fi:api:bad-request:data-validation-failed");
        missingFieldBody
            .Should()
            .Contain(
                "primarySchoolTypeDescriptor is required.",
                "a required-descriptor schema must reject a payload that omits the descriptor field"
            );

        var payload = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = 1025,
            ["displayName"] = "DMS-1025 descriptor reference resolution",
            ["primarySchoolTypeDescriptor"] = descriptorUri,
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
        createdResponse.Headers.Location.Should().NotBeNull();

        JsonObject persistedMergeItem = await GetJsonObjectAsync(
            harness,
            ToPath(createdResponse.Headers.Location!)
        );
        persistedMergeItem["primarySchoolTypeDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(
                descriptorUri,
                "the persisted required-descriptor reference must round-trip the resolved descriptor URI"
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
        string namespaceName,
        string queryField,
        string value,
        DescriptorValues expected
    )
    {
        JsonArray matches = await GetJsonArrayAsync(
            harness,
            $"{DescriptorEndpoint}?namespace={Escape(namespaceName)}&{queryField}={Escape(value)}"
        );

        matches.Count.Should().Be(1, $"{queryField} should uniquely identify the seeded descriptor");
        AssertDescriptorFields(matches[0]!.AsObject(), expected);
    }

    private static async Task<int> CountDocumentRowsAsync(ApiIntegrationHarness harness, string documentUuid)
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM "dms"."Document" WHERE "DocumentUuid" = @documentUuid
            """;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@documentUuid";
        parameter.Value = Guid.Parse(documentUuid);
        command.Parameters.Add(parameter);

        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
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

    private static Task WaitForNextWireSecondAsync() =>
        // _lastModifiedDate is stamped by the database clock at second precision. Sleep an unconditional
        // fixed delay (no test-host clock comparison) so any subsequent DB-side stamp lands in a later
        // wire second, regardless of any skew between the test host and database container clocks.
        Task.Delay(TimeSpan.FromMilliseconds(1500));

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
