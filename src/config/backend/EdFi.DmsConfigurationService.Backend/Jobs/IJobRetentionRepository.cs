// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>Deletes finished jobs past the retention window in bounded batches (spec D-17).</summary>
public interface IJobRetentionRepository
{
    /// <summary>
    /// Deletes at most <paramref name="batchSize"/> <c>Completed</c> or <c>Error</c> jobs whose
    /// <c>FinishedAt</c> is at least <paramref name="retentionSeconds"/> before database now.
    /// </summary>
    Task<JobRetentionResult> DeleteFinishedOlderThan(
        int retentionSeconds,
        int batchSize,
        CancellationToken cancellationToken
    );
}

public record JobRetentionResult
{
    public record Success(int DeletedCount) : JobRetentionResult;

    public record FailureUnknown(JobFailureDiagnostic Diagnostic) : JobRetentionResult;
}
