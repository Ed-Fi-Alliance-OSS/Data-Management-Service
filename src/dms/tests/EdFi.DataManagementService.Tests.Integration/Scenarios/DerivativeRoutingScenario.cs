// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Whole requests, over HTTP, against three separately provisioned databases. Every assertion here is
/// about which database answered: each holds exactly one Student, so the row that comes back names the
/// target the entire request used - fingerprint read, resource-key read, authorization SQL, repository
/// query, and hydration alike, because a request that split across targets could not return one
/// database's row.
/// </summary>
internal static class DerivativeRoutingScenario
{
    private const string DeletesEndpoint = "/data/ed-fi/students/deletes";
    private const string KeyChangesEndpoint = "/data/ed-fi/students/keyChanges";
    private const string AvailableChangeVersionsEndpoint = "/changeQueries/v1/availableChangeVersions";
    private const string PartitionsEndpoint = "/data/ed-fi/students/partitions?number=2";

    /// <summary>
    /// With a replica configured and no snapshot asked for, an eligible read is served by the replica.
    /// </summary>
    public static async Task It_serves_an_eligible_read_from_the_replica(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint
        );

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(response))
            .Should()
            .Be(DerivativeRoutingSupport.ReplicaStudentUniqueId);
    }

    /// <summary>An explicitly requested snapshot outranks a configured replica.</summary>
    public static async Task It_prefers_the_snapshot_over_the_replica(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint,
            useSnapshotHeaderValue: "true"
        );

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(response))
            .Should()
            .Be(DerivativeRoutingSupport.SnapshotStudentUniqueId);
    }

    /// <summary>
    /// GET-by-id follows the same target as GET-many. The id is resolved against the serving database,
    /// so a document that exists only on the replica is retrievable and the parent's is not.
    /// </summary>
    public static async Task It_serves_a_get_by_id_from_the_same_target(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage manyResponse = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint
        );

        string body = await manyResponse.Content.ReadAsStringAsync();
        manyResponse.StatusCode.Should().Be(HttpStatusCode.OK, body);

        string id = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsArray()[0]!["id"]!.GetValue<string>();

        using HttpResponseMessage byIdResponse = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            $"{DerivativeRoutingSupport.StudentsEndpoint}/{id}"
        );

        string byIdBody = await byIdResponse.Content.ReadAsStringAsync();
        byIdResponse.StatusCode.Should().Be(HttpStatusCode.OK, byIdBody);
        System.Text.Json.Nodes.JsonNode.Parse(byIdBody)!["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be(DerivativeRoutingSupport.ReplicaStudentUniqueId);
    }

    /// <summary>
    /// The other eligible read surfaces reach the derivative rather than the parent. Their bodies
    /// describe change-tracking and paging state rather than documents, so the assertion is that they
    /// are served successfully from the selected target.
    /// </summary>
    public static async Task It_routes_every_eligible_read_surface(ApiIntegrationHarness harness)
    {
        string[] endpoints = [AvailableChangeVersionsEndpoint, PartitionsEndpoint];

        foreach (string endpoint in endpoints)
        {
            using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Get,
                endpoint,
                useSnapshotHeaderValue: "true"
            );

            string body = await response.Content.ReadAsStringAsync();
            response
                .StatusCode.Should()
                .Be(HttpStatusCode.OK, $"{endpoint} must be served from the snapshot: {body}");
        }
    }

    /// <summary>
    /// The tracked-change surfaces are routing-neutral in this fixture: selecting a derivative changes
    /// nothing about how they answer.
    /// </summary>
    /// <remarks>
    /// This fixture's ApiSchema carries no Change Query response-field mapping for the Student
    /// identity, so both surfaces answer 500 with
    /// <c>Unable to map tracked-change identity path '$.studentUniqueId'</c> whichever target serves
    /// them. That is a property of the fixture rather than of routing, and it predates this work - no
    /// API-level test exercises either surface today. Asserting the two answers are identical is what
    /// can honestly be proven here; proving they route positively needs a fixture whose schema maps
    /// tracked-change identities.
    /// </remarks>
    public static async Task It_answers_the_tracked_change_surfaces_the_same_way_either_side(
        ApiIntegrationHarness harness
    )
    {
        string[] endpoints = [DeletesEndpoint, KeyChangesEndpoint];

        foreach (string endpoint in endpoints)
        {
            using HttpResponseMessage snapshotResponse = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Get,
                endpoint,
                useSnapshotHeaderValue: "true"
            );
            using HttpResponseMessage replicaResponse = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Get,
                endpoint
            );

            snapshotResponse
                .StatusCode.Should()
                .Be(
                    replicaResponse.StatusCode,
                    $"{endpoint} must not answer differently because a snapshot was selected"
                );
        }
    }

    /// <summary>
    /// A snapshot asked for on a data store that configures none is a typed not-found, decided at
    /// selection. No database is opened for it, which is what keeps a request for an absent snapshot
    /// from silently reading the parent.
    /// </summary>
    public static async Task It_returns_not_found_when_no_snapshot_is_configured(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        long dataStoreId,
        RelationalProviderToken providerToken,
        string primaryConnectionString,
        string replicaConnectionString
    )
    {
        provider.Publish([
            DerivativeRoutingSupport.ParentWith(
                dataStoreId,
                primaryConnectionString,
                providerToken,
                new Dictionary<DataStoreDerivativeType, string>
                {
                    [DataStoreDerivativeType.ReadReplica] = replicaConnectionString,
                }
            ),
        ]);

        using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint,
            useSnapshotHeaderValue: "true"
        );

        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
        body.Should().Contain("Snapshot not found.");
    }

    /// <summary>
    /// The snapshot header does not turn a write into a read. Every mutation shape is rejected before
    /// route semantics, and the response is the existing generic method-not-allowed.
    /// </summary>
    public static async Task It_rejects_a_mutation_that_asks_for_a_snapshot(ApiIntegrationHarness harness)
    {
        // A well-formed document id that names nothing. The rejection must happen before the request
        // reaches route semantics, so whether the document exists is deliberately irrelevant.
        string absentDocumentId = Guid.NewGuid().ToString();

        (HttpMethod Method, string Uri)[] mutations =
        [
            (HttpMethod.Post, DerivativeRoutingSupport.StudentsEndpoint),
            (HttpMethod.Put, $"{DerivativeRoutingSupport.StudentsEndpoint}/{absentDocumentId}"),
            (HttpMethod.Delete, $"{DerivativeRoutingSupport.StudentsEndpoint}/{absentDocumentId}"),
        ];

        foreach ((HttpMethod method, string uri) in mutations)
        {
            using HttpContent? content =
                method == HttpMethod.Delete
                    ? null
                    : DerivativeRoutingSupport.StudentContent("derivative-routing-rejected");

            using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
                harness,
                method,
                uri,
                useSnapshotHeaderValue: "true",
                content
            );

            string body = await response.Content.ReadAsStringAsync();
            response
                .StatusCode.Should()
                .Be(
                    HttpStatusCode.MethodNotAllowed,
                    $"{method} with a snapshot request must stop at selection: {body}"
                );
            response.Content.Headers.Allow.Should().BeEmpty("the interim response carries no Allow");
        }
    }

    /// <summary>
    /// Without the header, and with it parsed as false, a mutation behaves exactly as it always has.
    /// This is what keeps the rejection above from being a change to ordinary write behavior.
    /// </summary>
    public static async Task It_leaves_a_mutation_alone_without_a_snapshot_request(
        ApiIntegrationHarness harness
    )
    {
        foreach (string? headerValue in new[] { null, "false" })
        {
            using HttpContent content = DerivativeRoutingSupport.StudentContent(
                $"derivative-routing-write-{headerValue ?? "absent"}"
            );

            using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Post,
                DerivativeRoutingSupport.StudentsEndpoint,
                headerValue,
                content
            );

            string body = await response.Content.ReadAsStringAsync();
            response
                .StatusCode.Should()
                .Be(HttpStatusCode.Created, $"a write must still reach the parent: {body}");
        }
    }

    /// <summary>
    /// A write reaches the parent even while reads are being served by the replica, so the two targets
    /// are genuinely separate databases rather than one database reached two ways.
    /// </summary>
    public static async Task It_writes_to_the_parent_while_reads_go_to_the_replica(
        ApiIntegrationHarness harness
    )
    {
        const string WrittenStudentUniqueId = "derivative-routing-written";

        using HttpContent content = DerivativeRoutingSupport.StudentContent(WrittenStudentUniqueId);
        using HttpResponseMessage writeResponse = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Post,
            DerivativeRoutingSupport.StudentsEndpoint,
            useSnapshotHeaderValue: null,
            content
        );

        writeResponse
            .StatusCode.Should()
            .Be(HttpStatusCode.Created, await writeResponse.Content.ReadAsStringAsync());

        using HttpResponseMessage readResponse = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint
        );

        IReadOnlyList<string> served = await DerivativeRoutingSupport.ReadStudentUniqueIdsAsync(readResponse);

        served.Should().ContainSingle().Which.Should().Be(DerivativeRoutingSupport.ReplicaStudentUniqueId);
        served
            .Should()
            .NotContain(
                WrittenStudentUniqueId,
                "the write landed on the parent, which this read never touched"
            );
    }

    /// <summary>
    /// Replacing a derivative's connection string through the configuration the host reads is enough to
    /// move the next request; nothing is cached across the change.
    /// </summary>
    public static async Task It_serves_the_replacement_after_a_derivative_is_replaced(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        long dataStoreId,
        RelationalProviderToken providerToken,
        string primaryConnectionString,
        string replicaConnectionString,
        string snapshotConnectionString
    )
    {
        using (
            HttpResponseMessage before = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Get,
                DerivativeRoutingSupport.StudentsEndpoint
            )
        )
        {
            (await DerivativeRoutingSupport.ReadServingDatabaseAsync(before))
                .Should()
                .Be(DerivativeRoutingSupport.ReplicaStudentUniqueId);
        }

        // The replica is repointed at the snapshot's database - a replacement, not a removal.
        provider.Publish([
            DerivativeRoutingSupport.ParentWith(
                dataStoreId,
                primaryConnectionString,
                providerToken,
                new Dictionary<DataStoreDerivativeType, string>
                {
                    [DataStoreDerivativeType.ReadReplica] = snapshotConnectionString,
                }
            ),
        ]);

        using HttpResponseMessage after = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint
        );

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(after))
            .Should()
            .Be(
                DerivativeRoutingSupport.SnapshotStudentUniqueId,
                "the replaced replica connection string names the snapshot's database"
            );
    }

    /// <summary>
    /// Removing every derivative sends eligible reads back to the parent. There is no stale routing and
    /// no failure: the request is simply served by the database that is still configured.
    /// </summary>
    public static async Task It_returns_to_the_parent_after_the_derivatives_are_removed(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        long dataStoreId,
        RelationalProviderToken providerToken,
        string primaryConnectionString
    )
    {
        provider.Publish([
            DerivativeRoutingSupport.ParentOnly(dataStoreId, primaryConnectionString, providerToken),
        ]);

        using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint
        );

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(response))
            .Should()
            .Be(DerivativeRoutingSupport.PrimaryStudentUniqueId);
    }

    /// <summary>
    /// A configuration change while requests are in flight does not interrupt them. Each request keeps
    /// the target it selected, and every one of them answers successfully from a database that held a
    /// Student.
    /// </summary>
    public static async Task It_does_not_interrupt_in_flight_requests_when_configuration_changes(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        long dataStoreId,
        RelationalProviderToken providerToken,
        string primaryConnectionString,
        string replicaConnectionString,
        string snapshotConnectionString
    )
    {
        const int RequestCount = 12;

        Task<HttpResponseMessage>[] inFlight =
        [
            .. Enumerable
                .Range(0, RequestCount)
                .Select(_ =>
                    DerivativeRoutingSupport.SendAsync(
                        harness,
                        HttpMethod.Get,
                        DerivativeRoutingSupport.StudentsEndpoint
                    )
                ),
        ];

        // Published while those requests are outstanding.
        provider.Publish([
            DerivativeRoutingSupport.ParentOnly(dataStoreId, primaryConnectionString, providerToken),
        ]);

        HttpResponseMessage[] responses = await Task.WhenAll(inFlight);

        try
        {
            foreach (HttpResponseMessage response in responses)
            {
                string served = await DerivativeRoutingSupport.ReadServingDatabaseAsync(response);
                served
                    .Should()
                    .BeOneOf(
                        [
                            DerivativeRoutingSupport.ReplicaStudentUniqueId,
                            DerivativeRoutingSupport.PrimaryStudentUniqueId,
                        ],
                        "every request must complete against one whole database, whichever configuration it observed"
                    );
            }
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }

        DerivativeRoutingSupport.PublishFullArrangement(
            provider,
            dataStoreId,
            providerToken,
            primaryConnectionString,
            replicaConnectionString,
            snapshotConnectionString
        );
    }

    /// <summary>
    /// Client authorization is resolved from the parent's claim set, not from the target that serves
    /// the request: a resource the client may not read is refused even while routing succeeds.
    /// </summary>
    public static async Task It_authorizes_from_the_parent_while_serving_a_derivative(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage authorized = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint
        );

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(authorized))
            .Should()
            .Be(DerivativeRoutingSupport.ReplicaStudentUniqueId);

        // A resource outside this fixture's claim set is refused on the same routed path.
        using HttpResponseMessage refused = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            "/data/ed-fi/students/deletes",
            useSnapshotHeaderValue: null
        );

        refused.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A mutation that would answer 415 or 400 on its own still stops at selection when it asks for a
    /// snapshot: the rejection precedes content-type and body validation.
    /// </summary>
    public static async Task It_stops_at_selection_before_content_and_body_validation(
        ApiIntegrationHarness harness
    )
    {
        using (
            HttpContent wrongMediaType = DerivativeRoutingSupport.RawContent(
                """{"studentUniqueId":"derivative-routing-415"}""",
                "text/plain"
            )
        )
        using (
            HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Post,
                DerivativeRoutingSupport.StudentsEndpoint,
                useSnapshotHeaderValue: "true",
                wrongMediaType
            )
        )
        {
            response
                .StatusCode.Should()
                .Be(
                    HttpStatusCode.MethodNotAllowed,
                    "an unsupported media type must never be reached once a snapshot was asked for"
                );
        }

        using HttpContent malformedBody = DerivativeRoutingSupport.RawContent(
            "{ this is not json",
            "application/json"
        );
        using HttpResponseMessage malformedResponse = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Post,
            DerivativeRoutingSupport.StudentsEndpoint,
            useSnapshotHeaderValue: "true",
            malformedBody
        );

        malformedResponse
            .StatusCode.Should()
            .Be(
                HttpStatusCode.MethodNotAllowed,
                "a malformed body must never be parsed once a snapshot was asked for"
            );
    }

    /// <summary>
    /// An unknown resource is a 404 whether or not a snapshot was asked for: the reordering puts
    /// endpoint validation before target selection, so the snapshot request never changes the answer.
    /// </summary>
    public static async Task It_returns_not_found_for_an_unknown_resource(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Post,
            "/data/ed-fi/thisResourceDoesNotExist",
            useSnapshotHeaderValue: "true",
            DerivativeRoutingSupport.StudentContent("derivative-routing-unknown")
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
