// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Creates or updates a recurring schedule after validating its job (spec D-10). This is the only caller of
/// <see cref="IJobScheduleRepository.Upsert"/>: a schedule's payload is checked as an enqueued job's is, before it is
/// stored, so invalid or secret-bearing JSON never reaches the table.
/// </summary>
public interface IJobScheduleService
{
    Task<JobScheduleServiceResult> UpsertAsync(
        JobScheduleUpsertCommand command,
        CancellationToken cancellationToken
    );
}

public record JobScheduleServiceResult
{
    public record Success(long ScheduleId) : JobScheduleServiceResult;

    public record FailureUnsupportedType() : JobScheduleServiceResult;

    public record FailureUnsupportedVersion() : JobScheduleServiceResult;

    /// <summary>The payload broke its contract; <paramref name="ReasonCode"/> is a fixed code, not input text.</summary>
    public record FailurePayloadInvalid(string ReasonCode) : JobScheduleServiceResult;

    public record FailureUnknown(JobFailureDiagnostic Diagnostic) : JobScheduleServiceResult;
}
