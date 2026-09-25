// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.Metrics;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The job runtime's metrics (spec D-16, §6.4), on the meter <see cref="MeterName"/>. No exporter is configured here;
/// a host that wants them adds its own. Tag values that come from rows pass through
/// <see cref="JobDiagnostics.SafeIdentifier"/>.
/// </summary>
public sealed class JobMetrics : IDisposable
{
    public const string MeterName = "EdFi.DmsConfigurationService.Jobs";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _claimed;
    private readonly Counter<long> _finished;
    private readonly Counter<long> _ownershipUncertain;
    private readonly Histogram<double> _queueDelay;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _occurrencesEnqueued;
    private readonly Counter<long> _retentionDeleted;

    public JobMetrics()
    {
        _claimed = _meter.CreateCounter<long>(
            "dmscs.jobs.claimed",
            description: "Jobs claimed for execution."
        );
        _finished = _meter.CreateCounter<long>(
            "dmscs.jobs.finished",
            description: "Executions that ended, by outcome."
        );
        _ownershipUncertain = _meter.CreateCounter<long>(
            "dmscs.jobs.ownership_uncertain",
            description: "Executions that lost certainty about their ownership, by reason."
        );
        _queueDelay = _meter.CreateHistogram<double>(
            "dmscs.jobs.queue_delay",
            unit: "ms",
            description: "Database time from a job becoming eligible to its claim."
        );
        _duration = _meter.CreateHistogram<double>(
            "dmscs.jobs.duration",
            unit: "ms",
            description: "Wall-clock time of one execution."
        );
        _occurrencesEnqueued = _meter.CreateCounter<long>(
            "dmscs.schedules.occurrences_enqueued",
            description: "Schedule occurrences enqueued as jobs."
        );
        _retentionDeleted = _meter.CreateCounter<long>(
            "dmscs.jobs.retention_deleted",
            description: "Finished jobs deleted by retention."
        );
    }

    public void JobClaimed(string jobType, bool reclaimed, double queueDelayMilliseconds)
    {
        _claimed.Add(1, JobType(jobType), new KeyValuePair<string, object?>("reclaimed", reclaimed));
        _queueDelay.Record(queueDelayMilliseconds, JobType(jobType));
    }

    public void JobFinished(string jobType, JobExecutionOutcome outcome, double durationMilliseconds)
    {
        _finished.Add(1, JobType(jobType), new KeyValuePair<string, object?>("outcome", outcome.ToString()));
        _duration.Record(durationMilliseconds, JobType(jobType));
    }

    public void OwnershipUncertain(string jobType, string reason) =>
        _ownershipUncertain.Add(
            1,
            JobType(jobType),
            new KeyValuePair<string, object?>("reason", JobDiagnostics.SafeIdentifier(reason))
        );

    public void OccurrenceEnqueued(string scheduleType) =>
        _occurrencesEnqueued.Add(
            1,
            new KeyValuePair<string, object?>("schedule_type", JobDiagnostics.SafeIdentifier(scheduleType))
        );

    public void RetentionDeleted(int count) => _retentionDeleted.Add(count);

    public void Dispose() => _meter.Dispose();

    private static KeyValuePair<string, object?> JobType(string jobType) =>
        new("job_type", JobDiagnostics.SafeIdentifier(jobType));
}
