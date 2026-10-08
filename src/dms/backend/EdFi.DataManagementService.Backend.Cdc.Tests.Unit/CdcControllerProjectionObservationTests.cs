// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(Ddl.CdcProvider.Postgresql)]
[TestFixture(Ddl.CdcProvider.SqlServer)]
[Platform(Exclude = "Win", Reason = "Local CDC state requires Unix owner-only permissions.")]
internal class Given_Cdc_projection_observation_clocks(Ddl.CdcProvider provider)
    : CdcReadinessTestBase(provider)
{
    [TestCase(-300)]
    [TestCase(300)]
    public void It_correlates_a_fresh_durable_read_on_the_controller_clock(int databaseClockSkewSeconds)
    {
        var started = DateTimeOffset.UtcNow;
        var response = Projection(databaseClockSkew: TimeSpan.FromSeconds(databaseClockSkewSeconds));
        var finished = DateTimeOffset.UtcNow;
        var observation = CdcControllerObservations.Projection(
            _request,
            response,
            Guid.NewGuid().ToString("D"),
            started,
            finished
        );
        observation.ProjectionObservedAt.Should().Be(finished);
    }

    [TestCase("stale-response")]
    [TestCase("future-response")]
    [TestCase("mismatched-process")]
    [TestCase("missing-durable")]
    [TestCase("expired-call")]
    public void It_rejects_missing_durable_evidence_and_stale_or_uncorrelated_host_observations(string fault)
    {
        var started = DateTimeOffset.UtcNow;
        var response = Projection(
            processClockSkew: fault == "mismatched-process" ? TimeSpan.FromSeconds(-1) : TimeSpan.Zero,
            missingDurableObservation: fault == "missing-durable"
        );
        var finished = DateTimeOffset.UtcNow;
        if (fault == "stale-response")
        {
            response = new(started.AddTicks(-1), response.Targets);
        }
        if (fault == "future-response")
        {
            response = new(finished.AddTicks(1), response.Targets);
        }
        if (fault == "expired-call")
        {
            finished = started + _request.Timing.MaximumObservationAge + TimeSpan.FromTicks(1);
        }
        Action act = () =>
            CdcControllerObservations.Projection(
                _request,
                response,
                Guid.NewGuid().ToString("D"),
                started,
                finished
            );
        act.Should().Throw<CdcWorkflowStateException>();
    }
}
