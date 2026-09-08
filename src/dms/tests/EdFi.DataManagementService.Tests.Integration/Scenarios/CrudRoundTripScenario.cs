// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Basic CRUD smoke: POST a Student then GET-by-id from the Location header,
/// asserting the request fields round-trip through the real DMS HTTP pipeline
/// against the leased database. Hard-coded for the ProfileRootOnlyMerge fixture's
/// Student shape: project endpoint <c>ed-fi</c>, resource <c>students</c>, required
/// identity <c>studentUniqueId</c>, required non-identity <c>firstName</c>.
/// </summary>
internal static class CrudRoundTripScenario
{
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string StudentUniqueId = "smoke-001";
    private const string FirstName = "Ada";
    private const string MergeItemsEndpoint = "/data/ed-fi/profileRootOnlyMergeItems";
    private const string MissingStudentUniqueId = "smoke-missing-student-001";

    public static async Task It_creates_and_reads_a_student(ApiIntegrationHarness harness)
    {
        var payload = new JsonObject { ["studentUniqueId"] = StudentUniqueId, ["firstName"] = FirstName };

        using var createContent = new StringContent(
            payload.ToJsonString(),
            Encoding.UTF8,
            "application/json"
        );
        using HttpResponseMessage createResponse = await harness.HttpClient.PostAsync(
            StudentsEndpoint,
            createContent
        );
        string createBody = await createResponse.Content.ReadAsStringAsync();

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, createBody);
        createResponse.Headers.Location.Should().NotBeNull();
        createResponse.TryReadRawEtag(out _).Should().BeTrue("a POST create must emit an ETag header");

