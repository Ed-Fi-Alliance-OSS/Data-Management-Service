// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.SchemaTools.Cdc;
using FakeItEasy;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[NonParallelizable]
internal partial class Given_Cdc_command_managed_start
{
    [TestCase("missing-state", "cdc-provenance-rejection")]
    [TestCase("source-mismatch", "cdc-provenance-rejection")]
    [TestCase("terminal", "cdc-intact-restart")]
    public async Task It_Cdc_runbook_rejects_unsupported_recovery_using_original_controller_evidence(
        string defect,
        string snippet
    )
    {
        using var isolated = new CdcRunbookEnvironment();
        if (defect == "missing-state")
        {
            // Controlled fixture fault, never an operator recovery step.
            Directory.Delete(Path.Combine(_root, "workflows"), true);
        }
        if (defect == "source-mismatch")
        {
            _wrongSource = true;
        }
        if (defect == "terminal")
        {
            LoseHistory("offset");
            (await CommandAsync()).Succeeded.Should().BeFalse();
            (await _bindings.ExactMatchBindingAsync(_request.Binding))
                .State!.State.Should()
                .Be(CdcBindingState.IncidentLatched);
            _offsetState = CdcConnectOffsetState.Streaming;
            _trace.Clear();
            Fake.ClearRecordedCalls(_connect);
        }
        var result = await CommandAsync(snippetId: snippet);
        result.Succeeded.Should().BeFalse();
        result.ExitCode.Should().Be(1);
        _trace
            .Should()
            .NotContain("start")
            .And.NotContain("resume")
            .And.NotContain("broker-start")
            .And.NotContain("worker-start");
        A.CallTo(() => _connect.RestartAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        if (defect == "source-mismatch")
        {
            CdcRunbookExamples.AssertExcerpt(
                "cdc-output-source-mismatch",
                JsonSerializer.Serialize(result, CdcCommandHost.JsonOptions),
                "operation",
                "succeeded",
                "exitCode",
                "diagnostics/0/component",
                "diagnostics/0/failure"
            );
        }
        if (defect == "terminal")
        {
            (await _bindings.ExactMatchBindingAsync(_request.Binding))
                .State!.State.Should()
                .Be(CdcBindingState.IncidentLatched);
        }
        else
        {
            result
                .Diagnostics.Select(d => (d.Component, d.Failure))
                .Should()
                .Equal(
                    (
                        defect == "missing-state"
                            ? CdcDeploymentComponent.WorkflowState
                            : CdcDeploymentComponent.Projection,
                        defect == "missing-state"
                            ? CdcDeploymentFailure.Unavailable
                            : CdcDeploymentFailure.ValidationFailed
                    )
                );
        }
    }
}
