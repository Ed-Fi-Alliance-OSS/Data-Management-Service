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
        createResponse.Headers.ETag.Should().NotBeNull("a POST create must return an ETag header");

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
        string initialEtag = createResponse.Headers.ETag!.Tag;
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
        putResponse.Headers.ETag.Should().NotBeNull("PUT must return the new ETag");
        putResponse.Headers.ETag!.Tag.Should().NotBe(initialEtag, "PUT must advance the ETag");

        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(locationPath);
        string getBody = await getResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, getBody);
        JsonObject returned = JsonNode.Parse(getBody)!.AsObject();
        returned["firstName"]!.GetValue<string>().Should().Be(putFirstName);
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
        // Seed three students. ValidateQueryMiddleware rejects orderBy when the
        // resource has an empty queryFieldMapping (this fixture does), so the
        // scenario asserts only the limit/offset window sizes rather than a
        // specific element order.
        string[] ids = ["smoke-page-001", "smoke-page-002", "smoke-page-003"];
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
        }

        using HttpResponseMessage firstPage = await harness.HttpClient.GetAsync(
            $"{StudentsEndpoint}?offset=0&limit=2"
        );
        string firstPageBody = await firstPage.Content.ReadAsStringAsync();
        firstPage.StatusCode.Should().Be(HttpStatusCode.OK, firstPageBody);
        JsonNode
            .Parse(firstPageBody)!
            .AsArray()
            .Count.Should()
            .Be(2, "limit=2 returns the first two seeded students");

        using HttpResponseMessage secondPage = await harness.HttpClient.GetAsync(
            $"{StudentsEndpoint}?offset=2&limit=2"
        );
        string secondPageBody = await secondPage.Content.ReadAsStringAsync();
        secondPage.StatusCode.Should().Be(HttpStatusCode.OK, secondPageBody);
        JsonNode
            .Parse(secondPageBody)!
            .AsArray()
            .Count.Should()
            .Be(1, "offset=2 leaves only one of three seeded students");
    }

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

        response.StatusCode.Should().BeOneOf([HttpStatusCode.Conflict, HttpStatusCode.BadRequest], body);
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
