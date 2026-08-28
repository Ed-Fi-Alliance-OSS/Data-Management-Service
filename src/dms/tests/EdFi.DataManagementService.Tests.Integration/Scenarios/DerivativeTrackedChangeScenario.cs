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
        await SeedTrackedDeletesAsync(harness, SnapshotDeleteCount, "snap");

        provider.Publish([
            DerivativeRoutingSupport.ParentOnly(dataStoreId, primaryConnectionString, providerToken),
        ]);
        await SeedTrackedDeletesAsync(harness, PrimaryDeleteCount, "prim");

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
    /// The Student shape the DS 5.2 schema requires, which is richer than the focused fixtures'.
    /// </summary>
    private static HttpContent Ds52StudentContent(string studentUniqueId)
    {
        JsonObject payload = new()
        {
            ["studentUniqueId"] = studentUniqueId,
            ["firstName"] = "Ada",
            ["lastSurname"] = "Lovelace",
            ["birthDate"] = "2010-01-01",
        };

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
    /// The key-change surface routes the same way. Neither database has any key change, so the shared
    /// assertion is that the snapshot answers it with the whole primary unreachable, which no request
    /// touching the primary could do.
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
            JsonNode.Parse(body)!.AsArray().Should().BeEmpty("no identity was updated in either database");
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
