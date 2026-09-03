// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Paging;
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
    private const string PartitionsEndpoint = "/data/ed-fi/students/partitions?number=2";

    /// <summary>
    /// The window every min-only scenario here uses. It is open below every seeded row, so the same
    /// query is answered with the whole collection on each of the three databases and a difference in
    /// what a walk returns is a difference in which database served it, never in the window.
    /// </summary>
    private const string MinOnlyWindowQuery = "minChangeVersion=1";

    private const string MinOnlyPartitionsEndpoint = $"{PartitionsEndpoint}&{MinOnlyWindowQuery}";

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
    /// The paging surface partitions the database that served it, and walking those partitions returns
    /// that database's Students and no other's. Each database holds a different number of them, so the
    /// walked set is an exact identity rather than a shape that all three share.
    /// </summary>
    public static async Task It_partitions_the_selected_target(
        ApiIntegrationHarness harness,
        IDerivativeTargetReachability reachability,
        string primaryConnectionString,
        string replicaConnectionString
    )
    {
        await reachability.MakeUnreachableAsync(primaryConnectionString);
        await reachability.MakeUnreachableAsync(replicaConnectionString);

        try
        {
            using HttpResponseMessage partitionsResponse = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Get,
                PartitionsEndpoint,
                useSnapshotHeaderValue: "true"
            );

            string partitionsBody = await partitionsResponse.Content.ReadAsStringAsync();
            partitionsResponse.StatusCode.Should().Be(HttpStatusCode.OK, partitionsBody);

            string[] pageTokens =
            [
                .. JsonNode.Parse(partitionsBody)!["pageTokens"]!
                    .AsArray()
                    .Select(token => token!.GetValue<string>()),
            ];

            pageTokens.Should().NotBeEmpty("the snapshot holds Students to partition");

            HashSet<string> walked = await WalkPartitionsAsync(harness, pageTokens);

            walked
                .Should()
                .HaveCount(
                    DerivativeRoutingSupport.SnapshotStudentCount,
                    "the walk must cover the snapshot's Students, whose count differs from the "
                        + "parent's and the replica's"
                );
            walked
                .Should()
                .Contain(
                    DerivativeRoutingSupport.SnapshotStudentUniqueId,
                    "the snapshot's marker row must be among them"
                );
            walked
                .Should()
                .NotContain(DerivativeRoutingSupport.PrimaryStudentUniqueId)
                .And.NotContain(DerivativeRoutingSupport.ReplicaStudentUniqueId);
        }
        finally
        {
            await reachability.MakeReachableAsync(primaryConnectionString);
            await reachability.MakeReachableAsync(replicaConnectionString);
        }
    }

    /// <summary>
    /// Consumes every page token through the collection paging contract, following each partition's
    /// continuation to its end, and returns the studentUniqueId values the walk produced. Every request
    /// carries the snapshot header, because a walk that dropped it would page a different database.
    /// </summary>
    /// <param name="windowQuery">
    /// The change-version window the boundaries were cut over, repeated on every request of the walk.
    /// A window is not carried in a token, and the anchor is resolved from it, so a walk that dropped
    /// it would resolve a different anchor than the boundaries were computed under and reject them.
    /// Empty for an unwindowed partition set.
    /// </param>
    private static async Task<HashSet<string>> WalkPartitionsAsync(
        ApiIntegrationHarness harness,
        IReadOnlyList<string> pageTokens,
        string windowQuery = ""
    )
    {
        HashSet<string> walked = new(StringComparer.Ordinal);

        foreach (string partitionToken in pageTokens)
        {
            string? pageToken = partitionToken;

            // Bounded so a continuation that never terminates fails the test rather than hanging it.
            for (int page = 0; page < 10 && pageToken is not null; page++)
            {
                using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
                    harness,
                    HttpMethod.Get,
                    $"{DerivativeRoutingSupport.StudentsEndpoint}"
                        + $"?pageToken={Uri.EscapeDataString(pageToken)}&pageSize=1"
                        + (windowQuery.Length == 0 ? "" : $"&{windowQuery}"),
                    useSnapshotHeaderValue: "true"
                );

                string body = await response.Content.ReadAsStringAsync();
                response.StatusCode.Should().Be(HttpStatusCode.OK, body);

                foreach (JsonNode? document in JsonNode.Parse(body)!.AsArray())
                {
                    walked
                        .Add(document!["studentUniqueId"]!.GetValue<string>())
                        .Should()
                        .BeTrue("partitions are disjoint, so no document may be returned twice");
                }

                pageToken = response.Headers.TryGetValues("Next-Page-Token", out var next)
                    ? next.Single()
                    : null;
            }

            pageToken.Should().BeNull("every partition must be walked to its end");
        }

        return walked;
    }

    /// <summary>
    /// A min-only walk belongs to the data source it started against. The snapshot resolves the
    /// <c>ContentVersion</c> anchor for that window and every live source resolves <c>DocumentId</c>, so
    /// the token a page hands out names bounds in units only its own source reads. Adding or dropping
    /// the snapshot header mid-walk is answered with the invalid-page-token response, exactly as
    /// changing the window would be.
    /// </summary>
    /// <remarks>
    /// The verdict is reached by comparing the token's marker against the anchor the request resolves,
    /// before any row is read, so this proves the two sources resolve different anchors without needing
    /// the snapshot database to hold a ContentVersion order that diverges from its DocumentId order.
    /// </remarks>
    public static async Task It_binds_a_min_only_walk_to_the_source_that_issued_its_token(
        ApiIntegrationHarness harness
    )
    {
        string snapshotToken = await IssueMinOnlyPageTokenAsync(harness, useSnapshotHeaderValue: "true");
        string liveToken = await IssueMinOnlyPageTokenAsync(harness, useSnapshotHeaderValue: null);

        snapshotToken
            .Should()
            .NotBe(
                liveToken,
                "the two sources anchor the same window differently, so their tokens cannot be identical"
            );

        await AssertReplayAsync(harness, snapshotToken, useSnapshotHeaderValue: "true", accepted: true);
        await AssertReplayAsync(harness, snapshotToken, useSnapshotHeaderValue: null, accepted: false);

        // The mirror. The replica serves this one, and it is walked exactly as the parent would be:
        // anything short of a frozen source keeps the live anchor.
        await AssertReplayAsync(harness, liveToken, useSnapshotHeaderValue: null, accepted: true);
        await AssertReplayAsync(harness, liveToken, useSnapshotHeaderValue: "true", accepted: false);
    }

    /// <summary>
    /// The cross-operation half of the same rule. Boundaries are cut in the units the walk that
    /// consumes them reads, so /partitions resolves its anchor from the same two inputs GET-many does:
    /// against a frozen snapshot a min-only window balances on <c>ContentVersion</c>, not
    /// <c>DocumentId</c>. The boundaries therefore belong to the snapshot, and a walk that drops the
    /// header is answered with the invalid-page-token response rather than served rows read against
    /// the wrong column.
    /// </summary>
    /// <remarks>
    /// The unwindowed twin, <see cref="It_partitions_the_selected_target" />, runs entirely on the
    /// DocumentId anchor and would pass unchanged if the partition-side resolution regressed to the
    /// live rule. This is the scenario that fails if /partitions and GET-many ever resolve their
    /// anchors differently — the defect the partition step resolves its anchor at all to prevent,
    /// which neither operation's own tests can see.
    /// </remarks>
    public static async Task It_partitions_a_min_only_window_on_the_snapshot_anchor(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage partitionsResponse = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            MinOnlyPartitionsEndpoint,
            useSnapshotHeaderValue: "true"
        );

        string partitionsBody = await partitionsResponse.Content.ReadAsStringAsync();
        partitionsResponse.StatusCode.Should().Be(HttpStatusCode.OK, partitionsBody);

        string[] pageTokens =
        [
            .. JsonNode.Parse(partitionsBody)!["pageTokens"]!
                .AsArray()
                .Select(token => token!.GetValue<string>()),
        ];

        pageTokens.Should().NotBeEmpty("the snapshot holds Students inside the window");

        // The walk these boundaries were cut for. Covering the snapshot exactly once is only true if
        // the boundaries and the pages were computed over the same column: a ContentVersion boundary
        // consumed by a DocumentId page selection would overlap or leave a gap.
        HashSet<string> walked = await WalkPartitionsAsync(harness, pageTokens, MinOnlyWindowQuery);

        walked
            .Should()
            .HaveCount(
                DerivativeRoutingSupport.SnapshotStudentCount,
                "the walk must cover the snapshot's Students, whose count differs from the "
                    + "parent's and the replica's"
            );
        walked
            .Should()
            .Contain(
                DerivativeRoutingSupport.SnapshotStudentUniqueId,
                "the snapshot's marker row must be among them"
            );

        // Dropping the header resolves the live anchor for this window, so a boundary named in
        // ContentVersion units is no longer replayable — the same verdict a GET-many continuation gets.
        await AssertReplayAsync(harness, pageTokens[0], useSnapshotHeaderValue: null, accepted: false);
    }

    /// <summary>
    /// Takes the first page of a min-only walk and returns the continuation it hands out. The window is
    /// open below every seeded row, so the page is served and a continuation exists on all three
    /// databases.
    /// </summary>
    /// <remarks>
    /// The opening page is a traditional one: <c>pageSize</c> is the continuation's page size and is
    /// rejected without a <c>pageToken</c> to accompany it. That a traditional page hands out a
    /// continuation at all is the property this scenario turns on — the anchor is stamped into the
    /// token of every successful page, so a walk can start traditionally and continue by cursor.
    /// </remarks>
    private static async Task<string> IssueMinOnlyPageTokenAsync(
        ApiIntegrationHarness harness,
        string? useSnapshotHeaderValue
    )
    {
        using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            $"{DerivativeRoutingSupport.StudentsEndpoint}?{MinOnlyWindowQuery}&limit=1",
            useSnapshotHeaderValue
        );

        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        response
            .Headers.TryGetValues("Next-Page-Token", out var tokens)
            .Should()
            .BeTrue("a min-only page hands out a continuation on every data source");

        return tokens!.Single();
    }

    private static async Task AssertReplayAsync(
        ApiIntegrationHarness harness,
        string pageToken,
        string? useSnapshotHeaderValue,
        bool accepted
    )
    {
        string source = useSnapshotHeaderValue is null ? "without the header" : "with the header";

        using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            $"{DerivativeRoutingSupport.StudentsEndpoint}"
                + $"?{MinOnlyWindowQuery}&pageToken={Uri.EscapeDataString(pageToken)}&pageSize=1",
            useSnapshotHeaderValue
        );

        string body = await response.Content.ReadAsStringAsync();

        if (accepted)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"replayed {source}: {body}");
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"replayed {source}: {body}");
        body.Should().Contain(CursorRequestValidator.InvalidPageToken);
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
    /// The three request shapes that would modify data and have no route semantics of their own: a
    /// collection DELETE, a collection PUT, and an item POST. With a snapshot asked for, each is
    /// rejected at selection - before route semantics decides anything about them.
    /// </summary>
    public static async Task It_rejects_the_invalid_mutation_shapes_that_ask_for_a_snapshot(
        ApiIntegrationHarness harness
    )
    {
        foreach ((HttpMethod method, string uri, _) in InvalidMutationShapes())
        {
            using HttpContent? content =
                method == HttpMethod.Delete
                    ? null
                    : DerivativeRoutingSupport.StudentContent("routing-rejected");

            using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
                harness,
                method,
                uri,
                useSnapshotHeaderValue: "true",
                content
            );

            await AssertGenericMethodNotAllowedAsync(
                response,
                $"{method} {uri} with a snapshot request",
                expectedAllow: null
            );
        }
    }

    /// <summary>
    /// The same three shapes without the header, and with it parsed as false, keep the answer route
    /// semantics has always given them: the identical generic method-not-allowed. That is what makes
    /// the rejection above a selection decision rather than a change to how these shapes are answered.
    /// </summary>
    public static async Task It_keeps_the_route_semantics_answer_for_the_invalid_shapes(
        ApiIntegrationHarness harness
    )
    {
        foreach (string? headerValue in new[] { null, "false" })
        {
            foreach ((HttpMethod method, string uri, string[] allowed) in InvalidMutationShapes())
            {
                using HttpContent? content =
                    method == HttpMethod.Delete
                        ? null
                        : DerivativeRoutingSupport.StudentContent("routing-baseline");

                using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
                    harness,
                    method,
                    uri,
                    headerValue,
                    content
                );

                await AssertGenericMethodNotAllowedAsync(
                    response,
                    $"{method} {uri} with Use-Snapshot {headerValue ?? "absent"}",
                    expectedAllow: allowed
                );
            }
        }
    }

    /// <summary>The methods a collection path supports, which is what its route semantics advertises.</summary>
    private static readonly string[] _collectionMethods = ["GET", "POST"];

    /// <summary>And the methods an item path supports.</summary>
    private static readonly string[] _itemMethods = ["GET", "PUT", "DELETE"];

    /// <summary>
    /// The invalid shapes: a method against a path that has no route semantics for it, paired with the
    /// exact method set that path does support. The item id is well formed and names nothing, because
    /// whether the document exists must not matter.
    /// </summary>
    private static IEnumerable<(HttpMethod Method, string Uri, string[] Allowed)> InvalidMutationShapes()
    {
        string absentDocumentId = Guid.NewGuid().ToString();

        yield return (HttpMethod.Delete, DerivativeRoutingSupport.StudentsEndpoint, _collectionMethods);
        yield return (HttpMethod.Put, DerivativeRoutingSupport.StudentsEndpoint, _collectionMethods);
        yield return (
            HttpMethod.Post,
            $"{DerivativeRoutingSupport.StudentsEndpoint}/{absentDocumentId}",
            _itemMethods
        );
    }

    /// <summary>
    /// The generic method-not-allowed body both answers share: status, problem type, title, detail, and
    /// content type.
    /// </summary>
    /// <param name="expectedAllow">
    /// The exact <c>Allow</c> set expected, or null for the interim snapshot rejection, which carries
    /// none because the allowed-method set for a snapshot request is defined by separate work. The two
    /// answers differ here and only here, so this is the one thing a caller can use to tell them apart.
    /// The set is asserted exactly rather than as merely present: an <c>Allow</c> naming the very
    /// method being rejected, or silently narrowed to one entry, would satisfy a non-empty check and
    /// still be wrong.
    /// </param>
    private static async Task AssertGenericMethodNotAllowedAsync(
        HttpResponseMessage response,
        string because,
        string[]? expectedAllow
    )
    {
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, $"{because}: {body}");
        response
            .Content.Headers.ContentType?.ToString()
            .Should()
            .Be("application/json; charset=utf-8", because);

        if (expectedAllow is not null)
        {
            response
                .Content.Headers.Allow.Should()
                .BeEquivalentTo(
                    expectedAllow,
                    $"{because}: route semantics advertises exactly the methods the path supports"
                );
        }
        else
        {
            response
                .Content.Headers.Allow.Should()
                .BeEmpty($"{because}: the interim snapshot rejection defines no allowed-method set");
        }

        JsonNode problem = JsonNode.Parse(body)!;
        problem["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:method-not-allowed", because);
        problem["title"]!.GetValue<string>().Should().Be("Method Not Allowed", because);
        problem["detail"]!.GetValue<string>().Should().Be("The request construction was invalid.", because);
        problem["status"]!.GetValue<int>().Should().Be(405, because);
    }

    /// <summary>
    /// A valid write with no snapshot asked for still reaches the parent, so the rejection above is
    /// about the invalid shapes rather than about writes in general.
    /// </summary>
    public static async Task It_leaves_a_valid_write_alone_without_a_snapshot_request(
        ApiIntegrationHarness harness
    )
    {
        foreach (string? headerValue in new[] { null, "false" })
        {
            using HttpContent content = DerivativeRoutingSupport.StudentContent(
                $"routing-write-{headerValue ?? "absent"}"
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
                .Be(HttpStatusCode.Created, $"a valid write must still reach the parent: {body}");
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

        served.Should().ContainSingle(id => id == DerivativeRoutingSupport.ReplicaStudentUniqueId);
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
    /// A configuration change published while a request is provably in flight does not interrupt or
    /// redirect it. The held request is parked at hydration, which runs long after its target was
    /// selected and its repository query ran, so it is committed to the replica when the replacement
    /// is published; it must still answer from the replica, and the next request must observe the new
    /// configuration.
    /// </summary>
    public static async Task It_does_not_interrupt_an_in_flight_request_when_configuration_changes(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        HydrationGate gate,
        long dataStoreId,
        RelationalProviderToken providerToken,
        string primaryConnectionString,
        string replicaConnectionString,
        string snapshotConnectionString
    )
    {
        gate.Arm();

        Task<HttpResponseMessage> inFlight = DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint
        );

        try
        {
            // Deterministic: the request is inside hydration, past selection and past its query.
            await gate.Arrived.WaitAsync(TimeSpan.FromSeconds(30));

            provider.Publish([
                DerivativeRoutingSupport.ParentOnly(dataStoreId, primaryConnectionString, providerToken),
            ]);
        }
        finally
        {
            // Released even when the wait or the publish fails, so a failure here cannot strand the
            // held request and every later test in the fixture.
            gate.Release();
        }

        using HttpResponseMessage held = await inFlight;

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(held))
            .Should()
            .Be(
                DerivativeRoutingSupport.ReplicaStudentUniqueId,
                "a request already committed to the replica must finish there"
            );

        using HttpResponseMessage next = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint
        );

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(next))
            .Should()
            .Be(
                DerivativeRoutingSupport.PrimaryStudentUniqueId,
                "the next request must observe the configuration published while the first was held"
            );

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
