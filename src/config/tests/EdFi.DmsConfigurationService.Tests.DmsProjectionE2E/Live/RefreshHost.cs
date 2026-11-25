// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.Job;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.Live;

/// <summary>What a live refresh case configures on its host.</summary>
public sealed record RefreshHostSettings
{
    public int PageSize { get; init; } = 2;

    /// <summary>Null keeps the reader's default.</summary>
    public int? PageRequestTimeoutSeconds { get; init; }

    public int MaxAttempts { get; init; } = 1;

    public TimeSpan RetryBackoffBase { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Null is the live DMS.</summary>
    public Uri? DmsBaseUrl { get; init; }
}

/// <summary>
/// A Configuration Service job worker over a <see cref="ScratchCmsDatabase"/>, as a deployment with workers on runs
/// one: the provider's job, lease, fence and snapshot repositories, <c>AddEducationOrganizationRefresh</c> with the
/// production reader and the given per-tenant projection credentials, and the job runtime (<c>JobExecutor</c>,
/// <c>JobWorkerService</c> polling every second). The reader's requests pass through <see cref="Plan"/> to the live
/// DMS. Jobs are enqueued and read back through the provider's job repository, under the tenant's context, as the
/// refresh routes and <c>GET /v3/jobs/{jobId}</c> do.
/// </summary>
public sealed class RefreshHost : IAsyncDisposable
{
    private static readonly TimeSpan _terminalDeadline = TimeSpan.FromSeconds(90);

    private readonly ServiceProvider _provider;
    private readonly ScratchCmsDatabase _database;
    private readonly JobWorkerService _worker;

    private RefreshHost(
        ServiceProvider provider,
        ScratchCmsDatabase database,
        FaultPlan plan,
        WarningLog warnings
    )
    {
        _provider = provider;
        _database = database;
        Plan = plan;
        _warnings = warnings;
        _worker = provider.GetRequiredService<JobWorkerService>();
    }

    private readonly WarningLog _warnings;

    public FaultPlan Plan { get; }

    /// <summary>
    /// Every warning and error the host logged, as event name and rendered message, with the exception's type and
    /// never its message. The Configuration Service sanitizes these entries, so a failed case can print them.
    /// </summary>
    public IReadOnlyList<string> Warnings => [.. _warnings.Entries];

    /// <summary>Builds the host and starts its worker.</summary>
    public static async Task<RefreshHost> StartAsync(
        ProjectionE2EFixture fixture,
        ScratchCmsDatabase database,
        RefreshHostSettings settings,
        IReadOnlyDictionary<string, ClientCredentials> clients
    )
    {
        string reader = DmsEducationOrganizationProjectionSettings.SectionName;
        Dictionary<string, string?> values = new()
        {
            [$"{reader}:DmsBaseUrl"] = (settings.DmsBaseUrl ?? fixture.Environment.DmsBaseUrl)
                .ToString()
                .TrimEnd('/'),
            [$"{reader}:PageSize"] = settings.PageSize.ToString(CultureInfo.InvariantCulture),
            ["JobSettings:WorkerEnabled"] = "true",
            ["JobSettings:SchedulerEnabled"] = "false",
            ["JobSettings:RetentionEnabled"] = "false",
            ["JobSettings:PollInterval"] = "00:00:01",
            ["JobSettings:MaxAttempts"] = settings.MaxAttempts.ToString(CultureInfo.InvariantCulture),
            ["JobSettings:RetryBackoffBase"] = settings.RetryBackoffBase.ToString(
                "c",
                CultureInfo.InvariantCulture
            ),
        };
        if (settings.PageRequestTimeoutSeconds is { } pageTimeout)
        {
            values[$"{reader}:PageRequestTimeoutSeconds"] = pageTimeout.ToString(
                CultureInfo.InvariantCulture
            );
        }

        foreach ((string tenant, ClientCredentials client) in clients)
        {
            values[$"{reader}:TenantCredentials:{tenant}:ClientId"] = client.Key;
            values[$"{reader}:TenantCredentials:{tenant}:ClientSecret"] = client.Secret;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        FaultPlan plan = new();
        WarningLog warnings = new();

        ServiceCollection services = new();
        services.AddLogging(logging => logging.AddProvider(warnings));
        services.AddHttpClient();
        services.AddSingleton(configuration);
        services.AddSingleton(
            Options.Create(
                new DatabaseOptions
                {
                    DatabaseConnection = database.ConnectionString,
                    EncryptionKey = "LiveRefreshE2EScratchKey32Chars!",
                }
            )
        );
        services.AddOptions<JobOptions>().Bind(configuration.GetSection(JobOptions.SectionName));
        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<JobOptions>>().Value.LeaseTimings
        );
        services.AddSingleton(new JobRuntimeEnvironment(MultiTenancy: true));
        services.AddSingleton<JobMetrics>();
        services.AddSingleton<IAuditContext, SystemAuditContext>();
        services.AddScoped<ITenantContextProvider, TenantContextProvider>();
        AddProviderRepositories(services, database.Provider == "sqlserver");

        services.AddEducationOrganizationRefresh(configuration);
        services
            .AddHttpClient(DmsEducationOrganizationProjectionHttpClient.Name)
            .AddHttpMessageHandler(() => new FaultInjectingHandler(plan));

        services.AddSingleton<IDmsDataStoreProvisionerReadiness, DmsDataStoreProvisionerReadiness>();
        services.AddSingleton(JobClaimPolicy.ExcludeManagedDataStores);
        services.AddSingleton<JobExecutor>();
        services.AddSingleton<JobWorkerService>();

        ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        // What ValidateOnStart does in a host: the settings must pass the production validators.
        _ = provider.GetRequiredService<IOptions<DmsEducationOrganizationProjectionSettings>>().Value;
        _ = provider.GetRequiredService<IOptions<EducationOrganizationRefreshOptions>>().Value;

        RefreshHost host = new(provider, database, plan, warnings);
        await host._worker.StartAsync(CancellationToken.None);
        return host;
    }

