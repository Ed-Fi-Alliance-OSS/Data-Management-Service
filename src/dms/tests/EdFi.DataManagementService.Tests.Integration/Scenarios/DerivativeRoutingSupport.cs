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
        await PostStudentAsync(harness, ReplicaStudentUniqueId);

        provider.Publish([ParentOnly(dataStoreId, snapshotConnectionString, providerToken)]);
        await PostStudentAsync(harness, SnapshotStudentUniqueId);

        provider.Publish([ParentOnly(dataStoreId, primaryConnectionString, providerToken)]);
        await PostStudentAsync(harness, PrimaryStudentUniqueId);

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
    /// The one Student the target that served this request holds. Each database holds exactly one, so
    /// the identity of the row is the identity of the database.
    /// </summary>
    public static async Task<string> ReadServingDatabaseAsync(HttpResponseMessage response)
    {
        IReadOnlyList<string> studentUniqueIds = await ReadStudentUniqueIdsAsync(response);

        return studentUniqueIds
            .Should()
            .ContainSingle("each leased database holds exactly one Student")
            .Subject;
    }

    public static StringContent StudentContent(string studentUniqueId)
    {
        JsonObject payload = new() { ["studentUniqueId"] = studentUniqueId, ["firstName"] = "Ada" };

        return new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
    }

    public static StringContent RawContent(string body, string mediaType) =>
        new(body, Encoding.UTF8, new MediaTypeHeaderValue(mediaType).MediaType!);
}
