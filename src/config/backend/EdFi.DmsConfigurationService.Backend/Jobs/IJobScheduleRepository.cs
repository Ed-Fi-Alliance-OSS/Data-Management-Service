// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Recurring schedules and their materialization into jobs (spec D-8, D-9, D-10). Steps 2.9 and 2.10
/// implement it.
/// </summary>
public interface IJobScheduleRepository
{
    Task<JobScheduleUpsertResult> Upsert(
        JobScheduleUpsertCommand command,
        CancellationToken cancellationToken
    );

    /// <summary>Disables the current tenant's schedule of <paramref name="scheduleType"/>.</summary>
    Task<JobScheduleDisableResult> Disable(string scheduleType, CancellationToken cancellationToken);

    Task<JobScheduleDisableResult> DisableById(long id, CancellationToken cancellationToken);

    /// <summary>Every tenant's schedule of <paramref name="scheduleType"/>, for internal use.</summary>
    Task<JobScheduleListResult> ListByType(string scheduleType, CancellationToken cancellationToken);

    /// <summary>
    /// Materializes the next due occurrence of one schedule in a single bounded transaction: it leases the
    /// schedule, inserts the occurrence as job <paramref name="newJobId"/>, and advances the schedule.
    /// </summary>
    Task<JobScheduleMaterializeResult> MaterializeNextDue(
        string owner,
        int leaseSeconds,
        string newJobId,
        CancellationToken cancellationToken
    );
}

public record JobScheduleUpsertResult
{
    public record Success(long ScheduleId) : JobScheduleUpsertResult;

    public record FailureUnknown(JobFailureDiagnostic Diagnostic) : JobScheduleUpsertResult;
}

public record JobScheduleDisableResult
{
    public record Success() : JobScheduleDisableResult;

    public record FailureNotFound() : JobScheduleDisableResult;

    public record FailureUnknown(JobFailureDiagnostic Diagnostic) : JobScheduleDisableResult;
}

/// <summary>One schedule as listed by <see cref="IJobScheduleRepository.ListByType"/>.</summary>
public sealed record JobScheduleSummary(
    long Id,
    long? TenantId,
    bool Enabled,
    int IntervalMinutes,
    DateTime NextRunAt
);

public record JobScheduleListResult
{
    public record Success(IReadOnlyList<JobScheduleSummary> Schedules) : JobScheduleListResult;

    public record FailureUnknown(JobFailureDiagnostic Diagnostic) : JobScheduleListResult;
}

public record JobScheduleMaterializeResult
{
    /// <summary>The occurrence was enqueued as <paramref name="JobId"/> and the schedule advanced.</summary>
    public record Materialized(long ScheduleId, string JobId, DateTime Occurrence, DateTime NewNextRunAt)
        : JobScheduleMaterializeResult;

    /// <summary>A job for this occurrence already existed; the schedule still advanced.</summary>
    public record AlreadyEnqueued(long ScheduleId, DateTime Occurrence) : JobScheduleMaterializeResult;

    public record NoneDue() : JobScheduleMaterializeResult;

    /// <summary>The advancing UPDATE matched no row; the transaction rolled back.</summary>
    public record OwnershipLost() : JobScheduleMaterializeResult;

    public record FailureUnknown(JobFailureDiagnostic Diagnostic) : JobScheduleMaterializeResult;
}
