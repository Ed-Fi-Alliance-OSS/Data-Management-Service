// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Tests.Integration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

/// <summary>
/// The loader proof gate, run against a live 10,000-row load on either provider. It proves the
/// direct-SQL fixture is indistinguishable from production writes: one control student is
/// POSTed through the real HTTP pipeline after the load, and the loader rows must match its
/// row shapes across dms.Document, edfi.Student, dms.ReferentialIdentity, and the
/// tracked-change side effects — including producing exactly as many insert-time
/// tracked-change rows per document as the POST does (expected: none). Real GET-many and
/// GET-by-id requests must then hydrate loader rows with correct bodies.
///
/// All assertion SQL uses double-quoted identifiers, which both providers accept.
/// </summary>
internal static class PerfFixtureSmokeScenario
{
    private const string TrackedChangesCountSql = """
        SELECT COUNT(*) FROM "tracked_changes_edfi"."Student";
        """;

    private const string DocumentRowSql = """
        SELECT "DocumentId", "ResourceKeyId", "CreatedByOwnershipTokenId", "ContentVersion", "IdentityVersion"
        FROM "dms"."Document"
        WHERE "DocumentUuid" = @uuid;
        """;

    private const string StudentRowSql = """
        SELECT "StudentUniqueId", "FirstName", "LastSurname", "BirthDate", "BirthCity", "ContentVersion"
        FROM "edfi"."Student"
        WHERE "DocumentId" = @documentId;
        """;

    private const string ReferentialIdentityCountSql = """
        SELECT COUNT(*) FROM "dms"."ReferentialIdentity" WHERE "DocumentId" = @documentId;
        """;

    private sealed record DocumentRow(
        long DocumentId,
        long ResourceKeyId,
        bool HasOwnershipToken,
        long ContentVersion,
        long IdentityVersion
    );

    private sealed record StudentRow(
        string StudentUniqueId,
        string FirstName,
        string LastSurname,
        object BirthDate,
        bool BirthCityIsNull,
        long ContentVersion
    );

    public static async Task RunAsync(ApiIntegrationHarness harness, PerfProvider provider)
    {
        PerfFixtureDefinition definition = new(PerfFixtureKind.Smoke10k);
        DbConnection connection = harness.DbConnection;

        long trackedBeforeLoad = await CountAsync(connection, TrackedChangesCountSql);
        trackedBeforeLoad.Should().Be(0, "the leased database must start clean");

        await PerfFixtureLoader.LoadAndVerifyAsync(connection, provider, definition);

        long trackedAfterLoad = await CountAsync(connection, TrackedChangesCountSql);

        Guid controlUuid = await PostControlStudentAsync(harness);

        long trackedAfterControl = await CountAsync(connection, TrackedChangesCountSql);
        long trackedRowsPerInsert = trackedAfterControl - trackedAfterLoad;
        trackedAfterLoad
            .Should()
            .Be(
                definition.RowCount * trackedRowsPerInsert,
                "loader inserts must produce the same tracked-change side effects per document as a production POST"
            );

        DocumentRow control = await ReadDocumentRowAsync(connection, controlUuid);
        control
            .DocumentId.Should()
            .Be(definition.MaxDocumentId + 1, "the reseed must hand the next id to the write path");

        DocumentRow loaderFirst = await ReadDocumentRowAsync(
            connection,
            PerfFixtureDefinition.DocumentUuidFor(1)
        );
        loaderFirst.DocumentId.Should().Be(PerfFixtureDefinition.MinDocumentId);
        loaderFirst.ResourceKeyId.Should().Be(control.ResourceKeyId);
        loaderFirst.HasOwnershipToken.Should().Be(control.HasOwnershipToken);
        loaderFirst.ContentVersion.Should().BePositive();
        loaderFirst.IdentityVersion.Should().BePositive();
        control.ContentVersion.Should().BePositive();

        StudentRow loaderStudent = await ReadStudentRowAsync(connection, loaderFirst.DocumentId);
        StudentRow controlStudent = await ReadStudentRowAsync(connection, control.DocumentId);
        loaderStudent.StudentUniqueId.Should().Be(PerfFixtureDefinition.StudentUniqueIdFor(1));
        loaderStudent.FirstName.Should().Be(controlStudent.FirstName);
        loaderStudent.LastSurname.Should().Be(controlStudent.LastSurname);
        loaderStudent.BirthDate.Should().Be(controlStudent.BirthDate);
        loaderStudent.BirthCityIsNull.Should().BeTrue();
        controlStudent.BirthCityIsNull.Should().BeTrue();
        loaderStudent
            .ContentVersion.Should()
            .Be(loaderFirst.ContentVersion, "the stamp trigger must have mirrored the document version");
        controlStudent.ContentVersion.Should().Be(control.ContentVersion);

        long loaderReferentialIdentities = await CountAsync(
            connection,
            ReferentialIdentityCountSql,
            loaderFirst.DocumentId
        );
        long controlReferentialIdentities = await CountAsync(
            connection,
            ReferentialIdentityCountSql,
            control.DocumentId
        );
        controlReferentialIdentities.Should().BeGreaterThan(0);
        loaderReferentialIdentities.Should().Be(controlReferentialIdentities);

        await AssertFirstPageAsync(harness);
        await AssertDeepPageAsync(harness, definition);
        await AssertGetByIdAsync(harness);
    }

