// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.Job;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

public record JobStatusQueryResult
{
    public record Success(JobStatusResponse Job) : JobStatusQueryResult;

    /// <summary>No job with that identifier exists for the current tenant.</summary>
    public record FailureNotFound() : JobStatusQueryResult;

    public record FailureUnknown(JobFailureDiagnostic Diagnostic) : JobStatusQueryResult;
}
