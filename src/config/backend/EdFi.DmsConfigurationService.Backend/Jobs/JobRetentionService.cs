// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The retention hosted service (spec D-17, step 3.6). It sweeps when it starts and then every
/// <see cref="JobOptions.RetentionInterval"/>: each sweep deletes finished jobs older than
/// <see cref="JobOptions.FinishedJobRetention"/> in batches of <see cref="JobOptions.RetentionBatchSize"/>, until a
/// batch comes back short. A failed batch ends the sweep; the next interval tries again. Failures are logged by their
/// safe diagnostic fields and never fault the host.
/// </summary>
public sealed class JobRetentionService(
    IJobRetentionRepository repository,
    JobMetrics metrics,
    IOptions<JobOptions> options,
    TimeProvider timeProvider,
    ILogger<JobRetentionService> logger
) : BackgroundService
{
    internal static readonly EventId RetentionDeleted = new(1437_30, nameof(RetentionDeleted));
    internal static readonly EventId RetentionFailed = new(1437_31, nameof(RetentionFailed));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        JobOptions settings = options.Value;
        using PeriodicTimer timer = new(settings.RetentionInterval, timeProvider);
        try
        {
            do
            {
                await SweepAsync(settings, stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: the sweep in progress, if any, was a batch that has already committed or failed on its own.
        }
    }

    private async Task SweepAsync(JobOptions settings, CancellationToken stoppingToken)
    {
        // Rounded up: a fractional retention must never let a job be deleted before its full window has passed.
        int retentionSeconds = (int)Math.Ceiling(settings.FinishedJobRetention.TotalSeconds);
        int deleted = 0;

        // Only the type chain is kept; the exception object is never logged (D-16a).
        string? failedWith = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                JobRetentionResult result = await repository.DeleteFinishedOlderThan(
                    retentionSeconds,
                    settings.RetentionBatchSize,
                    stoppingToken
                );
                if (result is JobRetentionResult.FailureUnknown failure)
                {
                    LogFailure(
                        deleted,
                        failure.Diagnostic.ExceptionTypeChain,
                        failure.Diagnostic.ProviderErrorCode,
                        failure.Diagnostic.Operation
                    );
                    break;
                }

                int batch = ((JobRetentionResult.Success)result).DeletedCount;
                deleted += batch;
                metrics.RetentionDeleted(batch);
                if (batch < settings.RetentionBatchSize)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failedWith = JobDiagnostics.TypeChain(exception);
        }

        if (failedWith is not null)
        {
            LogFailure(deleted, failedWith, null, "DeleteFinishedOlderThan");
        }

        if (deleted > 0)
        {
            logger.LogInformation(
                RetentionDeleted,
                "{Event} Count={Count} RetentionSeconds={RetentionSeconds}",
                nameof(RetentionDeleted),
                deleted,
                retentionSeconds
            );
        }
    }

    private void LogFailure(
        int deletedBeforeFailure,
        string? exceptionTypeChain,
        string? providerErrorCode,
        string operation
    ) =>
        logger.LogWarning(
            RetentionFailed,
            "{Event} DeletedBeforeFailure={DeletedBeforeFailure} ExceptionTypeChain={ExceptionTypeChain} ProviderErrorCode={ProviderErrorCode} Operation={Operation}",
            nameof(RetentionFailed),
            deletedBeforeFailure,
            exceptionTypeChain,
            providerErrorCode,
            operation
        );
}
