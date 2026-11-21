// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;

/// <summary>
/// Keeps one enabled refresh schedule per tenant: at startup, then every <see cref="Period"/>, it creates or updates
/// each tenant's <see cref="EducationOrganizationRefreshJobTypes.ScheduledRefresh"/> schedule, which enqueues a refresh
/// of every data store at <see cref="EducationOrganizationRefreshOptions.EdOrgsRefreshIntervalInMins"/>, and disables the
/// schedules that no longer belong to a tenant of this tenancy mode. It runs on every replica whose scheduler is on,
/// whatever the data store management setting is.
/// </summary>
/// <remarks>
/// <para>
/// Upserts are keyed by tenant and schedule type, so every replica converges on the same rows. Each runs in a scope
/// whose tenant context is that tenant's, installed before the schedule service is resolved.
/// </para>
/// <para>
/// Disabling is decided per schedule, never from the enumeration: in multi-tenant mode a schedule without a tenant is
/// disabled, and a tenant's schedule is disabled only when a fresh lookup finds the tenant gone. A lookup failure keeps
/// it. In single-tenant mode every tenant's schedule is disabled. A run whose tenant enumeration or schedule listing
/// fails disables nothing. Disabling keeps the row and its jobs.
/// </para>
/// <para>
/// A failure is logged by its type chain and fixed diagnostic fields only, and retried at the next run; it never stops
/// the host or the dispatch of existing schedules.
/// </para>
/// </remarks>
public sealed class EducationOrganizationRefreshScheduleReconciler(
    IServiceScopeFactory scopeFactory,
    IOptions<EducationOrganizationRefreshOptions> refreshOptions,
    JobRuntimeEnvironment environment,
    TimeProvider timeProvider,
    ILogger<EducationOrganizationRefreshScheduleReconciler> logger
) : BackgroundService
{
    /// <summary>The time between the end of one run and the start of the next.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(5);

    internal const int TenantPageSize = 100;

    internal static readonly EventId ScheduleReconciled = new(
        1441_09,
        "EducationOrganizationScheduleReconciled"
    );

    internal static readonly EventId ScheduleReconciliationFailed = new(
        1441_10,
        "EducationOrganizationScheduleReconciliationFailed"
    );

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogFailure("Reconcile", JobDiagnostics.TypeChain(exception), null, null);
            }

            try
            {
                await Task.Delay(Period, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>One run: every upsert, then, when every tenant was enumerated, every disable decision.</summary>
    internal async Task<ReconciliationSummary> ReconcileAsync(CancellationToken cancellationToken)
    {
        ReconciliationSummary summary = new();
        JobScheduleUpsertCommand command = UpsertCommand();

        bool enumerated = environment.MultiTenancy
            ? await UpsertEveryTenantAsync(command, summary, cancellationToken)
            : await UpsertAsync(
                command,
                new TenantContext.NotMultitenant(),
                null,
                summary,
                cancellationToken
            );
        if (environment.MultiTenancy && !enumerated)
        {
            summary.DisablePhaseSkipped = true;
        }
        else
        {
            await DisableAsync(summary, cancellationToken);
        }

        logger.LogInformation(
            ScheduleReconciled,
            "{Event} MultiTenancy={MultiTenancy} Upserted={Upserted} UpsertFailed={UpsertFailed} Disabled={Disabled} DisableFailed={DisableFailed} Kept={Kept} DisablePhaseSkipped={DisablePhaseSkipped}",
            ScheduleReconciled.Name,
            environment.MultiTenancy,
            summary.Upserted,
            summary.UpsertFailed,
            summary.Disabled,
            summary.DisableFailed,
            summary.Kept,
            summary.DisablePhaseSkipped
        );
        return summary;
    }

    private JobScheduleUpsertCommand UpsertCommand()
    {
        string payload = JobPayloadSerializer.Serialize(
            new EducationOrganizationRefreshPayload(EducationOrganizationRefreshScope.All, 0)
        ) switch
        {
            JobPayloadWriteResult.Success written => written.Json,
            _ => throw new InvalidOperationException("The scheduled refresh payload could not be written."),
        };
        return new JobScheduleUpsertCommand(
            EducationOrganizationRefreshJobTypes.ScheduledRefresh,
            EducationOrganizationRefreshJobTypes.Refresh,
            EducationOrganizationRefreshJobTypes.PayloadVersion,
            payload,
            refreshOptions.Value.EdOrgsRefreshIntervalInMins,
            RunFirstOccurrenceImmediately: true
        );
    }

    /// <summary>
    /// Pages through every tenant in identifier order and upserts each one's schedule. Returns false when a page could
    /// not be read; the upserts made before it stand.
    /// </summary>
    private async Task<bool> UpsertEveryTenantAsync(
        JobScheduleUpsertCommand command,
        ReconciliationSummary summary,
        CancellationToken cancellationToken
    )
    {
        int offset = 0;
        bool more = true;
        while (more)
        {
            List<TenantResponse>? page = await ReadTenantPageAsync(offset, cancellationToken);
            if (page is null)
            {
                return false;
            }
            foreach (TenantResponse tenant in page)
            {
                await UpsertAsync(
                    command,
                    new TenantContext.Multitenant(tenant.Id, tenant.Name),
                    tenant.Id,
                    summary,
                    cancellationToken
                );
            }
            more = page.Count == TenantPageSize;
            offset += TenantPageSize;
        }
        return true;
    }

    private async Task<List<TenantResponse>?> ReadTenantPageAsync(
        int offset,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            TenantQueryResult result = await scope
                .ServiceProvider.GetRequiredService<ITenantRepository>()
                .QueryTenant(new PagingQuery { Offset = offset, Limit = TenantPageSize });
            if (result is TenantQueryResult.Success success)
            {
                return [.. success.TenantResponses];
            }
            // The failure's message may carry exception text, so only its kind is logged.
            LogFailure("QueryTenant", null, null, null);
            return null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailure("QueryTenant", JobDiagnostics.TypeChain(exception), null, null);
            return null;
        }
    }

    /// <summary>Upserts one tenant's schedule in a scope that carries the tenant. Returns whether it succeeded.</summary>
    private async Task<bool> UpsertAsync(
        JobScheduleUpsertCommand command,
        TenantContext tenant,
        long? tenantId,
        ReconciliationSummary summary,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextProvider>().Context = tenant;
            JobScheduleServiceResult result = await scope
                .ServiceProvider.GetRequiredService<IJobScheduleService>()
                .UpsertAsync(command, cancellationToken);
            switch (result)
            {
                case JobScheduleServiceResult.Success:
                    summary.Upserted++;
                    return true;
                case JobScheduleServiceResult.FailureUnknown failure:
                    LogFailure(
                        "UpsertSchedule",
                        failure.Diagnostic.ExceptionTypeChain,
                        failure.Diagnostic.ProviderErrorCode,
                        tenantId
                    );
                    break;
                default:
                    LogFailure("UpsertSchedule" + result.GetType().Name, null, null, tenantId);
                    break;
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailure("UpsertSchedule", JobDiagnostics.TypeChain(exception), null, tenantId);
        }
        summary.UpsertFailed++;
        return false;
    }

    /// <summary>Decides each enabled schedule of the type on its own, by the rules of this tenancy mode.</summary>
    private async Task DisableAsync(ReconciliationSummary summary, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IJobScheduleRepository schedules = scope.ServiceProvider.GetRequiredService<IJobScheduleRepository>();
        IReadOnlyList<JobScheduleSummary> listed;
        try
        {
            JobScheduleListResult result = await schedules.ListByType(
                EducationOrganizationRefreshJobTypes.ScheduledRefresh,
                cancellationToken
            );
            if (result is not JobScheduleListResult.Success success)
            {
                JobFailureDiagnostic? diagnostic = (
                    result as JobScheduleListResult.FailureUnknown
                )?.Diagnostic;
                LogFailure(
                    "ListSchedules",
                    diagnostic?.ExceptionTypeChain,
                    diagnostic?.ProviderErrorCode,
                    null
                );
                summary.DisablePhaseSkipped = true;
                return;
            }
            listed = success.Schedules;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailure("ListSchedules", JobDiagnostics.TypeChain(exception), null, null);
            summary.DisablePhaseSkipped = true;
            return;
        }

        foreach (JobScheduleSummary schedule in listed.Where(schedule => schedule.Enabled))
        {
            if (await ShouldDisableAsync(scope.ServiceProvider, schedule, cancellationToken))
            {
                await DisableByIdAsync(schedules, schedule, summary, cancellationToken);
            }
            else
            {
                summary.Kept++;
            }
        }
    }

    private async Task<bool> ShouldDisableAsync(
        IServiceProvider services,
        JobScheduleSummary schedule,
        CancellationToken cancellationToken
    )
    {
        if (!environment.MultiTenancy)
        {
            return schedule.TenantId is not null;
        }
        if (schedule.TenantId is not long tenantId)
        {
            return true;
        }
        // A fresh lookup, so a tenant created after this run's enumeration keeps the schedule another replica gave it.
        try
        {
            switch (await services.GetRequiredService<ITenantRepository>().GetTenant(tenantId))
            {
                case TenantGetResult.FailureNotFound:
                    return true;
                case TenantGetResult.Success:
                    return false;
                default:
                    LogFailure("GetTenant", null, null, tenantId);
                    return false;
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailure("GetTenant", JobDiagnostics.TypeChain(exception), null, tenantId);
            return false;
        }
    }

    private async Task DisableByIdAsync(
        IJobScheduleRepository schedules,
        JobScheduleSummary schedule,
        ReconciliationSummary summary,
        CancellationToken cancellationToken
    )
    {
        try
        {
            switch (await schedules.DisableById(schedule.Id, cancellationToken))
            {
                case JobScheduleDisableResult.Success:
                case JobScheduleDisableResult.FailureNotFound:
                    summary.Disabled++;
                    return;
                case JobScheduleDisableResult.FailureUnknown failure:
                    LogFailure(
                        "DisableSchedule",
                        failure.Diagnostic.ExceptionTypeChain,
                        failure.Diagnostic.ProviderErrorCode,
                        schedule.TenantId
                    );
                    break;
                default:
                    LogFailure("DisableSchedule", null, null, schedule.TenantId);
                    break;
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailure("DisableSchedule", JobDiagnostics.TypeChain(exception), null, schedule.TenantId);
        }
        summary.DisableFailed++;
    }

    private void LogFailure(
        string operation,
        string? exceptionTypeChain,
        string? providerErrorCode,
        long? tenantId
    ) =>
        logger.LogWarning(
            ScheduleReconciliationFailed,
            "{Event} Operation={Operation} TenantId={TenantId} ExceptionTypeChain={ExceptionTypeChain} ProviderErrorCode={ProviderErrorCode}",
            ScheduleReconciliationFailed.Name,
            operation,
            tenantId,
            exceptionTypeChain,
            providerErrorCode
        );
}

/// <summary>What one reconciliation run did.</summary>
internal sealed class ReconciliationSummary
{
    public int Upserted { get; set; }

    public int UpsertFailed { get; set; }

    public int Disabled { get; set; }

    public int DisableFailed { get; set; }

    public int Kept { get; set; }

    /// <summary>True when the tenant enumeration or the schedule listing failed, so nothing was disabled.</summary>
    public bool DisablePhaseSkipped { get; set; }
}
