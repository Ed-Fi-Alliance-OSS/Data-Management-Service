// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
public class Given_CdcConnectorLifecycleObservation
{
    private CdcBinding _binding = null!;
    private CdcConnectorRuntimeObservation _runtime = null!;
    private CdcObservationValidationContext _context = null!;

    [SetUp]
    public void Setup()
    {
        _binding = CdcTargetStatusFixture.CreateBinding();
        _runtime = CdcTargetStatusFixture.ConnectorRuntime(_binding);
        _context = new(
            _runtime.OperationId,
            _binding.ToTargetIdentity(),
            _binding.PhysicalSourceFingerprint,
            _runtime.ObservedAt
        );
    }

    [TestCase(CdcConnectorRuntimeState.Unknown)]
    [TestCase(CdcConnectorRuntimeState.Stopped)]
    public void It_accepts_verified_shutdown_only_for_lifecycle_validation(CdcConnectorRuntimeState soleTask)
    {
        _runtime = _runtime with
        {
            ConnectorState = CdcConnectorRuntimeState.Stopped,
            TaskCount = 0,
            RunningTaskCount = 0,
            SoleTaskState = soleTask,
        };
        CdcConnectorRuntimeObservationValidator
            .ValidateForLifecycle(_runtime, _binding, _context)
            .Succeeded.Should()
            .BeTrue();
        CdcConnectorRuntimeObservationValidator
            .ValidateForBinding(_runtime, _binding, _context)
            .Succeeded.Should()
            .BeFalse();
    }

    [Test]
    public void It_accepts_a_running_connector_with_a_failed_task_only_for_lifecycle_validation()
    {
        _runtime = _runtime with
        {
            SoleTaskState = CdcConnectorRuntimeState.Failed,
            RunningTaskCount = 0,
            LastErrorCategory = "connect-runtime-failed",
        };
        CdcConnectorRuntimeObservationValidator
            .ValidateForLifecycle(_runtime, _binding, _context)
            .Succeeded.Should()
            .BeTrue();
        CdcConnectorRuntimeObservationValidator
            .ValidateForBinding(_runtime, _binding, _context)
            .Succeeded.Should()
            .BeFalse();
    }

    [TestCase("target")]
    [TestCase("source")]
    [TestCase("connector")]
    [TestCase("future")]
    [TestCase("missingError")]
    [TestCase("multiple")]
    [TestCase("runningCount")]
    [TestCase("stoppedWithTask")]
    [TestCase("unknownCount")]
    public void It_preserves_identity_and_structural_rejection(string scenario)
    {
        _runtime = scenario switch
        {
            "target" => _runtime with { TargetIdentity = _runtime.TargetIdentity with { Generation = 2 } },
            "source" => _runtime with { PhysicalSourceFingerprint = "sha256:" + new string('f', 64) },
            "connector" => _runtime with { ConnectorName = "other" },
            "future" => _runtime with { ObservedAt = _runtime.ObservedAt.AddMinutes(1) },
            "missingError" => _runtime with
            {
                SoleTaskState = CdcConnectorRuntimeState.Failed,
                RunningTaskCount = 0,
                LastErrorCategory = null,
            },
            "multiple" => _runtime with { TaskCount = 2 },
            "runningCount" => _runtime with { RunningTaskCount = 0 },
            "stoppedWithTask" => _runtime with { ConnectorState = CdcConnectorRuntimeState.Stopped },
            _ => _runtime with { TaskCount = null },
        };
        CdcConnectorRuntimeObservationValidator
            .ValidateForLifecycle(_runtime, _binding, _context)
            .Succeeded.Should()
            .BeFalse();
    }
}
