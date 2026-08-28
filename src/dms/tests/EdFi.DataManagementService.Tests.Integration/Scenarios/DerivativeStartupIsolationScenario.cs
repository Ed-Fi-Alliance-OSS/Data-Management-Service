// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// A host that started with an unusable snapshot and an unusable read replica already configured.
/// </summary>
/// <remarks>
/// The derivatives are published before the host boots, so startup, readiness, and the whole
/// registration graph see them. What must be true is not merely that no request opened them - a data
/// source can be built without ever connecting - but that nothing built, leased, or realized them at
/// all. That is what the realization recorder observes.
/// </remarks>
internal static class DerivativeStartupIsolationScenario
{
    /// <summary>The host started, and readiness depends on the primary alone.</summary>
    public static async Task It_starts_and_reports_healthy(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage health = await harness.HttpClient.GetAsync("/health");

        health.StatusCode.Should().Be(HttpStatusCode.OK, await health.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Nothing about either derivative was built, leased, or realized during startup or during a
    /// request that selects the parent.
    /// </summary>
    public static async Task It_realizes_no_derivative(
        ApiIntegrationHarness harness,
        DerivativeRealizationRecorder recorder,
        string primaryConnectionString,
        string snapshotConnectionString,
        string replicaConnectionString
    )
    {
        recorder
            .CountFor(snapshotConnectionString)
            .Should()
            .Be(0, "a configured snapshot nobody asked for must cost nothing at startup");
        recorder
            .CountFor(replicaConnectionString)
            .Should()
            .Be(0, "and neither must a configured read replica");

        // A write selects the parent, so it exercises the acquisition path without asking for either.
        using HttpContent content = DerivativeRoutingSupport.StudentContent("startup-primary-write");
        using HttpResponseMessage write = await harness.HttpClient.PostAsync(
            DerivativeRoutingSupport.StudentsEndpoint,
            content
        );

        write.StatusCode.Should().Be(HttpStatusCode.Created, await write.Content.ReadAsStringAsync());

        recorder
            .CountFor(snapshotConnectionString)
            .Should()
            .Be(0, "a parent-selecting request must not realize the snapshot either");
        recorder.CountFor(replicaConnectionString).Should().Be(0);

        recorder
            .CountFor(primaryConnectionString)
            .Should()
            .BeGreaterThan(
                0,
                "the primary was realized, so the recorder is wired to the path under test rather "
                    + "than observing nothing at all"
            );
    }

    /// <summary>
    /// And a read that does ask for the snapshot fails, rather than the configuration having been
    /// quietly discarded at startup - the derivative is configured, it is simply unusable. The
    /// recorder sees that request realize the snapshot, which is what makes the zero counts above a
    /// statement about the requests that ran rather than about a boundary nothing ever reaches.
    /// </summary>
    /// <remarks>
    /// Runs after <see cref="It_realizes_no_derivative" />, because it is the one request in the
    /// fixture that deliberately does realize a derivative.
    /// </remarks>
    public static async Task It_still_offers_the_configured_snapshot(
        ApiIntegrationHarness harness,
        DerivativeRealizationRecorder recorder,
        string snapshotConnectionString
    )
    {
        using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint,
            useSnapshotHeaderValue: "true"
        );

        response
            .StatusCode.Should()
            .NotBe(
                HttpStatusCode.NotFound,
                "the snapshot is configured, so this is an unusable target rather than a missing one"
            );
        response
            .StatusCode.Should()
            .NotBe(HttpStatusCode.OK, "and it cannot be opened, so the request cannot succeed");

        recorder
            .CountFor(snapshotConnectionString)
            .Should()
            .BeGreaterThan(
                0,
                "a request that selects the snapshot reaches its database through the recorded "
                    + "acquisition boundary, starting with the fingerprint and resource-key "
                    + "validation reads"
            );
    }
}