    /// <summary>Enqueues a refresh in the tenant, as the refresh routes do, and returns the job's identifier.</summary>
    public async Task<string> EnqueueAsync(
        string tenant,
        EducationOrganizationRefreshScope scope,
        int dataStoreId
    )
    {
        string payload = JobPayloadSerializer.Serialize(
            new EducationOrganizationRefreshPayload(scope, dataStoreId)
        ) switch
        {
            JobPayloadWriteResult.Success written => written.Json,
            _ => throw new InvalidOperationException("The refresh payload could not be written."),
        };

        await using AsyncServiceScope scope_ = TenantScope(tenant);
        JobEnqueueResult enqueued = await scope_
            .ServiceProvider.GetRequiredService<IJobRepository>()
            .EnqueueJob(
                new JobEnqueueCommand(
                    EducationOrganizationRefreshJobTypes.Refresh,
                    EducationOrganizationRefreshJobTypes.PayloadVersion,
                    payload
                ),
                null,
                CancellationToken.None
            );
        return enqueued is JobEnqueueResult.Success success
            ? success.JobId
            : throw new InvalidOperationException(
                $"Enqueueing the refresh answered {enqueued.GetType().Name}."
            );
    }

    /// <summary>Polls the job's status in its tenant until it is <c>Completed</c> or <c>Error</c>.</summary>
    public async Task<JobStatusResponse> WaitForTerminalStatusAsync(string tenant, string jobId)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            await using (AsyncServiceScope scope = TenantScope(tenant))
            {
                JobStatusQueryResult result = await scope
                    .ServiceProvider.GetRequiredService<IJobRepository>()
                    .GetJobStatus(jobId, CancellationToken.None);
                if (result is not JobStatusQueryResult.Success { Job: var job })
                {
                    throw new InvalidOperationException(
                        $"Reading job {jobId} answered {result.GetType().Name}."
                    );
                }

                if (job.Status is "Completed" or "Error")
                {
                    if (job.Status == "Error")
                    {
                        // The terminal text names a category, not its cause; the host's warnings do.
                        await TestContext.Progress.WriteLineAsync(
                            $"Job {jobId} ended Error. Host warnings:{Environment.NewLine}  "
                                + string.Join($"{Environment.NewLine}  ", Warnings)
                        );
                    }

                    return job;
                }

                if (elapsed.Elapsed >= _terminalDeadline)
                {
                    throw new InvalidOperationException(
                        $"Job {jobId} was still {job.Status} after {_terminalDeadline.TotalSeconds} seconds. "
                            + $"Host warnings: {string.Join(" | ", Warnings)}"
                    );
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }

    /// <summary>Enqueues a refresh and waits for its terminal status.</summary>
    public async Task<JobStatusResponse> RunAsync(
        string tenant,
        EducationOrganizationRefreshScope scope,
        int dataStoreId = 0
    ) => await WaitForTerminalStatusAsync(tenant, await EnqueueAsync(tenant, scope, dataStoreId));

    public async ValueTask DisposeAsync()
    {
        await _worker.StopAsync(CancellationToken.None);
        _worker.Dispose();
        await _provider.DisposeAsync();
    }

    private AsyncServiceScope TenantScope(string tenant)
    {
        AsyncServiceScope scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextProvider>().Context =
            new TenantContext.Multitenant(_database.TenantId(tenant), tenant);
        return scope;
    }

    private static void AddProviderRepositories(IServiceCollection services, bool mssql)
    {
        // The executor installs a multi-tenant job's tenant by its identifier, through the tenant repository.
        if (mssql)
        {
            services.AddTransient<ITenantRepository, Backend.Mssql.Repositories.TenantRepository>();
            services.AddTransient<IJobRepository, Backend.Mssql.Repositories.JobRepository>();
            services.AddTransient<IJobLeaseRepository, Backend.Mssql.Repositories.JobLeaseRepository>();
            services.AddTransient<IJobFenceFactory, Backend.Mssql.Jobs.MssqlJobFenceFactory>();
            services.AddScoped<
                IEducationOrganizationSnapshotRepository,
                Backend.Mssql.Repositories.EducationOrganizationSnapshotRepository
            >();
            return;
        }

        services.AddTransient<ITenantRepository, Backend.Postgresql.Repositories.TenantRepository>();
        services.AddTransient<IJobRepository, Backend.Postgresql.Repositories.JobRepository>();
        services.AddTransient<IJobLeaseRepository, Backend.Postgresql.Repositories.JobLeaseRepository>();
        services.AddTransient<IJobFenceFactory, Backend.Postgresql.Jobs.PostgresqlJobFenceFactory>();
        services.AddScoped<
            IEducationOrganizationSnapshotRepository,
            Backend.Postgresql.Repositories.EducationOrganizationSnapshotRepository
        >();
    }

    /// <summary>Audit fields as a background operation writes them.</summary>
    private sealed class SystemAuditContext : IAuditContext
    {
        public string GetCurrentUser() => "system";

        public DateTime GetCurrentTimestamp() => DateTime.UtcNow;
    }

    /// <summary>Keeps warnings and errors: event name, rendered message and exception type only.</summary>
    private sealed class WarningLog : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (IsEnabled(logLevel))
            {
                Entries.Enqueue(
                    $"{logLevel} {eventId.Name}: {formatter(state, null)}"
                        + (exception is null ? "" : $" [{exception.GetType().FullName}]")
                );
            }
        }

        public void Dispose() { }
    }
}
