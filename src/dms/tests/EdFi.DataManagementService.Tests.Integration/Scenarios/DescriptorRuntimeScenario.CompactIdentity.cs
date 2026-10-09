// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static partial class DescriptorRuntimeScenario
{
    public static async Task It_applies_RI_matching_post_component_and_casing_updates(
        ApiIntegrationHarness harness
    )
    {
        DescriptorValues original = CreateDescriptorValues("compact-post", codeValue: "Campus#MiXeD");
        (string path, string etag) = await CreateDescriptorAsync(harness, original);
        StoredDescriptor before = await ReadStoredDescriptorAsync(harness, path);
        DescriptorValues components = original with
        {
            Namespace = $"{original.Namespace}#Campus",
            CodeValue = "MiXeD",
        };
        ($"{components.Namespace}#{components.CodeValue}")
            .Should()
            .Be($"{original.Namespace}#{original.CodeValue}");

        (StoredDescriptor afterComponents, string componentEtag) = await AssertPostUpdateAsync(
            harness,
            path,
            components,
            before,
            etag
        );
        DescriptorValues casing = components with { CodeValue = "MIXED" };
        await AssertPostUpdateAsync(harness, path, casing, afterComponents, componentEtag);
    }

    public static async Task It_accepts_equal_whole_URI_components_on_put(ApiIntegrationHarness harness)
    {
        DescriptorValues original = CreateDescriptorValues("compact-put", codeValue: "Campus#MiXeD");
        (string path, string etag) = await CreateDescriptorAsync(harness, original);
        StoredDescriptor before = await ReadStoredDescriptorAsync(harness, path);
        DescriptorValues components = original with
        {
            Namespace = $"{original.Namespace}#Campus",
            CodeValue = "MiXeD",
        };
        using HttpResponseMessage response = await PutDescriptorAsync(
            harness,
            path,
            path.Split('/')[^1],
            components,
            etag
        );
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        response.TryReadRawEtag(out string updatedEtag).Should().BeTrue();
        updatedEtag.Should().NotBe(etag);
        StoredDescriptor after = await ReadStoredDescriptorAsync(harness, path);
        AssertChangedState(after, before, components);
        await AssertResponseStateAsync(harness, path, components, updatedEtag, after);
    }

    public static async Task It_preserves_all_stamps_and_change_queries_on_identical_post_and_put(
        ApiIntegrationHarness harness
    )
    {
        DescriptorValues original = CreateDescriptorValues("compact-no-op");
        (string path, string etag) = await CreateDescriptorAsync(harness, original);
        StoredDescriptor before = await ReadStoredDescriptorAsync(harness, path);
        JsonArray initialChanges = await GetJsonArrayAsync(
            harness,
            $"{DescriptorEndpoint}?minChangeVersion={before.Version}&maxChangeVersion={before.Version}"
        );
        initialChanges.Select(node => node!["id"]!.GetValue<string>()).Should().Equal(path.Split('/')[^1]);

        using HttpResponseMessage post = await PostJsonAsync(
            harness,
            DescriptorEndpoint,
            CreateDescriptorPayload(original)
        );
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());
        ToPath(post.Headers.Location!).Should().Be(path);
        post.TryReadRawEtag(out string postEtag).Should().BeTrue();
        postEtag.Should().Be(etag);
        using HttpResponseMessage put = await PutDescriptorAsync(
            harness,
            path,
            path.Split('/')[^1],
            original,
            etag
        );
        put.StatusCode.Should().Be(HttpStatusCode.NoContent, await put.Content.ReadAsStringAsync());
        put.TryReadRawEtag(out string putEtag).Should().BeTrue();
        putEtag.Should().Be(etag);
        (await ReadStoredDescriptorAsync(harness, path)).Should().Be(before);
        await AssertResponseStateAsync(harness, path, original, etag, before);
        JsonNode
            .DeepEquals(
                initialChanges,
                await GetJsonArrayAsync(
                    harness,
                    $"{DescriptorEndpoint}?minChangeVersion={before.Version}&maxChangeVersion={before.Version}"
                )
            )
            .Should()
            .BeTrue();
        (await GetJsonArrayAsync(harness, $"{DescriptorEndpoint}?minChangeVersion={before.Version + 1}"))
            .Should()
            .BeEmpty();
    }

    public static async Task It_rejects_a_duplicate_whole_URI_without_an_RI_match(
        ApiIntegrationHarness harness
    )
    {
        DescriptorValues original = CreateDescriptorValues("compact-conflict", codeValue: "Campus#MiXeD");
        (string path, string etag) = await CreateDescriptorAsync(harness, original);
        long initialDocumentCount = await CountAllDocumentRowsAsync(harness);
        // Model a row with no matching RI. Database equality must never silently select it for an upsert.
        await ExecuteSqlAsync(
            harness,
            "DELETE FROM \"dms\".\"ReferentialIdentity\" WHERE \"DocumentId\" = @documentId",
            ("@documentId", (await ReadStoredDescriptorAsync(harness, path)).DocumentId)
        );
        StoredDescriptor before = await ReadStoredDescriptorAsync(harness, path);
        DescriptorValues components = original with
        {
            Namespace = $"{original.Namespace}#Campus",
            CodeValue = "MiXeD",
        };
        using HttpResponseMessage duplicate = await PostJsonAsync(
            harness,
            DescriptorEndpoint,
            CreateDescriptorPayload(components)
        );
        string duplicateBody = await duplicate.Content.ReadAsStringAsync();
        duplicate.StatusCode.Should().Be(HttpStatusCode.InternalServerError, duplicateBody);
        JsonObject problem = JsonNode.Parse(duplicateBody)!.AsObject();
        problem["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:system");
        problem["title"]!.GetValue<string>().Should().Be("System Error");
        problem["status"]!.GetValue<int>().Should().Be(500);
        problem["detail"]!.GetValue<string>().Should().Be("An unexpected problem has occurred.");
        (await CountAllDocumentRowsAsync(harness)).Should().Be(initialDocumentCount);
        (await ReadStoredDescriptorAsync(harness, path)).Should().Be(before);
        await AssertResponseStateAsync(harness, path, original, etag, before);
        (await CountDocumentRowsAsync(harness, path.Split('/')[^1])).Should().Be(1);
        (await GetJsonArrayAsync(harness, $"{DescriptorEndpoint}?namespace={Escape(original.Namespace)}"))
            .Count.Should()
            .Be(1);
    }

    public static async Task It_isolates_identical_URIs_across_descriptor_types_and_projects(
        ApiIntegrationHarness harness
    )
    {
        await PrepareCompactKeysAsync(harness);
        DescriptorValues original = CreateDescriptorValues("compact-types");
        string[] endpoints =
        [
            DescriptorEndpoint,
            "/data/ed-fi/academicSubjectDescriptors",
            "/data/sample/schoolTypeDescriptors",
        ];
        List<(string Path, string Etag, StoredDescriptor State)> rows = [];
        foreach (string endpoint in endpoints)
        {
            using HttpResponseMessage response = await PostJsonAsync(
                harness,
                endpoint,
                CreateDescriptorPayload(original)
            );
            response
                .StatusCode.Should()
                .Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            string path = ToPath(response.Headers.Location!);
            response.TryReadRawEtag(out string etag).Should().BeTrue();
            StoredDescriptor state = await ReadStoredDescriptorAsync(harness, path);
            AssertCompactKeys(state);
            await AssertResponseStateAsync(harness, path, original, etag, state);
            rows.Add((path, etag, state));
        }
        rows.Select(row => row.State.DescriptorId).Should().OnlyHaveUniqueItems();
        rows.Select(row => row.State.DocumentId).Should().OnlyHaveUniqueItems();
        rows.Select(row => row.State.ResourceKeyId).Should().OnlyHaveUniqueItems();
        DescriptorValues changed = original with { Description = "Only the Sample descriptor changes" };
        await AssertPostUpdateAsync(
            harness,
            rows[2].Path,
            changed,
            rows[2].State,
            rows[2].Etag,
            endpoints[2]
        );
        for (int i = 0; i < 2; i++)
        {
            (await ReadStoredDescriptorAsync(harness, rows[i].Path)).Should().Be(rows[i].State);
            await AssertResponseStateAsync(harness, rows[i].Path, original, rows[i].Etag, rows[i].State);
        }
        using HttpResponseMessage wrongType = await harness.HttpClient.GetAsync(
            $"{endpoints[1]}/{rows[0].Path.Split('/')[^1]}"
        );
        wrongType.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using HttpResponseMessage wrongProjectDelete = await harness.HttpClient.DeleteAsync(
            $"{endpoints[2]}/{rows[0].Path.Split('/')[^1]}"
        );
        wrongProjectDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using HttpResponseMessage deleted = await harness.HttpClient.DeleteAsync(rows[2].Path);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await CountDocumentRowsAsync(harness, rows[2].Path.Split('/')[^1])).Should().Be(0);
        foreach (var row in rows.Take(2))
        {
            (await ReadStoredDescriptorAsync(harness, row.Path)).Should().Be(row.State);
        }
    }

    public static async Task It_enforces_namespace_authorization_without_stamp_side_effects(
        ApiIntegrationHarness harness,
        MutableNamespacePrefixJwtValidationService identity
    )
    {
        DescriptorValues original = CreateDescriptorValues("compact-namespace");
        (string path, string etag) = await CreateDescriptorAsync(harness, original);
        StoredDescriptor before = await ReadStoredDescriptorAsync(harness, path);
        identity.SetNamespacePrefixes(["uri://denied.example.org/"]);
        using HttpResponseMessage read = await harness.HttpClient.GetAsync(path);
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using HttpResponseMessage post = await PostJsonAsync(
            harness,
            DescriptorEndpoint,
            CreateDescriptorPayload(original with { Description = "Denied update" })
        );
        post.StatusCode.Should().Be(HttpStatusCode.Forbidden, await post.Content.ReadAsStringAsync());
        using HttpResponseMessage put = await PutDescriptorAsync(
            harness,
            path,
            path.Split('/')[^1],
            original,
            etag
        );
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using HttpResponseMessage delete = await harness.HttpClient.DeleteAsync(path);
        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using HttpResponseMessage create = await PostJsonAsync(
            harness,
            DescriptorEndpoint,
            CreateDescriptorPayload(original with { CodeValue = "DeniedCreate" })
        );
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadStoredDescriptorAsync(harness, path)).Should().Be(before);
        identity.SetNamespacePrefixes(["uri://ed-fi.org/"]);
        await AssertResponseStateAsync(harness, path, original, etag, before);
        (await GetJsonArrayAsync(harness, $"{DescriptorEndpoint}?namespace={Escape(original.Namespace)}"))
            .Count.Should()
            .Be(1);
        // The stored namespace passes this narrower prefix; moving a delimiter into CodeValue
        // leaves the whole URI equal but makes the proposed namespace unauthorized.
        DescriptorValues scoped = CreateDescriptorValues("compact-proposed-namespace") with
        {
            CodeValue = "MiXeD",
        };
        scoped = scoped with { Namespace = $"{scoped.Namespace}#Campus" };
        (string scopedPath, string scopedEtag) = await CreateDescriptorAsync(harness, scoped);
        StoredDescriptor scopedBefore = await ReadStoredDescriptorAsync(harness, scopedPath);
        identity.SetNamespacePrefixes([scoped.Namespace]);
        DescriptorValues outsidePrefix = scoped with
        {
            Namespace = scoped.Namespace[..scoped.Namespace.LastIndexOf('#')],
            CodeValue = "Campus#MiXeD",
        };
        using HttpResponseMessage deniedProposedPost = await PostJsonAsync(
            harness,
            DescriptorEndpoint,
            CreateDescriptorPayload(outsidePrefix)
        );
        deniedProposedPost
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden, await deniedProposedPost.Content.ReadAsStringAsync());
        using HttpResponseMessage deniedProposedPut = await PutDescriptorAsync(
            harness,
            scopedPath,
            scopedPath.Split('/')[^1],
            outsidePrefix,
            scopedEtag
        );
        deniedProposedPut.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadStoredDescriptorAsync(harness, scopedPath)).Should().Be(scopedBefore);
        await AssertResponseStateAsync(harness, scopedPath, scoped, scopedEtag, scopedBefore);
    }

    private static async Task<(StoredDescriptor State, string Etag)> AssertPostUpdateAsync(
        ApiIntegrationHarness harness,
        string path,
        DescriptorValues values,
        StoredDescriptor before,
        string etag,
        string endpoint = DescriptorEndpoint
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            endpoint,
            CreateDescriptorPayload(values)
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        ToPath(response.Headers.Location!).Should().Be(path);
        response.TryReadRawEtag(out string afterEtag).Should().BeTrue();
        afterEtag.Should().NotBe(etag);
        StoredDescriptor after = await ReadStoredDescriptorAsync(harness, path);
        AssertChangedState(after, before, values);
        await AssertResponseStateAsync(harness, path, values, afterEtag, after);
        return (after, afterEtag);
    }

    private static void AssertChangedState(
        StoredDescriptor after,
        StoredDescriptor before,
        DescriptorValues values
    )
    {
        after.DescriptorId.Should().Be(before.DescriptorId);
        after.DocumentId.Should().Be(before.DocumentId);
        after.ResourceKeyId.Should().Be(before.ResourceKeyId);
        after.Version.Should().Be(before.Version + 1);
        after.ModifiedAt.Should().BeAfter(before.ModifiedAt);
        after.Namespace.Should().Be(values.Namespace);
        after.CodeValue.Should().Be(values.CodeValue);
        after.RiCount.Should().Be(before.RiCount);
        after.HistoryCount.Should().Be(before.HistoryCount);
    }

    private static void AssertCompactKeys(StoredDescriptor state)
    {
        state.DocumentId.Should().BeGreaterThan(int.MaxValue);
        ((long)state.DescriptorId).Should().NotBe(state.DocumentId);
    }

    private static async Task AssertResponseStateAsync(
        ApiIntegrationHarness harness,
        string path,
        DescriptorValues values,
        string etag,
        StoredDescriptor state
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.TryReadRawEtag(out string readEtag).Should().BeTrue();
        readEtag.Should().Be(etag);
        JsonObject body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        AssertDescriptorFields(body, values);
        body["id"]!.GetValue<string>().Should().Be(path.Split('/')[^1]);
        body["_etag"]!.GetValue<string>().Should().Be(etag);
        etag.Should().StartWith($"{state.Version}-");
        body["_lastModifiedDate"]!
            .GetValue<string>()
            .Should()
            .Be(
                state.ModifiedAt.UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture
                )
            );
    }

    private static async Task<long> CountAllDocumentRowsAsync(ApiIntegrationHarness harness)
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"dms\".\"Document\"";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static Task PrepareCompactKeysAsync(ApiIntegrationHarness harness) =>
        ExecuteSqlAsync(
            harness,
            harness.DbConnection.GetType().Name == "NpgsqlConnection"
                ? CompactDescriptorSeedSupport.PostgresqlSeparateDocumentIdsSql
                : CompactDescriptorSeedSupport.MssqlSeparateDocumentIdsSql
        );

    private static async Task ExecuteSqlAsync(
        ApiIntegrationHarness harness,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<StoredDescriptor> ReadStoredDescriptorAsync(
        ApiIntegrationHarness harness,
        string path
    )
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT descriptor."DescriptorId", document."DocumentId", descriptor."ResourceKeyId",
                   descriptor."Namespace", descriptor."CodeValue",
                   document."ContentVersion", document."ContentLastModifiedAt",
                   descriptor."ContentVersion", descriptor."ContentLastModifiedAt",
                   (SELECT COUNT(*) FROM "dms"."ReferentialIdentity" ri WHERE ri."DocumentId" = document."DocumentId"),
                   (SELECT COUNT(*) FROM "tracked_changes_edfi"."Descriptor" history WHERE history."DocumentId" = document."DocumentId")
            FROM "dms"."Document" document
            JOIN "dms"."Descriptor" descriptor ON descriptor."DocumentId" = document."DocumentId"
            WHERE document."DocumentUuid" = @uuid
            """;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@uuid";
        parameter.Value = Guid.Parse(path.Split('/')[^1]);
        command.Parameters.Add(parameter);
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        long version = reader.GetInt64(5);
        DateTimeOffset at = ToDateTimeOffset(reader.GetValue(6));
        reader.GetInt64(7).Should().Be(version, "descriptor mirrors the owning document stamp");
        ToDateTimeOffset(reader.GetValue(8)).Should().Be(at);
        return new(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetInt16(2),
            reader.GetString(3),
            reader.GetString(4),
            version,
            at,
            Convert.ToInt32(reader.GetValue(9)),
            Convert.ToInt32(reader.GetValue(10))
        );
    }

    private sealed record StoredDescriptor(
        int DescriptorId,
        long DocumentId,
        short ResourceKeyId,
        string Namespace,
        string CodeValue,
        long Version,
        DateTimeOffset ModifiedAt,
        int RiCount,
        int HistoryCount
    );
}
