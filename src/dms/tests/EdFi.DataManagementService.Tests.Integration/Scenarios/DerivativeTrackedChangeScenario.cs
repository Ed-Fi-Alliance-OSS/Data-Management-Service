// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// The tracked-change surfaces, routed. Each database is given a different number of tracked deletes
/// and key changes, so the response body - not merely its status - names the database that served it.
/// </summary>
internal static class DerivativeTrackedChangeScenario
{
    private const string DeletesEndpoint = "/data/ed-fi/students/deletes";
    private const string KeyChangesEndpoint = "/data/ed-fi/students/keyChanges";
    private const string AvailableChangeVersionsEndpoint = "/changeQueries/v1/availableChangeVersions";

    /// <summary>The parent is given one tracked delete; the snapshot is given three.</summary>
    private const int PrimaryDeleteCount = 1;
    private const int SnapshotDeleteCount = 3;

    /// <summary>And a different number of tracked key changes, for the same reason.</summary>
    private const int PrimaryKeyChangeCount = 1;
    private const int SnapshotKeyChangeCount = 2;

    /// <summary>Prefixes the two databases' rows, so a body names the database that produced it.</summary>
    private const string PrimaryPrefix = "prim";
    private const string SnapshotPrefix = "snap";

    public static async Task SeedAsync(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        long dataStoreId,
        RelationalProviderToken providerToken,
        string primaryConnectionString,
        string snapshotConnectionString
    )
    {
        provider.Publish([
            DerivativeRoutingSupport.ParentOnly(dataStoreId, snapshotConnectionString, providerToken),
        ]);
        await SeedTrackedDeletesAsync(harness, SnapshotDeleteCount, SnapshotPrefix);
        await SeedTrackedKeyChangesAsync(harness, SnapshotKeyChangeCount, SnapshotPrefix);

        provider.Publish([
            DerivativeRoutingSupport.ParentOnly(dataStoreId, primaryConnectionString, providerToken),
        ]);
        await SeedTrackedDeletesAsync(harness, PrimaryDeleteCount, PrimaryPrefix);
        await SeedTrackedKeyChangesAsync(harness, PrimaryKeyChangeCount, PrimaryPrefix);

        provider.Publish([
            DerivativeRoutingSupport.ParentWith(
                dataStoreId,
                primaryConnectionString,
                providerToken,
                new Dictionary<DataStoreDerivativeType, string>
                {
                    [DataStoreDerivativeType.Snapshot] = snapshotConnectionString,
                }
            ),
        ]);
    }

    /// <summary>
    /// Creates and then deletes documents, which is what puts rows on the tracked-delete surface. Each
    /// database gets a different count, so the two are distinguishable from the response alone.
    /// </summary>
    private static async Task SeedTrackedDeletesAsync(ApiIntegrationHarness harness, int count, string prefix)
    {
        for (int index = 0; index < count; index++)
        {
            string studentUniqueId = $"{prefix}-tracked-{index}";

            using HttpContent content = Ds52StudentContent(studentUniqueId);
            using HttpResponseMessage created = await harness.HttpClient.PostAsync(
                DerivativeRoutingSupport.StudentsEndpoint,
                content
            );

            created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

            string location = created.Headers.Location!.IsAbsoluteUri
                ? created.Headers.Location!.AbsolutePath
                : created.Headers.Location!.OriginalString;

            using HttpResponseMessage deleted = await harness.HttpClient.DeleteAsync(location);
            deleted
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        }
    }

    /// <summary>
    /// Creates Students and then updates their identity, which is what puts rows on the tracked
    /// key-change surface. The new unique id carries the database's prefix and the word "renamed", so
    /// the response body names both the database and the fact that this was a key change.
    /// </summary>
    private static async Task SeedTrackedKeyChangesAsync(
        ApiIntegrationHarness harness,
        int count,
        string prefix
    )
    {
        for (int index = 0; index < count; index++)
        {
            string originalUniqueId = $"{prefix}-key-{index}";
            string renamedUniqueId = $"{prefix}-renamed-{index}";

            using HttpContent createContent = Ds52StudentContent(originalUniqueId);
            using HttpResponseMessage created = await harness.HttpClient.PostAsync(
                DerivativeRoutingSupport.StudentsEndpoint,
                createContent
            );

            created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

            string location = created.Headers.Location!.IsAbsoluteUri
                ? created.Headers.Location!.AbsolutePath
                : created.Headers.Location!.OriginalString;

            string documentId = location[(location.LastIndexOf('/') + 1)..];

            using HttpContent updateContent = Ds52StudentContent(renamedUniqueId, documentId);
            using HttpResponseMessage updated = await harness.HttpClient.PutAsync(location, updateContent);

            updated
                .StatusCode.Should()
                .Be(HttpStatusCode.NoContent, await updated.Content.ReadAsStringAsync());
        }
    }