        string locationPath = createResponse.Headers.Location!.IsAbsoluteUri
            ? createResponse.Headers.Location!.AbsolutePath
            : createResponse.Headers.Location!.OriginalString;

        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(locationPath);
        string getBody = await getResponse.Content.ReadAsStringAsync();

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, getBody);

        JsonNode? returnedNode = JsonNode.Parse(getBody);
        returnedNode.Should().NotBeNull("GET response must be a JSON document");
        JsonObject returned = returnedNode!.AsObject();
        returned["studentUniqueId"]!.GetValue<string>().Should().Be(StudentUniqueId);
        returned["firstName"]!.GetValue<string>().Should().Be(FirstName);

        int persistedRowCount = await CountStudentRowsAsync(harness, StudentUniqueId);
        persistedRowCount.Should().Be(1, "the POSTed Student must be persisted in the relational backend");
    }

    public static async Task It_updates_a_student_via_put(ApiIntegrationHarness harness)
    {
        const string putFirstName = "Ada-renamed";

        var initialPayload = new JsonObject
        {
            ["studentUniqueId"] = StudentUniqueId,
            ["firstName"] = FirstName,
        };
        using var createContent = new StringContent(
            initialPayload.ToJsonString(),
            Encoding.UTF8,
            "application/json"
        );
        using HttpResponseMessage createResponse = await harness.HttpClient.PostAsync(
            StudentsEndpoint,
            createContent
        );
        createResponse
            .StatusCode.Should()
            .Be(HttpStatusCode.Created, await createResponse.Content.ReadAsStringAsync());

        string locationPath = createResponse.Headers.Location!.IsAbsoluteUri
            ? createResponse.Headers.Location!.AbsolutePath
            : createResponse.Headers.Location!.OriginalString;
        createResponse
            .TryReadRawEtag(out string initialEtag)
            .Should()
            .BeTrue("the initial POST must emit an ETag so PUT can If-Match against it");
        string resourceId = locationPath.Split('/')[^1];

        var putPayload = new JsonObject
        {
            ["id"] = resourceId,
            ["studentUniqueId"] = StudentUniqueId,
            ["firstName"] = putFirstName,
        };
        using var putContent = new StringContent(
            putPayload.ToJsonString(),
            Encoding.UTF8,
            "application/json"
        );
        using var putRequest = new HttpRequestMessage(HttpMethod.Put, locationPath) { Content = putContent };
        // If-Match is a request header, not a content header; setting it on
        // StringContent.Headers would silently no-op.
        putRequest.Headers.TryAddWithoutValidation("If-Match", initialEtag);

        using HttpResponseMessage putResponse = await harness.HttpClient.SendAsync(putRequest);
        string putBody = await putResponse.Content.ReadAsStringAsync();
        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, putBody);
        putResponse.TryReadRawEtag(out string putEtag).Should().BeTrue("PUT must return the new ETag");
        putEtag.Should().NotBe(initialEtag, "PUT must advance the ETag");

        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(locationPath);
        string getBody = await getResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, getBody);
        JsonObject returned = JsonNode.Parse(getBody)!.AsObject();
        returned["firstName"]!.GetValue<string>().Should().Be(putFirstName);
    }

    public static async Task It_upserts_a_student_via_post(ApiIntegrationHarness harness)
    {
        const string upsertUniqueId = "smoke-upsert-001";
        const string firstNameInitial = "Ada";
        const string firstNameUpdated = "Grace";

        var insertPayload = new JsonObject
        {
            ["studentUniqueId"] = upsertUniqueId,
            ["firstName"] = firstNameInitial,
        };
        using var insertContent = new StringContent(
            insertPayload.ToJsonString(),
            Encoding.UTF8,
            "application/json"
        );
        using HttpResponseMessage insertResponse = await harness.HttpClient.PostAsync(
            StudentsEndpoint,
            insertContent
        );
        string insertBody = await insertResponse.Content.ReadAsStringAsync();
        insertResponse.StatusCode.Should().Be(HttpStatusCode.Created, insertBody);
        insertResponse.Headers.Location.Should().NotBeNull("the initial POST must return a Location header");
        insertResponse
            .TryReadRawEtag(out string insertEtag)
            .Should()
            .BeTrue("the initial POST must emit an ETag header");

        string insertLocationPath = insertResponse.Headers.Location!.IsAbsoluteUri
            ? insertResponse.Headers.Location!.AbsolutePath
            : insertResponse.Headers.Location!.OriginalString;

        var upsertPayload = new JsonObject
        {
            ["studentUniqueId"] = upsertUniqueId,
            ["firstName"] = firstNameUpdated,
        };
        using var upsertContent = new StringContent(
            upsertPayload.ToJsonString(),
            Encoding.UTF8,
            "application/json"
        );
        using HttpResponseMessage upsertResponse = await harness.HttpClient.PostAsync(
            StudentsEndpoint,
            upsertContent
        );
        string upsertBody = await upsertResponse.Content.ReadAsStringAsync();
        upsertResponse
            .StatusCode.Should()
            .Be(
                HttpStatusCode.OK,
                $"POST on an existing natural key must upsert through the update path, not insert a duplicate or fail as conflict. Body: {upsertBody}"
            );
        upsertResponse
            .Headers.Location.Should()
            .NotBeNull(
                "the upsert-update POST must return a Location header pointing at the existing document"
            );
        string upsertLocationPath = upsertResponse.Headers.Location!.IsAbsoluteUri
            ? upsertResponse.Headers.Location!.AbsolutePath
            : upsertResponse.Headers.Location!.OriginalString;
        upsertLocationPath
            .Should()
            .Be(
                insertLocationPath,
                "POST upsert must keep the original resource id rather than mint a new one"
            );
        upsertResponse
            .TryReadRawEtag(out string upsertEtag)
            .Should()
            .BeTrue("POST upsert-update must emit the new ETag");
        upsertEtag.Should().NotBe(insertEtag, "POST upsert-update must advance the ETag");

        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(insertLocationPath);
        string getBody = await getResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, getBody);
        JsonObject returned = JsonNode.Parse(getBody)!.AsObject();
        returned["studentUniqueId"]!.GetValue<string>().Should().Be(upsertUniqueId);
        returned["firstName"]!.GetValue<string>().Should().Be(firstNameUpdated);

        int persistedRowCount = await CountStudentRowsAsync(harness, upsertUniqueId);
        persistedRowCount
            .Should()
            .Be(1, "POST upsert-update must not create a duplicate relational row for the same natural key");
    }

    public static async Task It_deletes_a_student(ApiIntegrationHarness harness)
    {
        var payload = new JsonObject { ["studentUniqueId"] = StudentUniqueId, ["firstName"] = FirstName };
        using var createContent = new StringContent(
            payload.ToJsonString(),
            Encoding.UTF8,
            "application/json"
        );
        using HttpResponseMessage createResponse = await harness.HttpClient.PostAsync(
            StudentsEndpoint,
            createContent
        );
        createResponse
            .StatusCode.Should()
            .Be(HttpStatusCode.Created, await createResponse.Content.ReadAsStringAsync());

        string locationPath = createResponse.Headers.Location!.IsAbsoluteUri
            ? createResponse.Headers.Location!.AbsolutePath
            : createResponse.Headers.Location!.OriginalString;

        using HttpResponseMessage deleteResponse = await harness.HttpClient.DeleteAsync(locationPath);
        string deleteBody = await deleteResponse.Content.ReadAsStringAsync();
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, deleteBody);

        using HttpResponseMessage getAfterDelete = await harness.HttpClient.GetAsync(locationPath);
        getAfterDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);

        int persistedRowCount = await CountStudentRowsAsync(harness, StudentUniqueId);
        persistedRowCount.Should().Be(0, "DELETE must remove the row from the relational backend");
    }

    public static async Task It_pages_students_via_query(ApiIntegrationHarness harness)
    {
        // Seed three students serially so their surrogate DocumentIds ascend in seed order — but in
        // deliberately non-lexical natural-key order (003, 001, 002), so a wrong deterministic sort
        // (for example ORDER BY the studentUniqueId natural key) produces different windows than the
        // required DocumentId order on BOTH engines. Then update the FIRST-seeded student with a
        // changed non-identity field: on PostgreSQL the MVCC update relocates that tuple to the heap
        // tail while its DocumentId is unchanged, so a bare unordered scan also fails. Each
        // limit/offset window is asserted twice to prove repeat stability, and the exact ordered
        // windows together prove complete, non-overlapping coverage on both engines.
        string[] ids = ["smoke-page-003", "smoke-page-001", "smoke-page-002"];
        string? firstStudentLocationPath = null;
        foreach (string id in ids)
        {
            var payload = new JsonObject { ["studentUniqueId"] = id, ["firstName"] = $"Name-{id}" };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await harness.HttpClient.PostAsync(
                StudentsEndpoint,
                content
            );
            response
                .StatusCode.Should()
                .Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

            if (id == ids[0])
            {
                firstStudentLocationPath = response.Headers.Location!.IsAbsoluteUri
                    ? response.Headers.Location!.AbsolutePath
                    : response.Headers.Location!.OriginalString;
            }
        }

        var updatedFirstStudent = new JsonObject
        {
            ["id"] = firstStudentLocationPath!.Split('/')[^1],
            ["studentUniqueId"] = ids[0],
            ["firstName"] = $"Relocated-{ids[0]}",
        };
        using var updateContent = new StringContent(
            updatedFirstStudent.ToJsonString(),
            Encoding.UTF8,
            "application/json"
        );
        using HttpResponseMessage updateResponse = await harness.HttpClient.PutAsync(
            firstStudentLocationPath,
            updateContent
        );
        updateResponse
            .StatusCode.Should()
            .Be(HttpStatusCode.NoContent, await updateResponse.Content.ReadAsStringAsync());

        for (int repeat = 0; repeat < 2; repeat++)
        {
            using HttpResponseMessage firstPage = await harness.HttpClient.GetAsync(
                $"{StudentsEndpoint}?offset=0&limit=2"
            );
            string firstPageBody = await firstPage.Content.ReadAsStringAsync();
            firstPage.StatusCode.Should().Be(HttpStatusCode.OK, firstPageBody);
            string[] firstPageIds = PageStudentUniqueIds(firstPageBody);
            firstPageIds
                .Should()
                .Equal(
                    ["smoke-page-003", "smoke-page-001"],
                    "limit=2 returns the first two seeded students in DocumentId order — not natural-key order — even after the first tuple is physically relocated (repeat {0})",
                    repeat
                );

            using HttpResponseMessage secondPage = await harness.HttpClient.GetAsync(
                $"{StudentsEndpoint}?offset=2&limit=2"
            );
            string secondPageBody = await secondPage.Content.ReadAsStringAsync();
            secondPage.StatusCode.Should().Be(HttpStatusCode.OK, secondPageBody);
            string[] secondPageIds = PageStudentUniqueIds(secondPageBody);
            secondPageIds
                .Should()
                .Equal(
                    ["smoke-page-002"],
                    "offset=2 returns the remaining seeded student, deterministically ordered after the first page (repeat {0})",
                    repeat
                );
        }
    }

    private static string[] PageStudentUniqueIds(string pageBody) =>
        [.. JsonNode.Parse(pageBody)!.AsArray().Select(node => node!["studentUniqueId"]!.GetValue<string>())];

    public static async Task It_rejects_create_with_missing_reference(ApiIntegrationHarness harness)
    {
        var payload = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = 1,
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = MissingStudentUniqueId },
        };
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(MergeItemsEndpoint, content);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        JsonObject problem = JsonNode.Parse(body)!.AsObject();
        problem["type"]!
            .GetValue<string>()
            .Should()
            .Be(
                "urn:ed-fi:api:data-conflict:unresolved-reference",
                "unresolved references must map to the data-conflict problem type, not generic bad-request"
            );
        problem["status"]!.GetValue<int>().Should().Be(409);
        body.Should()
            .Contain("studentReference", "the error body should identify the unresolved reference field");
    }

    public static async Task It_rejects_delete_when_referenced(ApiIntegrationHarness harness)
    {
        var studentPayload = new JsonObject
        {
            ["studentUniqueId"] = StudentUniqueId,
            ["firstName"] = FirstName,
        };
        using var studentContent = new StringContent(
            studentPayload.ToJsonString(),
            Encoding.UTF8,
            "application/json"
        );
        using HttpResponseMessage studentCreate = await harness.HttpClient.PostAsync(
            StudentsEndpoint,
            studentContent
        );
        studentCreate
            .StatusCode.Should()
            .Be(HttpStatusCode.Created, await studentCreate.Content.ReadAsStringAsync());
        string studentLocationPath = studentCreate.Headers.Location!.IsAbsoluteUri
            ? studentCreate.Headers.Location!.AbsolutePath
            : studentCreate.Headers.Location!.OriginalString;

        var itemPayload = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = 2,
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = StudentUniqueId },
        };
        using var itemContent = new StringContent(
            itemPayload.ToJsonString(),
            Encoding.UTF8,
            "application/json"
        );
        using HttpResponseMessage itemCreate = await harness.HttpClient.PostAsync(
            MergeItemsEndpoint,
            itemContent
        );
        itemCreate
            .StatusCode.Should()
            .Be(HttpStatusCode.Created, await itemCreate.Content.ReadAsStringAsync());

        using HttpResponseMessage deleteResponse = await harness.HttpClient.DeleteAsync(studentLocationPath);
        string deleteBody = await deleteResponse.Content.ReadAsStringAsync();
        deleteResponse
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Conflict,
                "deleting a Student referenced by a profileRootOnlyMergeItem must be rejected"
            );
        deleteBody
            .Should()
            .Contain(
                "ProfileRootOnlyMergeItem",
                "the conflict body should identify the dependent resource by its PascalCase resource name"
            );

        int persistedStudentCount = await CountStudentRowsAsync(harness, StudentUniqueId);
        persistedStudentCount.Should().Be(1, "the referenced Student must remain after the rejected DELETE");
    }

    private static async Task<int> CountStudentRowsAsync(
        ApiIntegrationHarness harness,
        string studentUniqueId
    )
    {
        // Double-quoted identifiers work on PostgreSQL natively and on SQL Server
        // with the default QUOTED_IDENTIFIER ON setting (Microsoft.Data.SqlClient
        // sets this by default), so a single statement covers both dialects.
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM "edfi"."Student" WHERE "StudentUniqueId" = @studentUniqueId
            """;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@studentUniqueId";
        parameter.Value = studentUniqueId;
        command.Parameters.Add(parameter);

        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
