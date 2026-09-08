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
/// Proof that a routed request touches one database and no other, and that a request answered at
/// selection touches none at all.
/// </summary>
/// <remarks>
/// The three leased databases are clones: identical fingerprints, identical resource-key seeds,
/// identical schema. A response body therefore only names the database the repository query and
/// hydration read. These scenarios close that gap by making every database the request must not touch
/// unreachable, so a request that succeeded cannot have opened one - not for the fingerprint read, not
/// for the resource-key read, not for authorization SQL, not for hydration.
/// </remarks>
internal static class DerivativeWholeRequestScenario
{
    /// <summary>
    /// Every seam of a snapshot-routed request uses the snapshot. The parent and the replica are both
    /// unreachable for the duration, so any database work outside the snapshot would fail the request
    /// rather than pass unnoticed.
    /// </summary>
    public static async Task It_uses_only_the_selected_target_for_the_whole_request(
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
            using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Get,
                DerivativeRoutingSupport.StudentsEndpoint,
                useSnapshotHeaderValue: "true"
            );

            (await DerivativeRoutingSupport.ReadServingDatabaseAsync(response))
                .Should()
                .Be(
                    DerivativeRoutingSupport.SnapshotStudentUniqueId,
                    "the whole request - fingerprint, resource key, authorization, query, hydration - "
                        + "had only the snapshot available to it"
                );
        }
        finally
        {
            await reachability.MakeReachableAsync(primaryConnectionString);
            await reachability.MakeReachableAsync(replicaConnectionString);
        }
    }

    /// <summary>
    /// The same proof for the replica, so the mechanism is not specific to the snapshot branch of
    /// selection.
    /// </summary>
    public static async Task It_uses_only_the_replica_when_no_snapshot_is_requested(
        ApiIntegrationHarness harness,
        IDerivativeTargetReachability reachability,
        string primaryConnectionString,
        string snapshotConnectionString
    )
    {
        await reachability.MakeUnreachableAsync(primaryConnectionString);
        await reachability.MakeUnreachableAsync(snapshotConnectionString);

        try
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
        finally
        {
            await reachability.MakeReachableAsync(primaryConnectionString);
            await reachability.MakeReachableAsync(snapshotConnectionString);
        }
    }

    /// <summary>
    /// A missing-snapshot request is answered at selection, before anything is opened. Every configured
    /// database is unreachable, so a request that opened any of them could not return the typed
    /// not-found; it would fail on the connection instead.
    /// </summary>
    public static async Task It_opens_no_database_for_a_missing_snapshot(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        IDerivativeTargetReachability reachability,
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

        await reachability.MakeUnreachableAsync(primaryConnectionString);
        await reachability.MakeUnreachableAsync(replicaConnectionString);

        try
        {
            using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Get,
                DerivativeRoutingSupport.StudentsEndpoint,
                useSnapshotHeaderValue: "true"
            );

            string body = await response.Content.ReadAsStringAsync();

            response
                .StatusCode.Should()
                .Be(
                    HttpStatusCode.NotFound,
                    $"the answer is decided at selection, with every database unreachable: {body}"
                );
            body.Should().Contain("Snapshot not found.");
        }
        finally
        {
            await reachability.MakeReachableAsync(primaryConnectionString);
            await reachability.MakeReachableAsync(replicaConnectionString);
        }
    }

    /// <summary>
    /// A mutation rejected for asking for a snapshot is likewise decided before any database is opened.
    /// </summary>
    public static async Task It_opens_no_database_for_a_rejected_mutation(
        ApiIntegrationHarness harness,
        IDerivativeTargetReachability reachability,
        string primaryConnectionString,
        string replicaConnectionString,
        string snapshotConnectionString
    )
    {
        await reachability.MakeUnreachableAsync(primaryConnectionString);
        await reachability.MakeUnreachableAsync(replicaConnectionString);
        await reachability.MakeUnreachableAsync(snapshotConnectionString);

        try
        {
            using HttpContent content = DerivativeRoutingSupport.StudentContent(
                "derivative-routing-no-database"
            );
            using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Post,
                DerivativeRoutingSupport.StudentsEndpoint,
                useSnapshotHeaderValue: "true",
                content
            );

            response
                .StatusCode.Should()
                .Be(HttpStatusCode.MethodNotAllowed, "the rejection precedes every database acquisition");
        }
        finally
        {
            await reachability.MakeReachableAsync(primaryConnectionString);
            await reachability.MakeReachableAsync(replicaConnectionString);
            await reachability.MakeReachableAsync(snapshotConnectionString);
        }
    }

    /// <summary>
    /// A derivative that is unreachable fails the request, and the very next request at the same
    /// configured connection string succeeds once it is reachable again. Nothing about the first
    /// failure is retained: no cached validation verdict, no poisoned pool, no configuration change.
    /// </summary>
    public static async Task It_recovers_at_an_unchanged_derivative_connection_string(
        ApiIntegrationHarness harness,
        IDerivativeTargetReachability reachability,
        string snapshotConnectionString
    )
    {
        await reachability.MakeUnreachableAsync(snapshotConnectionString);

        try
        {
            using HttpResponseMessage unavailable = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Get,
                DerivativeRoutingSupport.StudentsEndpoint,
                useSnapshotHeaderValue: "true"
            );

            string unavailableBody = await unavailable.Content.ReadAsStringAsync();

            unavailable
                .StatusCode.Should()
                .Be(
                    HttpStatusCode.ServiceUnavailable,
                    "a configured derivative whose database cannot be opened fails at connection "
                        + $"acquisition, which is a transient fault rather than a missing snapshot: {unavailableBody}"
                );
        }
        finally
        {
            await reachability.MakeReachableAsync(snapshotConnectionString);
        }

        using HttpResponseMessage recovered = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint,
            useSnapshotHeaderValue: "true"
        );

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(recovered))
            .Should()
            .Be(
                DerivativeRoutingSupport.SnapshotStudentUniqueId,
                "the next request at the same configured string must retry rather than replay the failure"
            );

        // And again, so recovery is a steady state rather than one lucky retry.
        using HttpResponseMessage again = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint,
            useSnapshotHeaderValue: "true"
        );

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(again))
            .Should()
            .Be(DerivativeRoutingSupport.SnapshotStudentUniqueId);
    }

    /// <summary>
    /// An unreachable derivative must not disturb the parent: readiness and any request that selects
    /// the parent keep working, and nothing about the derivative was realized or pooled eagerly.
    /// </summary>
    public static async Task It_leaves_the_primary_alone_when_a_derivative_is_unreachable(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        IDerivativeTargetReachability reachability,
        long dataStoreId,
        RelationalProviderToken providerToken,
        string primaryConnectionString,
        string snapshotConnectionString
    )
    {
        // One unreachable derivative and one whose configured string names a database that does not
        // exist at all - the provider-invalid-at-open case - published together.
        provider.Publish([
            DerivativeRoutingSupport.ParentWith(
                dataStoreId,
                primaryConnectionString,
                providerToken,
                new Dictionary<DataStoreDerivativeType, string>
                {
                    [DataStoreDerivativeType.Snapshot] = snapshotConnectionString,
                    [DataStoreDerivativeType.ReadReplica] = reachability.AbsentDatabaseConnectionString(
                        primaryConnectionString
                    ),
                }
            ),
        ]);

        await reachability.MakeUnreachableAsync(snapshotConnectionString);

        try
        {
            using HttpResponseMessage health = await harness.HttpClient.GetAsync("/health");
            health.StatusCode.Should().Be(HttpStatusCode.OK, "health must depend on the primary alone");

            // A write selects the parent, so it must succeed while both derivatives are unusable.
            using HttpContent content = DerivativeRoutingSupport.StudentContent("routing-primary-ok");
            using HttpResponseMessage write = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Post,
                DerivativeRoutingSupport.StudentsEndpoint,
                useSnapshotHeaderValue: null,
                content
            );

            write.StatusCode.Should().Be(HttpStatusCode.Created, await write.Content.ReadAsStringAsync());
        }
        finally
        {
            await reachability.MakeReachableAsync(snapshotConnectionString);
        }
    }
}
