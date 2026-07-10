// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Profiled HTTP coverage bound to the <c>ProfileRootOnlyMerge</c> fixture and its
/// <c>ProfileRootOnlyMergeItem-Visible</c> / <c>ProfileRootOnlyMergeItem-ReadOnly</c>
/// profile XML files. Drives the full DMS HTTP pipeline through real profile content
/// types so the read/write profile machinery, hidden-field preservation on PUT, and
/// profile-aware HTTP error semantics are exercised end-to-end against both
/// PostgreSQL and SQL Server backends.
/// </summary>
internal static class ProfileRootOnlyMergeProfileScenario
{
    private const string MergeItemsEndpoint = "/data/ed-fi/profileRootOnlyMergeItems";
    private const string StandardJsonContentType = "application/json";

    private const string VisibleWritableContentType =
        "application/vnd.ed-fi.profilerootonlymergeitem.profilerootonlymergeitem-visible.writable+json";
    private const string VisibleReadableContentType =
        "application/vnd.ed-fi.profilerootonlymergeitem.profilerootonlymergeitem-visible.readable+json";
    private const string ReadOnlyWritableContentType =
        "application/vnd.ed-fi.profilerootonlymergeitem.profilerootonlymergeitem-readonly.writable+json";

    public static async Task It_creates_and_reads_via_visible_profile(ApiIntegrationHarness harness)
    {
        const int itemId = 4001;
        const string displayName = "visible-create";
        const string clearableText = "visible-clearable";

        var payload = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = itemId,
            ["displayName"] = displayName,
            ["profileScope"] = new JsonObject { ["clearableText"] = clearableText },
        };

