// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// The three-database arrangement every derivative-routing scenario shares: a parent, a read replica,
/// and a snapshot, each a separately provisioned database holding a Student nobody else holds.
/// </summary>
/// <remarks>
/// The distinguishing rows are written through the production HTTP write path rather than by hand,
/// by publishing a configuration whose parent is the database being seeded and then publishing the
/// real one. Hand-writing dms.Document and its projections would prove routing against rows no
/// production write could have produced, and would go stale the first time the write path changed.
/// </remarks>
internal static class DerivativeRoutingSupport
{
    public const string StudentsEndpoint = "/data/ed-fi/students";
    public const string UseSnapshotHeaderName = "Use-Snapshot";

    /// <summary>The Student unique id held only by the parent database.</summary>
    public const string PrimaryStudentUniqueId = "derivative-routing-primary";

    /// <summary>The Student unique id held only by the read replica.</summary>
    public const string ReplicaStudentUniqueId = "derivative-routing-replica";

    /// <summary>The Student unique id held only by the snapshot.</summary>
    public const string SnapshotStudentUniqueId = "derivative-routing-snapshot";

    /// <summary>
    /// How many Students each database holds. The counts differ so that a surface which reports shape
    /// rather than documents - a partition token list, a change version - still names the database that
    /// answered, instead of being identical across three clones.
    /// </summary>
    public const int PrimaryStudentCount = 1;

    public const int ReplicaStudentCount = 2;

    public const int SnapshotStudentCount = 3;

    public static FakeDataStoreDefinition ParentOnly(
        long id,
        string connectionString,
        RelationalProviderToken providerToken
    ) => new(id, connectionString, providerToken);

    public static FakeDataStoreDefinition ParentWith(
        long id,
        string connectionString,
        RelationalProviderToken providerToken,
        IReadOnlyDictionary<DataStoreDerivativeType, string> derivatives
    ) => new(id, connectionString, providerToken, derivatives);

    /// <summary>
    /// Seeds each database with its own Student by pointing the parent at it in turn, then publishes
    /// the arrangement under test. Every write goes through the same pipeline a real client uses.
    /// </summary>
    public static async Task SeedDistinguishableStudentsAsync(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        long dataStoreId,
        RelationalProviderToken providerToken,
        string primaryConnectionString,
        string replicaConnectionString,
        string snapshotConnectionString
    )
    {
        provider.Publish([ParentOnly(dataStoreId, replicaConnectionString, providerToken)]);
        await SeedDatabaseAsync(harness, ReplicaStudentUniqueId, ReplicaStudentCount);

        provider.Publish([ParentOnly(dataStoreId, snapshotConnectionString, providerToken)]);
        await SeedDatabaseAsync(harness, SnapshotStudentUniqueId, SnapshotStudentCount);

        provider.Publish([ParentOnly(dataStoreId, primaryConnectionString, providerToken)]);
        await SeedDatabaseAsync(harness, PrimaryStudentUniqueId, PrimaryStudentCount);

        PublishFullArrangement(
            provider,
            dataStoreId,
            providerToken,
            primaryConnectionString,
            replicaConnectionString,
            snapshotConnectionString
        );
    }

    public static void PublishFullArrangement(
        MutableInstanceProvider provider,
        long dataStoreId,
        RelationalProviderToken providerToken,
        string primaryConnectionString,
        string replicaConnectionString,
        string snapshotConnectionString
    ) =>
        provider.Publish([
            ParentWith(
                dataStoreId,
                primaryConnectionString,
                providerToken,
                new Dictionary<DataStoreDerivativeType, string>
                {
                    [DataStoreDerivativeType.ReadReplica] = replicaConnectionString,
                    [DataStoreDerivativeType.Snapshot] = snapshotConnectionString,
                }
            ),
        ]);

    /// <summary>
    /// Writes the marker Student that names this database, plus enough filler Students to reach the
    /// count that distinguishes it from the other two.
    /// </summary>
    private static async Task SeedDatabaseAsync(
        ApiIntegrationHarness harness,
        string markerStudentUniqueId,
        int totalCount
    )
    {
        await PostStudentAsync(harness, markerStudentUniqueId);

        for (int index = 1; index < totalCount; index++)
        {
            await PostStudentAsync(harness, $"{markerStudentUniqueId}-{index}");
        }
    }

    public static async Task PostStudentAsync(ApiIntegrationHarness harness, string studentUniqueId)
    {
        JsonObject payload = new() { ["studentUniqueId"] = studentUniqueId, ["firstName"] = "Ada" };

        using StringContent content = new(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(StudentsEndpoint, content);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Sends a request, optionally carrying the snapshot header with an exact value.</summary>
    public static async Task<HttpResponseMessage> SendAsync(
        ApiIntegrationHarness harness,
        HttpMethod method,
        string requestUri,
        string? useSnapshotHeaderValue = null,
        HttpContent? content = null
    )
    {
        using HttpRequestMessage request = new(method, requestUri);

        if (useSnapshotHeaderValue is not null)
        {
            request.Headers.TryAddWithoutValidation(UseSnapshotHeaderName, useSnapshotHeaderValue);
        }

        if (content is not null)
        {
            request.Content = content;
        }

        return await harness.HttpClient.SendAsync(request);
    }

    /// <summary>The studentUniqueId values a GET-many response carries, in response order.</summary>
    public static async Task<IReadOnlyList<string>> ReadStudentUniqueIdsAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        JsonNode? parsed = JsonNode.Parse(body);
        parsed.Should().NotBeNull("a GET-many response must be a JSON array");

        return [.. parsed!.AsArray().Select(element => element!["studentUniqueId"]!.GetValue<string>())];
    }

    /// <summary>
    /// The marker Student of whichever database served this request. Each database holds exactly one
    /// marker and a different total count, so both the identity and the shape of the response name it.
    /// </summary>
    public static async Task<string> ReadServingDatabaseAsync(HttpResponseMessage response)
    {
        IReadOnlyList<string> studentUniqueIds = await ReadStudentUniqueIdsAsync(response);

        string marker = studentUniqueIds.Should().ContainSingle(id => Markers.Contains(id)).Subject;

        studentUniqueIds
            .Should()
            .HaveCount(
                ExpectedCountFor(marker),
                $"the database holding {marker} holds a distinguishing number of Students"
            );

        return marker;
    }

    private static readonly string[] Markers =
    [
        PrimaryStudentUniqueId,
        ReplicaStudentUniqueId,
        SnapshotStudentUniqueId,
    ];

    /// <summary>How many Students the database identified by this marker holds.</summary>
    public static int ExpectedCountFor(string markerStudentUniqueId) =>
        markerStudentUniqueId switch
        {
            PrimaryStudentUniqueId => PrimaryStudentCount,
            ReplicaStudentUniqueId => ReplicaStudentCount,
            SnapshotStudentUniqueId => SnapshotStudentCount,
            _ => throw new ArgumentOutOfRangeException(nameof(markerStudentUniqueId)),
        };

    public static StringContent StudentContent(string studentUniqueId)
    {
        JsonObject payload = new() { ["studentUniqueId"] = studentUniqueId, ["firstName"] = "Ada" };

        return new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
    }

    public static StringContent RawContent(string body, string mediaType) =>
        new(body, Encoding.UTF8, new MediaTypeHeaderValue(mediaType).MediaType!);
}
