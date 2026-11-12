// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// Carries a classified <see cref="DmsProvisioningFailure"/> from an orchestration step to the operation
/// boundary, where it becomes the operation's failure result together with the cleanup report. It is
/// control flow between the steps that classify failures (the orchestrator's own steps and the catalog's
/// artifact extraction) and <see cref="DmsDataStoreProvisioningService"/>, which catches it; it never
/// escapes the provisioner. The message is the failure's fixed summary; the inner exception, when present,
/// is the engine or framework exception the step classified, kept for the log, never for the result.
/// </summary>
public sealed class DmsProvisioningFailedException : Exception
{
    public DmsProvisioningFailedException(DmsProvisioningFailure failure, Exception? innerException = null)
        : base(SummaryOf(failure), innerException)
    {
        Failure = failure;
    }

    public DmsProvisioningFailure Failure { get; }

    private static string SummaryOf(DmsProvisioningFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.Summary;
    }
}
