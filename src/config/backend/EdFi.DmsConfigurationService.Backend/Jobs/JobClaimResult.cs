// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

public record JobClaimResult
{
    public record Claimed(ClaimedJob Job) : JobClaimResult;

    /// <summary>No job was eligible, or every eligible row was locked by another claimer.</summary>
    public record NoneAvailable() : JobClaimResult;

    public record FailureUnknown(JobFailureDiagnostic Diagnostic) : JobClaimResult;
}
