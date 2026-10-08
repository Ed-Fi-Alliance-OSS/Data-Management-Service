// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Checks a job with <see cref="JobCommandValidator"/> and enqueues only what passes, with the serializer's form of the
/// payload, in the caller's transaction when one is supplied (spec D-18).
/// </summary>
public sealed class JobEnqueuer(JobCommandValidator validator, IJobRepository repository) : IJobEnqueuer
{
    public async Task<JobEnqueueResult> EnqueueAsync(
        JobEnqueueCommand command,
        DbTransaction? transaction,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(command);

        return validator.Check(command.JobType, command.PayloadVersion, command.PayloadJson) switch
        {
            JobPayloadCheck.Valid valid => await repository.EnqueueJob(
                command with
                {
                    PayloadJson = valid.PayloadJson,
                },
                transaction,
                cancellationToken
            ),
            JobPayloadCheck.UnsupportedType => new JobEnqueueResult.FailureUnsupportedType(),
            JobPayloadCheck.UnsupportedVersion => new JobEnqueueResult.FailureUnsupportedVersion(),
            JobPayloadCheck.PayloadInvalid invalid => new JobEnqueueResult.FailurePayloadInvalid(
                invalid.ReasonCode
            ),
            _ => throw new InvalidOperationException("The job check returned an unknown result."),
        };
    }
}