    /// <summary>
    /// The Student shape the DS 5.2 schema requires, which is richer than the focused fixtures'.
    /// </summary>
    private static HttpContent Ds52StudentContent(string studentUniqueId, string? documentId = null)
    {
        JsonObject payload = new()
        {
            ["studentUniqueId"] = studentUniqueId,
            ["firstName"] = "Ada",
            ["lastSurname"] = "Lovelace",
            ["birthDate"] = "2010-01-01",
        };

        if (documentId is not null)
        {
            // A PUT carries the document id it is replacing.
            payload["id"] = documentId;
        }

        return new System.Net.Http.StringContent(
            payload.ToJsonString(),
            System.Text.Encoding.UTF8,
            "application/json"
        );
    }

    public static Task It_serves_deletes_from_the_selected_target(
        ApiIntegrationHarness harness,
        IDerivativeTargetReachability reachability,
        string primaryConnectionString
    ) =>
        AssertCountFromSelectedTargetAsync(
            harness,
            reachability,
            primaryConnectionString,
            DeletesEndpoint,
            SnapshotDeleteCount
        );

    /// <summary>
    /// The key-change surface reports the selected target's own identity updates. The two databases
    /// received different numbers of them under different prefixes, so the body names which one
    /// answered rather than merely proving something answered.
    /// </summary>
    public static async Task It_serves_key_changes_from_the_selected_target(
        ApiIntegrationHarness harness,
        IDerivativeTargetReachability reachability,
        string primaryConnectionString
    )
    {
        await reachability.MakeUnreachableAsync(primaryConnectionString);

        try
        {
            using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Get,
                KeyChangesEndpoint,
                useSnapshotHeaderValue: "true"
            );

            string body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);

            JsonArray keyChanges = JsonNode.Parse(body)!.AsArray();

            keyChanges
                .Count.Should()
                .Be(
                    SnapshotKeyChangeCount,
                    $"the snapshot received {SnapshotKeyChangeCount} identity updates and the parent "
                        + $"{PrimaryKeyChangeCount}: {body}"
                );

            body.Should().Contain(SnapshotPrefix, "every key change the snapshot holds carries its prefix");
            body.Should()
                .NotContain(
                    PrimaryPrefix,
                    "the parent's key changes must not appear in the snapshot's answer"
                );
        }
        finally
        {
            await reachability.MakeReachableAsync(primaryConnectionString);
        }
    }

    /// <summary>
    /// The change-version surface reports the selected target's own change versions, which differ
    /// because the two databases received different numbers of writes.
    /// </summary>
    public static async Task It_serves_available_change_versions_from_the_selected_target(
        ApiIntegrationHarness harness,
        IDerivativeTargetReachability reachability,
        string primaryConnectionString
    )
    {
        long primaryNewest = await ReadNewestChangeVersionAsync(harness, useSnapshot: false);

        await reachability.MakeUnreachableAsync(primaryConnectionString);

        long snapshotNewest;
        try
        {
            snapshotNewest = await ReadNewestChangeVersionAsync(harness, useSnapshot: true);
        }
        finally
        {
            await reachability.MakeReachableAsync(primaryConnectionString);
        }

        snapshotNewest
            .Should()
            .NotBe(
                primaryNewest,
                "the two databases received different numbers of writes, so their newest change "
                    + "versions differ and the body names which one answered"
            );
    }

    private static async Task<long> ReadNewestChangeVersionAsync(
        ApiIntegrationHarness harness,
        bool useSnapshot
    )
    {
        using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            AvailableChangeVersionsEndpoint,
            useSnapshot ? "true" : null
        );

        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        return JsonNode.Parse(body)!["newestChangeVersion"]!.GetValue<long>();
    }

    private static async Task AssertCountFromSelectedTargetAsync(
        ApiIntegrationHarness harness,
        IDerivativeTargetReachability reachability,
        string primaryConnectionString,
        string endpoint,
        int expectedCount
    )
    {
        await reachability.MakeUnreachableAsync(primaryConnectionString);

        try
        {
            using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Get,
                endpoint,
                useSnapshotHeaderValue: "true"
            );

            string body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);

            JsonNode
                .Parse(body)!
                .AsArray()
                .Count.Should()
                .Be(
                    expectedCount,
                    $"{endpoint} must report the snapshot's own tracked changes, not the parent's: {body}"
                );
        }
        finally
        {
            await reachability.MakeReachableAsync(primaryConnectionString);
        }
    }
}
