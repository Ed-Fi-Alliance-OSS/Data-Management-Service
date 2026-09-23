// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>Enqueue and status read (spec D-18, §5). Steps 2.3 and 2.4 implement it per provider.</summary>
public interface IJobRepository
{
    /// <summary>
    /// Stores a validated job as <c>Pending</c> with <c>NextAttemptAt = CreatedAt</c>, inside
    /// <paramref name="transaction"/> when one is supplied, otherwise in its own connection.
    /// </summary>
    Task<JobEnqueueResult> EnqueueJob(
        JobEnqueueCommand command,
        DbTransaction? transaction,
        CancellationToken cancellationToken
    );

    /// <summary>The five public fields of a job visible to the current tenant, matched exactly (D-14).</summary>
    Task<JobStatusQueryResult> GetJobStatus(string jobId, CancellationToken cancellationToken);
}
