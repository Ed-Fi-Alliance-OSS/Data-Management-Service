// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("ExitCode")]
public sealed class Given_DocumentCacheAdminCdcExitCodes
{
    private static readonly CdcTargetIdentity Target = new(
        "deployment",
        "",
        "1",
        "instance",
        1,
        CdcProvider.Postgresql
    );

    [TestCase(CdcAdmissionState.Admitted, DocumentCacheAdminExitCodes.Success)]
    [TestCase(CdcAdmissionState.NotAdmitted, DocumentCacheAdminExitCodes.IncompleteRetryable)]
    [TestCase(CdcAdmissionState.Unknown, DocumentCacheAdminExitCodes.IncompleteRetryable)]
    public void It_maps_every_admission_state(CdcAdmissionState admissionState, int expectedExitCode)
    {
        DocumentCacheAdminExitCodeMapper.ForAdmissionState(admissionState).Should().Be(expectedExitCode);
    }

    [Test]
    public void It_maps_every_admission_state_enum_value()
    {
        foreach (CdcAdmissionState admissionState in Enum.GetValues<CdcAdmissionState>())
        {
            DocumentCacheAdminExitCodeMapper
                .ForAdmissionState(admissionState)
                .Should()
                .NotBe(DocumentCacheAdminExitCodes.UnexpectedFailure, admissionState.ToString());
        }
    }

    [Test]
    public void It_maps_an_undefined_admission_state_to_the_unexpected_failure_code()
    {
        DocumentCacheAdminExitCodeMapper
            .ForAdmissionState((CdcAdmissionState)int.MaxValue)
            .Should()
            .Be(DocumentCacheAdminExitCodes.UnexpectedFailure);
    }

    [Test]
    public void It_maps_an_admission_contract_from_its_state()
    {
        DocumentCacheAdminExitCodeMapper
            .ForAdmission(Admission(CdcAdmissionState.NotAdmitted))
            .Should()
            .Be(DocumentCacheAdminExitCodes.IncompleteRetryable);
        DocumentCacheAdminExitCodeMapper
            .ForAdmission(Admission(CdcAdmissionState.Admitted))
            .Should()
            .Be(DocumentCacheAdminExitCodes.Success);
    }

    /// <summary>
    /// A status read answered even when it reports a binding that is not ready, so readiness never
    /// becomes an exit code of its own: the shared contract carries the verdict.
    /// </summary>
    [TestCase(CdcReadiness.Ready, DocumentCacheAdminExitCodes.Success)]
    [TestCase(CdcReadiness.NotReady, DocumentCacheAdminExitCodes.Success)]
    [TestCase(CdcReadiness.Unknown, DocumentCacheAdminExitCodes.Success)]
    public void It_maps_every_readiness_value(CdcReadiness readiness, int expectedExitCode)
    {
        DocumentCacheAdminExitCodeMapper.ForReadiness(readiness).Should().Be(expectedExitCode);
    }

    [Test]
    public void It_maps_every_readiness_enum_value()
    {
        foreach (CdcReadiness readiness in Enum.GetValues<CdcReadiness>())
        {
            DocumentCacheAdminExitCodeMapper
                .ForReadiness(readiness)
                .Should()
                .NotBe(DocumentCacheAdminExitCodes.UnexpectedFailure, readiness.ToString());
        }
    }

    [Test]
    public void It_maps_an_undefined_readiness_to_the_unexpected_failure_code()
    {
        DocumentCacheAdminExitCodeMapper
            .ForReadiness((CdcReadiness)int.MaxValue)
            .Should()
            .Be(DocumentCacheAdminExitCodes.UnexpectedFailure);
    }

    [Test]
    public void It_maps_a_status_contract_from_its_readiness()
    {
        DocumentCacheAdminExitCodeMapper
            .ForStatus(Status(CdcReadiness.NotReady))
            .Should()
            .Be(DocumentCacheAdminExitCodes.Success);
    }

    [TestCase(CdcControlPlaneOperationStatus.Succeeded, DocumentCacheAdminExitCodes.Success)]
    [TestCase(CdcControlPlaneOperationStatus.BindingMissing, DocumentCacheAdminExitCodes.RejectedNoMutation)]
    [TestCase(CdcControlPlaneOperationStatus.BindingMismatch, DocumentCacheAdminExitCodes.RejectedNoMutation)]
    [TestCase(
        CdcControlPlaneOperationStatus.InvalidOperation,
        DocumentCacheAdminExitCodes.RejectedNoMutation
    )]
    [TestCase(
        CdcControlPlaneOperationStatus.StateStoreUnavailable,
        DocumentCacheAdminExitCodes.FailedNoMutation
    )]
    public void It_maps_every_control_plane_operation_status(
        CdcControlPlaneOperationStatus status,
        int expectedExitCode
    )
    {
        DocumentCacheAdminExitCodeMapper.ForControlPlaneOperationStatus(status).Should().Be(expectedExitCode);
    }

    [Test]
    public void It_maps_every_control_plane_operation_status_enum_value()
    {
        foreach (CdcControlPlaneOperationStatus status in Enum.GetValues<CdcControlPlaneOperationStatus>())
        {
            DocumentCacheAdminExitCodeMapper
                .ForControlPlaneOperationStatus(status)
                .Should()
                .NotBe(DocumentCacheAdminExitCodes.UnexpectedFailure, status.ToString());
        }
    }

    [Test]
    public void It_maps_an_undefined_control_plane_operation_status_to_the_unexpected_failure_code()
    {
        DocumentCacheAdminExitCodeMapper
            .ForControlPlaneOperationStatus((CdcControlPlaneOperationStatus)int.MaxValue)
            .Should()
            .Be(DocumentCacheAdminExitCodes.UnexpectedFailure);
    }

    /// <summary>
    /// An unadmitted enablement may already have made the binding durable and created governed
    /// artifacts, so it must never report the rejected-before-mutation code.
    /// </summary>
    [Test]
    public void It_never_reports_an_unadmitted_enablement_as_rejected_before_mutation()
    {
        DocumentCacheAdminExitCodeMapper
            .ForAdmissionState(CdcAdmissionState.NotAdmitted)
            .Should()
            .NotBe(DocumentCacheAdminExitCodes.RejectedNoMutation);
        DocumentCacheAdminExitCodeMapper
            .ForAdmissionState(CdcAdmissionState.Unknown)
            .Should()
            .NotBe(DocumentCacheAdminExitCodes.RejectedNoMutation);
    }

    private static CdcAdmission Admission(CdcAdmissionState admissionState) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            "operation-1",
            DateTimeOffset.UnixEpoch,
            Target,
            admissionState,
            CdcBlockingCategory.None,
            Steps(),
            []
        );

    private static CdcAdmissionSteps Steps()
    {
        CdcComponent component = new(
            CdcComponentState.Satisfied,
            CdcBlockingCategory.None,
            DateTimeOffset.UnixEpoch,
            null
        );

        return new(
            component,
            component,
            component,
            component,
            component,
            component,
            component,
            component,
            component
        );
    }

    private static CdcStatus Status(CdcReadiness readiness) =>
        new(
            CdcJsonContract.CurrentContractVersion,
            DateTimeOffset.UnixEpoch,
            readiness,
            CdcBlockingCategory.None,
            []
        );
}
