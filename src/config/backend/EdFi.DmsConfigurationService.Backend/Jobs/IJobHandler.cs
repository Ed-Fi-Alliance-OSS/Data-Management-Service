// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Runs one job type (spec D-13). Registered with
/// <see cref="JobServiceCollectionExtensions.AddJobHandler{THandler, TPayload, TValidator}"/>; the payload it receives
/// has already passed the payload contract, the strict serializer, and the type's validator.
/// </summary>
public interface IJobHandler<in TPayload>
    where TPayload : class
{
    /// <summary>
    /// Runs the job. Database writes that must happen only while this execution still owns the job go through
    /// <see cref="JobExecutionContext.Fence"/>; external operations stay outside it. Throw
    /// <see cref="JobPermanentException"/> for a failure that retrying cannot fix.
    /// </summary>
    /// <remarks>
    /// Returning normally reports the work as completed. A handler stopping before its work is done must propagate
    /// the cancellation, or call <see cref="CancellationToken.ThrowIfCancellationRequested"/>, rather than return.
    /// When the host has already signaled shutdown, the executor conservatively releases the job even after a
    /// normal return, so the job may run again; handlers are idempotent.
    /// </remarks>
    Task ExecuteAsync(JobExecutionContext context, TPayload payload, CancellationToken cancellationToken);
}