        using HttpResponseMessage createResponse = await PostProfiledAsync(
            harness,
            payload,
            VisibleWritableContentType
        );
        string createBody = await createResponse.Content.ReadAsStringAsync();

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, createBody);
        createResponse.Headers.Location.Should().NotBeNull();
        createResponse
            .TryReadRawEtag(out _)
            .Should()
            .BeTrue("a profiled POST must emit an ETag header alongside Location");

        string locationPath = createResponse.Headers.Location!.IsAbsoluteUri
            ? createResponse.Headers.Location!.AbsolutePath
            : createResponse.Headers.Location!.OriginalString;

        using HttpResponseMessage getResponse = await GetProfiledAsync(
            harness,
            locationPath,
            VisibleReadableContentType
        );
        string getBody = await getResponse.Content.ReadAsStringAsync();

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, getBody);

        JsonObject returned = JsonNode.Parse(getBody)!.AsObject();
        returned["profileRootOnlyMergeItemId"]!.GetValue<int>().Should().Be(itemId);
        returned["displayName"]!.GetValue<string>().Should().Be(displayName);

        JsonNode? returnedProfileScope = returned["profileScope"];
        returnedProfileScope.Should().NotBeNull("clearableText is visible to this profile");
        JsonObject profileScopeObject = returnedProfileScope!.AsObject();
        profileScopeObject["clearableText"]!.GetValue<string>().Should().Be(clearableText);
        profileScopeObject
            .ContainsKey("preservedText")
            .Should()
            .BeFalse("preservedText is hidden by ProfileRootOnlyMergeItem-Visible and must not be returned");

        (string? persistedDisplayName, string? persistedClearable, string? persistedPreserved) =
            await ReadMergeItemColumnsAsync(harness, itemId);
        persistedDisplayName.Should().Be(displayName, "the create must persist visible scalar columns");
        persistedClearable.Should().Be(clearableText, "the create must persist the visible scoped column");
        persistedPreserved
            .Should()
            .BeNull("the create never named preservedText so the hidden column stays empty");
    }

    public static async Task It_preserves_hidden_field_on_profiled_put(ApiIntegrationHarness harness)
    {
        const int itemId = 4002;
        const string seededDisplayName = "seeded-display";
        const string seededClearable = "seeded-clearable";
        const string seededPreserved = "seeded-preserved";
        const string putDisplayName = "profiled-display";
        const string putClearable = "profiled-clearable";

        var seedPayload = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = itemId,
            ["displayName"] = seededDisplayName,
            ["profileScope"] = new JsonObject
            {
                ["clearableText"] = seededClearable,
                ["preservedText"] = seededPreserved,
            },
        };

        using var seedContent = new StringContent(
            seedPayload.ToJsonString(),
            Encoding.UTF8,
            StandardJsonContentType
        );
        using HttpResponseMessage seedResponse = await harness.HttpClient.PostAsync(
            MergeItemsEndpoint,
            seedContent
        );
        seedResponse
            .StatusCode.Should()
            .Be(HttpStatusCode.Created, await seedResponse.Content.ReadAsStringAsync());
        string locationPath = seedResponse.Headers.Location!.IsAbsoluteUri
            ? seedResponse.Headers.Location!.AbsolutePath
            : seedResponse.Headers.Location!.OriginalString;
        string resourceId = locationPath.Split('/')[^1];

        // As of the 2026-07-04 ADR amendment, profileCode is no longer state-significant for If-Match
        // (EtagMatchProjection compares only ContentVersion + schemaEpoch), so using an etag from a
        // profiled GET here is incidental rather than required - the unprofiled seed POST's etag would
        // also match. Use the profiled GET's ETag header to prove it carries the same served tag as the
        // body and can guard the subsequent write.
        using HttpResponseMessage profiledGetResponse = await GetProfiledAsync(
            harness,
            locationPath,
            VisibleReadableContentType
        );
        string profiledGetBody = await profiledGetResponse.Content.ReadAsStringAsync();
        profiledGetResponse.StatusCode.Should().Be(HttpStatusCode.OK, profiledGetBody);
        string bodyEtag = JsonNode.Parse(profiledGetBody)!.AsObject()["_etag"]!.GetValue<string>();
        profiledGetResponse
            .TryReadRawEtag(out string profiledEtag)
            .Should()
            .BeTrue("a successful profiled GET must emit its served ETag in the response header");
        profiledEtag.Should().Be(bodyEtag, "the GET header and body must expose the same served ETag");

        var putPayload = new JsonObject
        {
            ["id"] = resourceId,
            ["profileRootOnlyMergeItemId"] = itemId,
            ["displayName"] = putDisplayName,
            ["profileScope"] = new JsonObject { ["clearableText"] = putClearable },
        };
        using var putContent = new StringContent(
            putPayload.ToJsonString(),
            Encoding.UTF8,
            VisibleWritableContentType
        );
        using var putRequest = new HttpRequestMessage(HttpMethod.Put, locationPath) { Content = putContent };
        putRequest.Headers.TryAddWithoutValidation("If-Match", profiledEtag);

        using HttpResponseMessage putResponse = await harness.HttpClient.SendAsync(putRequest);
        string putBody = await putResponse.Content.ReadAsStringAsync();
        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, putBody);

        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(locationPath);
        string getBody = await getResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, getBody);
        JsonObject returned = JsonNode.Parse(getBody)!.AsObject();
        returned["displayName"]!.GetValue<string>().Should().Be(putDisplayName);

        JsonObject returnedProfileScope = returned["profileScope"]!.AsObject();
        returnedProfileScope["clearableText"]!.GetValue<string>().Should().Be(putClearable);
        returnedProfileScope["preservedText"]!
            .GetValue<string>()
            .Should()
            .Be(
                seededPreserved,
                "an unprofiled GET must still return the hidden preservedText since the profiled PUT must not erase it"
            );

        (string? persistedDisplayName, string? persistedClearable, string? persistedPreserved) =
            await ReadMergeItemColumnsAsync(harness, itemId);
        persistedDisplayName.Should().Be(putDisplayName, "PUT must overwrite the visible scalar column");
        persistedClearable.Should().Be(putClearable, "PUT must overwrite the visible scoped column");
        persistedPreserved
            .Should()
            .Be(
                seededPreserved,
                "the hidden relational column must keep its seeded value through a profiled PUT that never named it"
            );
    }

    public static async Task It_rejects_write_against_read_only_profile(ApiIntegrationHarness harness)
    {
        var payload = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = 4003,
            ["displayName"] = "read-only-write-rejected",
        };

        using HttpResponseMessage response = await PostProfiledAsync(
            harness,
            payload,
            ReadOnlyWritableContentType
        );
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.MethodNotAllowed,
                "POST under a profile that exposes only a ReadContentType must return 405"
            );
        body.Should()
            .Contain(
                // The header parser captures the profile name from the Content-Type
                // header verbatim (lowercase), and the resolver echoes it back in
                // the error body, so the assertion matches the parsed casing rather
                // than the original "ProfileRootOnlyMergeItem-ReadOnly" XML name.
                "profilerootonlymergeitem-readonly",
                "the error body must name the profile that rejected the write"
            );
        body.Should()
            .Contain(
                "ProfileRootOnlyMergeItem",
                "the error body must name the resource that was not writable under the profile"
            );

        int persistedRowCount = await CountMergeItemRowsAsync(harness, 4003);
        persistedRowCount.Should().Be(0, "the rejected profiled POST must not create a row");
    }

    private static async Task<HttpResponseMessage> PostProfiledAsync(
        ApiIntegrationHarness harness,
        JsonObject payload,
        string profileContentType
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Post, MergeItemsEndpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, profileContentType),
        };
        return await harness.HttpClient.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetProfiledAsync(
        ApiIntegrationHarness harness,
        string locationPath,
        string profileContentType
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, locationPath);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(profileContentType));
        return await harness.HttpClient.SendAsync(request);
    }

    private static async Task<(
        string? DisplayName,
        string? ClearableText,
        string? PreservedText
    )> ReadMergeItemColumnsAsync(ApiIntegrationHarness harness, int profileRootOnlyMergeItemId)
    {
        // Double-quoted identifiers are accepted by both PostgreSQL (native) and SQL
        // Server (default QUOTED_IDENTIFIER ON), so a single statement covers both.
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT "DisplayName", "ProfileScopeClearableText", "ProfileScopePreservedText"
            FROM "edfi"."ProfileRootOnlyMergeItem"
            WHERE "ProfileRootOnlyMergeItemId" = @profileRootOnlyMergeItemId
            """;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@profileRootOnlyMergeItemId";
        parameter.Value = profileRootOnlyMergeItemId;
        command.Parameters.Add(parameter);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return (null, null, null);
        }

        string? displayName = await reader.IsDBNullAsync(0) ? null : reader.GetString(0);
        string? clearable = await reader.IsDBNullAsync(1) ? null : reader.GetString(1);
        string? preserved = await reader.IsDBNullAsync(2) ? null : reader.GetString(2);
        return (displayName, clearable, preserved);
    }

    private static async Task<int> CountMergeItemRowsAsync(
        ApiIntegrationHarness harness,
        int profileRootOnlyMergeItemId
    )
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM "edfi"."ProfileRootOnlyMergeItem"
            WHERE "ProfileRootOnlyMergeItemId" = @profileRootOnlyMergeItemId
            """;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@profileRootOnlyMergeItemId";
        parameter.Value = profileRootOnlyMergeItemId;
        command.Parameters.Add(parameter);

        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
