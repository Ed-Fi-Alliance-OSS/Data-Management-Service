// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data.Common;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using CdcProvider = EdFi.DataManagementService.Backend.Ddl.CdcProvider;
using CdcProviderSetupMode = EdFi.DataManagementService.Backend.Ddl.CdcProviderSetupMode;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

/// <summary>
/// Shared real-system resources. Starting this fixture creates no controller ownership evidence:
/// admission suites must execute managed CREATE DATABASE and emitted DDL themselves. The legacy
/// template smoke setup remains opt-in and cannot serve as an initial-admission fixture.
/// </summary>
internal sealed class CdcControllerFixture : IAsyncDisposable
{
    private readonly HttpClient _connectClient = CdcConnectRestAdapter.CreateHttpClient();
    private readonly HttpClient _metricsClient = CdcConnectorTelemetryAdapter.CreateHttpClient();
    private readonly ServiceProvider _bindingServices;
    private readonly bool _keepResources;
    private bool _disposed;

    private CdcControllerFixture(CdcConnectorTemplatePinnedImageFixture resources, bool keepResources)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Local controller fixtures require Unix file permissions."
            );
        }
        Resources = resources;
        _keepResources = keepResources;
        StateRoot = Path.Combine(Path.GetTempPath(), "dms-cdc-controller-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(
            StateRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );
        _bindingServices = new ServiceCollection()
            .AddDmsCdcControlPlane()
            .Configure<CdcBindingStateStoreOptions>(options => options.RootPath = StateRoot)
            .BuildServiceProvider();
        Bindings = Hooks.Decorate(
            _bindingServices.GetRequiredService<ICdcBindingLifecycleService>(),
            name =>
                name == nameof(ICdcBindingLifecycleService.CreateBindingIfAbsentAsync)
                    ? CdcControllerBoundary.Binding
                    : CdcControllerBoundary.Observation
        );
        Connect = Hooks.Decorate<ICdcConnectTransport>(
            new CdcConnectRestAdapter(_connectClient),
            name =>
            {
                ConnectCalls.Enqueue(name);
                BeforeConnectCall(name);
                return name switch
                {
                    nameof(ICdcConnectTransport.CreateAsync) => CdcControllerBoundary.Registration,
                    nameof(ICdcConnectTransport.UpdateConfigurationForRecordSizeIncreaseAsync) =>
                        CdcControllerBoundary.Rollout,
                    nameof(ICdcConnectTransport.DeleteAsync)
                    or nameof(ICdcConnectTransport.DeleteOffsetsAsync) => CdcControllerBoundary.Cleanup,
                    _ => CdcControllerBoundary.Observation,
                };
            }
        );
        Worker = Hooks.Decorate<ICdcWorkerInspectionTransport>(
            new CdcWorkerDeployment(resources.ControllerProject, "kafka-cdc-worker"),
            _ =>
            {
                BeforeWorkerCall();
                return CdcControllerBoundary.Observation;
            }
        );
        Metrics = new CdcConnectorTelemetryAdapter(_metricsClient, Connect, Worker);
    }

    public CdcConnectorTemplatePinnedImageFixture Resources { get; }
    public string StateRoot { get; }
    public CdcControllerFixtureHooks Hooks { get; } = new();
    public ICdcConnectTransport Connect { get; }
    public ConcurrentQueue<string> ConnectCalls { get; } = new();
    public Action<string> BeforeConnectCall { get; set; } = _ => { };
    public ICdcBindingLifecycleService Bindings { get; }
    public Action BeforeWorkerCall { get; set; } = () => { };
    public ICdcWorkerInspectionTransport Worker { get; }
    public ICdcWorkerMetricsTransport Metrics { get; }
    public Uri ConnectEndpoint => Resources.ControllerConnectEndpoint;

    public Task<Uri> MetricsEndpointAsync(CancellationToken token) =>
        Resources.ControllerMetricsEndpointAsync(token);

    public Task<DbConnection> OpenAdminConnectionAsync(CancellationToken token) =>
        Resources.ControllerAdminConnectionAsync(token);

    public LocalCdcWorkflowJournalStore CreateJournalStore() => Hooks.CreateJournalStore(StateRoot);

    public static async Task<CdcControllerFixture> StartAsync(
        CdcProvider provider,
        CancellationToken token,
        Func<CdcControllerFixture, CancellationToken, Task> beforeWorker = null!,
        bool nativeKafka = false,
        bool composeKafka = false,
        bool offlineKafka = false
    )
    {
        var settings = CdcConnectorTemplateSmokeSettings.FromEnvironment(provider);
        ValidatePrerequisites(settings, provider);
        CdcControllerFixture fixture = null!;
        try
        {
            var resources = await CdcConnectorTemplatePinnedImageFixture.StartAsync(
                provider,
                token,
                async (resources, ct) =>
                {
                    // The same durable root/hooks are available for CREATE receipts and broker
                    // preflight before the worker starts, and survive every controller invocation.
                    fixture = new(resources, settings.KeepContainers);
                    await resources.AssertControllerProviderPrerequisiteAsync(ct);
                    if (beforeWorker is not null)
                    {
                        await beforeWorker(fixture, ct);
                    }
                },
                exposeBroker: true,
                nativeKafka: nativeKafka,
                composeKafka: composeKafka,
                offlineKafka: offlineKafka
            );
            if (!offlineKafka)
            {
                await resources.AssertControllerMetricsPrerequisiteAsync(token);
            }
            return fixture;
        }
        catch (Exception exception)
        {
            if (fixture is not null)
            {
                await fixture.DisposeAsync();
            }
            if (exception is OperationCanceledException or AssertionException or IgnoreException)
            {
                throw;
            }
            settings.StopOnPrerequisiteFailure(
                provider,
                "Controller fixture prerequisite failed. Details redacted."
            );
            throw;
        }
    }

    internal static void ValidatePrerequisites(
        CdcConnectorTemplateSmokeSettings settings,
        CdcProvider provider
    )
    {
        settings.StopIfNotConfigured(provider);
        if (!CdcQualifiedWorkerImage.Images.Contains(settings.ConnectImage))
        {
            settings.StopOnPrerequisiteFailure(
                provider,
                "Controller qualification requires the published exporter-enabled image."
            );
        }
    }

    public Task<T> InvokeAsync<T>(
        CdcControllerBoundary boundary,
        Func<CancellationToken, Task<T>> controller,
        CancellationToken token
    ) => Hooks.InvokeAsync(boundary, controller, token);

    public Task RecoverWorkerAsync(bool crash, CancellationToken token) =>
        InvokeAsync(
            CdcControllerBoundary.WorkerRecovery,
            async ct =>
            {
                await Resources.RecoverControllerWorkerAsync(crash, ct);
                return true;
            },
            token
        );

    /// <summary>Each probe must honor its token; its wait is also bounded if it does not.</summary>
    public static async Task WaitAsync(
        Func<CancellationToken, Task<bool>> probe,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken token
    )
    {
        if (
            timeout <= TimeSpan.Zero
            || timeout > TimeSpan.FromMinutes(10)
            || pollInterval <= TimeSpan.Zero
            || pollInterval > timeout
        )
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            while (!await probe(deadline.Token).WaitAsync(deadline.Token))
            {
                await Task.Delay(pollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Controller fixture observation deadline expired.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connectClient.Dispose();
        _metricsClient.Dispose();
        await _bindingServices.DisposeAsync();
        // Only an allow-listed trace is attached. Raw Connect/provider logs contain credentials and rows.
        var artifact = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "cdc-controller-" + Guid.NewGuid().ToString("N") + ".json"
        );
        try
        {
            await File.WriteAllTextAsync(artifact, Hooks.SerializeTrace());
            TestContext.AddTestAttachment(artifact, "Sanitized controller boundary trace");
        }
        finally
        {
            try
            {
                await Resources.DisposeAsync();
            }
            finally
            {
                if (!_keepResources)
                {
                    Directory.Delete(StateRoot, recursive: true);
                }
            }
        }
    }
}

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    internal async Task<CdcDeploymentRequest> CreateControllerSmokeObservationRequestAsync(
        CdcConnectorTemplateRequest template,
        CancellationToken token
    )
    {
        using var client = await CreateMetricsClientAsync(token);
        string metrics = await client.GetStringAsync("/metrics", token);
        return new(
            template.Binding,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            BuildProviderSetupRequest(CdcProviderSetupMode.ValidateOnly, null!),
            ControllerConnectEndpoint,
            new Uri(client.BaseAddress!, "/metrics"),
            template.DeploymentPolicy,
            EdFi.DataManagementService.Backend.Cdc.Tests.Unit.CdcDeploymentRequestTestData.Worker(
                heapBytes: checked(
                    (long)CdcTelemetryQualification.Scalar(metrics, "edfi_cdc_worker_heap_max_bytes")
                ),
                digest: CdcQualifiedWorkerImage.Digest,
                offsetTopic: _resourcePrefix + ".connect.offsets",
                workerKey: _resourcePrefix
            ),
            template.ProviderConnectionProperties,
            template.KafkaClientSecurityProperties,
            new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(250))
        );
    }

    internal string ControllerKafkaBootstrapServers =>
        _controllerBrokerPort > 0
            ? $"127.0.0.1:{_controllerBrokerPort}"
            : throw new InvalidOperationException("Host broker listener was not enabled.");
    internal string ControllerProject => _resourcePrefix;
    internal Uri ControllerConnectEndpoint =>
        _controllerConnectPort > 0
            ? new Uri($"http://127.0.0.1:{_controllerConnectPort}")
            : _httpClient.BaseAddress!;

    internal async Task<Uri> ControllerMetricsEndpointAsync(CancellationToken token)
    {
        if (_controllerMetricsPort > 0)
        {
            return new Uri($"http://127.0.0.1:{_controllerMetricsPort}/metrics");
        }
        using var client = await CreateMetricsClientAsync(token);
        return new Uri(client.BaseAddress!, "/metrics");
    }

    internal async Task<DbConnection> ControllerAdminConnectionAsync(CancellationToken token)
    {
        var connection = CreateProviderAdminConnection(await ReadMappedProviderPortAsync(token));
        // Server administration is usable before the story suite creates its owned database.
        connection.ConnectionString =
            Provider == CdcProvider.Postgresql
                ? new Npgsql.NpgsqlConnectionStringBuilder(connection.ConnectionString)
                {
                    Database = "postgres",
                }.ConnectionString
                : new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection.ConnectionString)
                {
                    InitialCatalog = "master",
                }.ConnectionString;
        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal async Task AssertControllerProviderPrerequisiteAsync(CancellationToken token)
    {
        await using var connection = await ControllerAdminConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText =
            Provider == CdcProvider.Postgresql
                ? "SELECT current_setting('wal_level') = 'logical'"
                : "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int)";
        object result = (await command.ExecuteScalarAsync(token))!;
        if (Provider == CdcProvider.Postgresql ? result is not true : Convert.ToInt32(result) < 17)
        {
            throw new InvalidOperationException("Controller provider version/capture prerequisite failed.");
        }
    }

    internal async Task AssertControllerMetricsPrerequisiteAsync(CancellationToken token)
    {
        using var client = await CreateMetricsClientAsync(token);
        await CdcControllerFixture.WaitAsync(
            async ct =>
            {
                string text = await client.GetStringAsync("/metrics", ct);
                if (
                    !text.Contains("edfi_cdc_worker_start_time_seconds ", StringComparison.Ordinal)
                    || !text.Contains("edfi_cdc_worker_heap_max_bytes ", StringComparison.Ordinal)
                )
                {
                    return false;
                }
                return CdcTelemetryQualification.Scalar(text, "edfi_cdc_worker_start_time_seconds") > 0
                    && CdcTelemetryQualification.Scalar(text, "edfi_cdc_worker_heap_max_bytes") > 0;
            },
            TimeSpan.FromSeconds(60),
            TimeSpan.FromMilliseconds(250),
            token
        );
    }

    internal async Task RecoverControllerWorkerAsync(bool crash, CancellationToken token)
    {
        if (crash)
        {
            await _docker.RunAsync(["kill", "--signal", "KILL", ConnectContainerName], token);
            await _docker.RunAsync(["start", ConnectContainerName], token);
        }
        else
        {
            await _docker.RunAsync(["restart", ConnectContainerName], token);
        }

        await WaitForKafkaConnectAsync(token);
        await AssertControllerMetricsPrerequisiteAsync(token);
    }
}