    private static async Task<Guid> PostControlStudentAsync(ApiIntegrationHarness harness)
    {
        JsonObject payload = new()
        {
            ["studentUniqueId"] = "control-000000001",
            ["firstName"] = PerfFixtureDefinition.FirstName,
            ["lastSurname"] = PerfFixtureDefinition.LastSurname,
            ["birthDate"] = PerfFixtureDefinition.BirthDateIso,
        };
        using StringContent content = new(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage created = await harness.HttpClient.PostAsync(
            PerfFixtureDefinition.ResourceEndpoint,
            content
        );
        string body = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.Created, body);
        string location = created.Headers.Location!.ToString();
        return Guid.Parse(location.Split('/')[^1]);
    }

    private static async Task AssertFirstPageAsync(ApiIntegrationHarness harness)
    {
        JsonArray items = await GetPageAsync(harness, "?limit=25&offset=0");
        items.Should().HaveCount(25);
        Guid firstId = Guid.Parse(items[0]!["id"]!.GetValue<string>());
        firstId.Should().Be(PerfFixtureDefinition.DocumentUuidFor(1));
        items[0]!["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be(PerfFixtureDefinition.StudentUniqueIdFor(1));
        items[0]!["firstName"]!.GetValue<string>().Should().Be(PerfFixtureDefinition.FirstName);
    }

    private static async Task AssertDeepPageAsync(
        ApiIntegrationHarness harness,
        PerfFixtureDefinition definition
    )
    {
        JsonArray items = await GetPageAsync(harness, $"?limit=25&offset={definition.RowCount - 25}");
        items.Should().HaveCount(25);
        Guid lastId = Guid.Parse(items[^1]!["id"]!.GetValue<string>());
        lastId.Should().Be(PerfFixtureDefinition.DocumentUuidFor(definition.RowCount));
    }

    private static async Task AssertGetByIdAsync(ApiIntegrationHarness harness)
    {
        Guid documentUuid = PerfFixtureDefinition.DocumentUuidFor(9_999);
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            $"{PerfFixtureDefinition.ResourceEndpoint}/{documentUuid}"
        );
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonNode document = JsonNode.Parse(body)!;
        document["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be(PerfFixtureDefinition.StudentUniqueIdFor(9_999));
    }

    private static async Task<JsonArray> GetPageAsync(ApiIntegrationHarness harness, string queryString)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            PerfFixtureDefinition.ResourceEndpoint + queryString
        );
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsArray();
    }

    private static async Task<long> CountAsync(DbConnection connection, string sql, long? documentId = null)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        if (documentId is not null)
        {
            AddParameter(command, "documentId", documentId.Value);
        }

        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<DocumentRow> ReadDocumentRowAsync(DbConnection connection, Guid documentUuid)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = DocumentRowSql;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "uuid";
        parameter.Value = documentUuid;
        command.Parameters.Add(parameter);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"document {documentUuid} must exist");
        return new DocumentRow(
            reader.GetInt64(0),
            Convert.ToInt64(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture),
            !await reader.IsDBNullAsync(2),
            reader.GetInt64(3),
            reader.GetInt64(4)
        );
    }

    private static async Task<StudentRow> ReadStudentRowAsync(DbConnection connection, long documentId)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = StudentRowSql;
        AddParameter(command, "documentId", documentId);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"student {documentId} must exist");
        return new StudentRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetValue(3),
            await reader.IsDBNullAsync(4),
            reader.GetInt64(5)
        );
    }

    private static void AddParameter(DbCommand command, string name, long value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
