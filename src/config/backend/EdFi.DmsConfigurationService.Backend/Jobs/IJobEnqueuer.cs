// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Enqueues a job after validating it (spec D-12, D-13, D-18). This, not <see cref="IJobRepository"/>, is what
/// consumers call.
/// </summary>
public interface IJobEnqueuer
{
    /// <summary>
    /// Validates <paramref name="command"/> and, when it passes, enqueues it. With a <paramref name="transaction"/>, the
    /// job commits or rolls back with the caller's writes.
    /// </summary>
    Task<JobEnqueueResult> EnqueueAsync(
        JobEnqueueCommand command,
        DbTransaction? transaction,
        CancellationToken cancellationToken
    );
}
