// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Runs a consumer's database work atomically with a proof of ownership (spec D-5): the job row stays locked
/// and the lease is revalidated with fresh database time before the work commits.
/// </summary>
public interface IJobFence
{
    /// <summary>
    /// Runs <paramref name="work"/> inside a transaction that holds the job row lock, under the execution's
    /// ownership gate, and commits only while ownership is still certain. Throws
    /// <see cref="JobLeaseLostException"/> when ownership is lost or uncertain, and
    /// <see cref="JobFenceUnavailableException"/> when the row lock cannot be acquired in time.
    /// </summary>
    Task ExecuteAsync(Func<DbTransaction, CancellationToken, Task> work, CancellationToken cancellationToken);
}
