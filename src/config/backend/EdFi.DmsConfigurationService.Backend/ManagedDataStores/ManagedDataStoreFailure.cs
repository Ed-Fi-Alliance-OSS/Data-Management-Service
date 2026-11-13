// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Jobs;

namespace EdFi.DmsConfigurationService.Backend.ManagedDataStores;

/// <summary>Bounded retained failure evidence: existing taxonomy codes only, never messages or diagnostics.</summary>
public sealed record ManagedDataStoreFailure
{
    private ManagedDataStoreFailure(
        DmsProvisioningFailureCategory category,
        DmsProvisioningFailureCode? provisioningCode,
        string? jobCode
    )
    {
        Category = category;
        ProvisioningCode = provisioningCode;
        JobCode = jobCode;
    }

    public DmsProvisioningFailureCategory Category { get; }
    public DmsProvisioningFailureCode? ProvisioningCode { get; }
    public string? JobCode { get; }

    public static ManagedDataStoreFailure FromProvisioner(DmsProvisioningFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(failure.Category, failure.Code, null);
    }

    public static ManagedDataStoreFailure FromJob(JobErrorCode error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!JobErrorCode.Infrastructure.Any(known => known.Code == error.Code))
        {
            throw new ArgumentException("Unknown managed job infrastructure failure code.", nameof(error));
        }
        return new(
            error.Code == JobErrorCode.AttemptsExhausted.Code
                ? DmsProvisioningFailureCategory.OperatorInspectionRequired
                : DmsProvisioningFailureCategory.Permanent,
            null,
            error.Code
        );
    }

    public static ManagedDataStoreFailure RetryableJobFailure() =>
        new(DmsProvisioningFailureCategory.Transient, null, JobErrorCode.HandlerFailed.Code);
}
