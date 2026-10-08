// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.SchemaTools.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture(Ddl.CdcProvider.Postgresql)]
[TestFixture(Ddl.CdcProvider.SqlServer)]
[NonParallelizable]
[Platform(Exclude = "Win", Reason = "Local CDC state requires Unix owner-only permissions.")]
internal class Given_Cdc_runbook_status_output(Ddl.CdcProvider provider) : CdcReadinessTestBase(provider)
{
    [TestCase("ready")]
    [TestCase("backlog")]
    [TestCase("unavailable")]
    [TestCase("terminal")]
    public async Task It_matches_status_excerpts_and_optional_fields_from_the_controller(string scenario)
    {
        _backlog = scenario == "backlog";
        if (scenario == "terminal")
        {
            _offsetState = CdcConnectOffsetState.Missing;
        }
        if (scenario == "unavailable")
        {
            A.CallTo(() =>
                    _metrics.CollectAsync(
                        A<CdcDeploymentRequest>._,
                        A<CdcTelemetryObservationPass>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(
                    new CdcTransportResult<CdcConnectorTelemetryObservation>.Unavailable(
                        new(CdcDeploymentComponent.Metrics, CdcDeploymentFailure.Unavailable)
                    )
                );
        }
        bool stopped = false;
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                stopped = true;
                return Observed(new CdcTransportAcknowledgement());
            });
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                var current = Status();
                return Observed(
                    stopped
                        ? new CdcConnectStatus(
                            current.Runtime with
                            {
                                ConnectorState = CdcConnectorRuntimeState.Stopped,
                                SoleTaskState = CdcConnectorRuntimeState.Stopped,
                                TaskCount = 0,
                                RunningTaskCount = 0,
                            },
                            current.WorkerId,
                            []
                        )
                        : current
                );
            });
        var bindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        var controller = new CdcControllerStatus(
            _store,
            bindings,
            _connect,
            _ =>
                new(
                    _store,
                    bindings,
                    _provider,
                    _templates,
                    _kafka,
                    _connect,
                    _worker,
                    _metrics,
                    _positions,
                    TimeProvider.System
                ),
            TimeProvider.System
        );
        var status = await controller.StatusAsync([new(_request, _runtime, 1000)]);
        bool ready = status.Aggregate.Readiness == CdcReadiness.Ready;
        var result = new CdcCommandResult("status", ready, ready ? 0 : 1, [], status);
        string json = JsonSerializer.Serialize(result, CdcCommandHost.JsonOptions);
        CdcRunbookExamples.AssertExcerpt(
            "cdc-output-" + scenario,
            json,
            "operation",
            "succeeded",
            "exitCode",
            "data/aggregate/readiness",
            "data/targets/0/details/queuePresence",
            "data/targets/0/details/lagMilliseconds",
            "data/targets/0/details/p95LagMilliseconds",
            "data/targets/0/incidentPersistence",
            "data/targets/0/containment",
            "data/targets/0/status/sourceHistory/incidentLatched"
        );
        status.Targets[0].Details.P95LagMilliseconds.Should().BeNull();
    }
}

[TestFixture]
public class Given_Cdc_runbook_result_output
{
    internal static CdcCommandResult StopResult =>
        new(
            "stop",
            true,
            0,
            [],
            new CdcManagedLifecycleResult(
                CdcManagedLifecycleOperation.Stop,
                true,
                true,
                false,
                CdcManagedLifecycleBoundary.VerifiedManagedStop,
                []
            )
            {
                Recovery = new(CdcRecoveryBoundary.VerifiedManagedStop, false),
            }
        );

    internal static CdcCommandResult RetireResult =>
        new(
            "retire",
            true,
            0,
            [],
            new CdcBindingRetirementResult(true, Guid.Parse("11111111-1111-1111-1111-111111111111"), [])
        );

    internal static readonly string[] StopPaths =
    [
        "operation",
        "succeeded",
        "exitCode",
        "diagnostics",
        "data/operation",
        "data/succeeded",
        "data/targetShutdownVerified",
        "data/ready",
        "data/boundary",
        "data/diagnostics",
        "data/observation",
        "data/recovery/boundary",
        "data/recovery/requiresFreshPass",
        "data/recovery/unobservedIntervalCertified",
    ];
    internal static readonly string[] RetirePaths =
    [
        "operation",
        "succeeded",
        "exitCode",
        "diagnostics",
        "data",
    ];

    [TestCase("stop")]
    [TestCase("retire")]
    public void It_matches_operation_scoped_results(string operation) =>
        CdcRunbookExamples.AssertExcerpt(
            "cdc-output-" + operation,
            JsonSerializer.Serialize(
                operation == "stop" ? StopResult : RetireResult,
                CdcCommandHost.JsonOptions
            ),
            operation == "stop" ? StopPaths : RetirePaths
        );

    [TestCase("field")]
    [TestCase("missing")]
    [TestCase("value")]
    public void It_rejects_a_mutated_copy_of_the_marked_result(string mutation)
    {
        string original = CdcRunbookExamples.Read("cdc-output-stop", "json");
        string copy = mutation switch
        {
            "field" => original.Replace(
                "targetShutdownVerified",
                "TargetShutdownVerified",
                StringComparison.Ordinal
            ),
            "missing" => original.Replace("\"targetShutdownVerified\": true,", "", StringComparison.Ordinal),
            _ => original.Replace(
                "\"targetShutdownVerified\": true",
                "\"targetShutdownVerified\": false",
                StringComparison.Ordinal
            ),
        };
        copy.Should().NotBe(original);
        Action check = () =>
            CdcRunbookExamples.AssertExcerptJson(
                copy,
                JsonSerializer.Serialize(StopResult, CdcCommandHost.JsonOptions),
                StopPaths
            );
        check.Should().Throw<AssertionException>();
    }

    [TestCase("{", typeof(JsonException))]
    [TestCase("{}", typeof(AssertionException))]
    [TestCase("{\"Succeeded\":true}", typeof(AssertionException))]
    [TestCase("{\"succeeded\":false}", typeof(AssertionException))]
    public void It_rejects_malformed_missing_or_incorrect_copies(string copy, Type exception)
    {
        Action check = () =>
            CdcRunbookExamples.AssertExcerptJson(
                copy,
                JsonSerializer.Serialize(StopResult, CdcCommandHost.JsonOptions),
                "succeeded"
            );
        check.Should().Throw<Exception>().Which.Should().BeAssignableTo(exception);
    }
}
