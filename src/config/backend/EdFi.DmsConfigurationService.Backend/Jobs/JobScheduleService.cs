// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Checks a schedule's job with <see cref="JobCommandValidator"/> and upserts only what passes, with the serializer's
/// form of the payload (spec D-10). The schedule type and interval are code-supplied, so a malformed one is a
/// programming error and throws before anything is checked or written.
/// </summary>
public sealed class JobScheduleService(JobCommandValidator validator, IJobScheduleRepository repository)
    : IJobScheduleService
{
    /// <summary>The largest interval <c>CK_JobSchedule_IntervalMinutes</c> accepts: 366 days.</summary>
    public const int MaxIntervalMinutes = 527_040;

    public async Task<JobScheduleServiceResult> UpsertAsync(
        JobScheduleUpsertCommand command,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!JobKeySyntax.IsValid(command.ScheduleType))
        {
            throw new ArgumentException(
                "The schedule type must be an ASCII letter followed by ASCII letters, digits, '_', '.', or '-'.",
                nameof(command)
            );
        }

        if (command.IntervalMinutes is < 1 or > MaxIntervalMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                $"The schedule interval must be between 1 and {MaxIntervalMinutes} minutes."
            );
        }

        return validator.Check(command.JobType, command.PayloadVersion, command.PayloadJson) switch
        {
            JobPayloadCheck.Valid valid => await repository.Upsert(
                command with
                {
                    PayloadJson = valid.PayloadJson,
                },
                cancellationToken
            ) switch
            {
                JobScheduleUpsertResult.Success success => new JobScheduleServiceResult.Success(
                    success.ScheduleId
                ),
                JobScheduleUpsertResult.FailureUnknown failure => new JobScheduleServiceResult.FailureUnknown(
                    failure.Diagnostic
                ),
                _ => throw new InvalidOperationException(
                    "The schedule repository returned an unknown result."
                ),
            },
            JobPayloadCheck.UnsupportedType => new JobScheduleServiceResult.FailureUnsupportedType(),
            JobPayloadCheck.UnsupportedVersion => new JobScheduleServiceResult.FailureUnsupportedVersion(),
            JobPayloadCheck.PayloadInvalid invalid => new JobScheduleServiceResult.FailurePayloadInvalid(
                invalid.ReasonCode
            ),
            _ => throw new InvalidOperationException("The job check returned an unknown result."),
        };
    }
}
