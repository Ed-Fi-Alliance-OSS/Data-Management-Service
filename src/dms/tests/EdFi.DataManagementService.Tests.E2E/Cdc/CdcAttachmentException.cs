// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

internal enum CdcAttachmentBoundary
{
    None,
    Handoff,
    ProvenanceOrHttpConfiguration,
    RetainedBinding,
    HttpEndpoints,
    Provider,
    KafkaAdvertisedEndpoints,
    Connect,
    Metrics,
    RuntimeIdentitySchema,
    ApiAuthentication,
}

/// <summary>Only fixed values cross the attachment boundary; never retain the original exception.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Sonar",
    "S3871",
    Justification = "Private fixture-to-runner contract; no public exception API is needed."
)]
internal sealed class CdcAttachmentException : Exception
{
    public CdcAttachmentBoundary Boundary { get; }
    public CdcScenarioFailure Failure { get; }

    public CdcAttachmentException(CdcAttachmentBoundary boundary, Exception cause)
        : base("CDC_API_ATTACHMENT_FAILED")
    {
        Boundary = Enum.IsDefined(boundary) ? boundary : CdcAttachmentBoundary.None;
        Failure = cause switch
        {
            OperationCanceledException => CdcScenarioFailure.Cancelled,
            TimeoutException => CdcScenarioFailure.TimedOut,
            _ => CdcScenarioFailure.Error,
        };
    }
}
