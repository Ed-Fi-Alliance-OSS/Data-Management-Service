// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Confluent.Kafka;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Cdc.Tests.Integration;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.SchemaTools.Cdc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

/// <summary>Attaches to wrapper-owned infrastructure. Owns only local resources; never admits,
/// provisions, retires, or resets the stack. All scenario/controller operations share one phase lock.</summary>
internal sealed class CdcAttachedContext : IAsyncDisposable
{
    private readonly CdcAttachmentResources _resources = new();
    private CdcCommandConfiguration _config = null!;
    private CdcRuntimeOwner _owner = null!;
    private CdcControllerStatus _status = null!;
    private CdcManagedLifecycle _lifecycle = null!;
    public CdcDeploymentRequest Request { get; private set; } = null!;
    public CdcApiClient Api { get; private set; } = null!;
    public CdcDocumentObserver Documents { get; private set; } = null!;
    public MessageContractKafkaObserver Kafka { get; private set; } = null!;
    public MessageContractProviderObserver Provider { get; private set; } = null!;
    public MessageContractProviderFences Fences { get; private set; } = null!;
    public ICdcConnectTransport Connect { get; private set; } = null!;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Sonar",
        "S1854",
        Justification = "Catch uses the current attachment boundary for sanitized diagnostics."
    )]
    public async Task InitializeAsync(CancellationToken token)
    {
        CdcAttachedContext context = this;
        string boundary = "HANDOFF";
        try
        {
            string path = Environment.GetEnvironmentVariable("CDC_API_E2E_HANDOFF_PATH") ?? "";
            var handoff = CdcApiHandoff.Load(path);
            context._config = CdcCommandConfiguration.Load(handoff.SettingsPath);
            var config = context._config;
            context._resources.Add((IDisposable)config.Settings);
            config.ValidateControllerSettings();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(config.Timing.WaitTimeout);
            var ct = deadline.Token;
            boundary = "PROVENANCE_OR_HTTP_CONFIGURATION";
            await VerifyWrapperAsync(config, path, ct);
            config.Settings["Cdc:PublicationHistory:StatePath"] = handoff.StatePath;
            config.Settings["Cdc:PublicationHistory:DeploymentKey"] = config.Target.DeploymentKey;

            boundary = "RETAINED_BINDING";
            DbConnection connection = config.CreateConnection();
            context._resources.Add((IAsyncDisposable)connection);
            var loader = new ApiSchemaFileLoader(
                new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance),
                NullLogger<ApiSchemaFileLoader>.Instance
            );
            var builder = new EffectiveSchemaSetBuilder(
                new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance),
                new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
            );
            context.Request = await config.CreateRequestAsync(
                handoff.StatePath,
                connection,
                loader,
                builder,
                ct,
                deferProjectionPreparation: true,
                useRetainedBinding: true
            );
            var request = context.Request;
            var target = DocumentCacheTargetKey.Create(
                config.Settings["Cdc:TenantKey"] ?? "",
                long.Parse(request.Binding.DataStoreId, CultureInfo.InvariantCulture)
            );
            var logger = new LoggerConfiguration().CreateLogger();
            context._resources.Add((IDisposable)logger);
            var services = new ServiceCollection()
                .AddCdcCommandControlPlane(config.Settings, logger)
                .BuildServiceProvider();
            context._resources.Add((IAsyncDisposable)services);
            var connectClient = CdcConnectRestAdapter.CreateHttpClient();
            context._resources.Add(connectClient);
            var metricsClient = CdcConnectorTelemetryAdapter.CreateHttpClient();
            context._resources.Add(metricsClient);
            context.Connect = new CdcConnectRestAdapter(connectClient);
            var worker = new CdcWorkerDeployment(config.Project, "kafka-cdc-worker");
            var metrics = new CdcConnectorTelemetryAdapter(metricsClient, context.Connect, worker);
            var sizes = new CdcComposeBrokerSizeDeployment(
                config.ComposeFile,
                config.EnvironmentFile,
                config.Project,
                config.BrokerSizeOverride
            );
            var adminConfig = new AdminClientConfig(config.Properties("Cdc:KafkaAdminProperties"))
            {
                BootstrapServers = config.Required("Cdc:KafkaAdminBootstrapServers"),
            };
            var kafka = Require(
                CdcKafkaAdminAdapter.Create(
                    adminConfig,
                    new CdcComposeKafkaAuthorizationInspection(
                        config.Project,
                        adminConfig.BootstrapServers,
                        request.ConnectorPolicy.KafkaBootstrapServers
                    ),
                    sizes
                )
            );
            context._resources.Add(kafka);
            var setup = services.GetRequiredService<ICdcProviderSetupService>();
            var templates = services.GetRequiredService<ICdcConnectorTemplateService>();
            var positions = services
                .GetServices<ICdcProviderSourcePositionAdapter>()
                .Single(p => p.Provider == request.Binding.Provider);
            context._status = new(
                handoff.StatePath,
                setup,
                templates,
                kafka,
                context.Connect,
                worker,
                metrics,
                [positions]
            );
            context._lifecycle = new(
                handoff.StatePath,
                setup,
                templates,
                kafka,
                context.Connect,
                worker,
                metrics,
                [positions]
            );
            context._owner = new(cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                CdcProjectionGate gate = new(target, TimeSpan.FromMinutes(5));
                ICdcProjectionRuntime runtime = new CdcDeferredProjectionRuntime(async ct =>
                {
                    // Status contains retained incidents before schema/runtime preparation.
                    _ = request.ProviderSetup;
                    return await CdcProjectionRuntimeFactory.CreateAsync(
                        config.Settings,
                        logger,
                        target,
                        gate.ConfigureServices,
                        ct
                    );
                });
                return Task.FromResult((runtime, gate));
            });
            // Registered last: stop executor/gate before disposing transports, DI and settings.
            context._resources.Add(context._owner);

            boundary = "HTTP_ENDPOINTS";
            var cms = new Uri(config.Required("ConfigurationServiceSettings:BaseUrl").TrimEnd('/') + "/");
            using var http = new HttpClient { Timeout = config.Timing.CallTimeout };
            foreach (var endpoint in new[] { new Uri(handoff.DmsBaseUrl, "health"), new Uri(cms, "health") })
            {
                using var response = await http.GetAsync(endpoint, ct);
                response.EnsureSuccessStatusCode();
            }
            boundary = "PROVIDER";
            await connection.OpenAsync(ct);
            context.Documents = new(request.Binding.Provider, connection.ConnectionString);
            context.Provider = new(request.Binding, config.CreateConnection);
            context.Fences = new(context.Provider, request, context.Connect);
            boundary = "KAFKA_ADVERTISED_ENDPOINTS";
            context.Kafka = new(adminConfig.BootstrapServers);
            var boundaries = await context.Kafka.CaptureKafkaBoundariesAsync(request.Binding.TopicName, ct);
            if (boundaries.Count != request.Binding.PartitionCount)
            {
                throw new InvalidOperationException();
            }

            boundary = "CONNECT";
            _ = Require(await context.Connect.ReadStatusAsync(request, ct));
            boundary = "METRICS";
            using (var response = await http.GetAsync(request.WorkerMetricsEndpoint, ct))
            {
                response.EnsureSuccessStatusCode();
            }

            boundary = "RUNTIME_IDENTITY_SCHEMA";
            await context._owner.InPhaseAsync(
                async cancellation =>
                {
                    await context._owner.OpenAsync(cancellation);
                    var status = await context._status.StatusAsync(
                        [context.CurrentTarget()],
                        cancellation,
                        cancellation
                    );
                    if (status.Aggregate.Readiness != CdcReadiness.Ready)
                    {
                        throw new InvalidOperationException();
                    }

                    var observed = await context._owner.Runtime.ObserveEstablishedDatabaseAsync(cancellation);
                    if (
                        observed.TargetKey != target
                        || observed.PhysicalSourceFingerprint != request.Binding.PhysicalSourceFingerprint
                    )
                    {
                        throw new InvalidOperationException();
                    }

                    return true;
                },
                ct
            );
            boundary = "API_AUTHENTICATION";
            context.Api = new(
                handoff.DmsBaseUrl,
                cms,
                int.Parse(request.Binding.DataStoreId, CultureInfo.InvariantCulture)
            );
            context._resources.Add(context.Api);
            await context.Api.AuthenticateAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The scenario owner accounts for partial-attachment disposal in its finalization path.
            // Do not retain raw transport exceptions as inner exceptions (NUnit prints them).
            throw new InvalidOperationException($"CDC_API_ATTACHMENT_{boundary}");
        }
    }

    private CdcControllerStatusTarget CurrentTarget() => new(Request, _owner.Runtime, _config.LagThreshold);

    public Task<T> InPhaseAsync<T>(Func<Phase, CancellationToken, Task<T>> action, CancellationToken token) =>
        _owner.InPhaseAsync(
            async ct =>
            {
                using var phase = new Phase(this);
                return await action(phase, ct);
            },
            token
        );

    internal sealed class Phase(CdcAttachedContext context) : IDisposable
    {
        private bool _ended;
        private CdcAttachedContext Current
        {
            get
            {
                ObjectDisposedException.ThrowIf(_ended, this);
                return context;
            }
        }
        public CdcProjectionGate Gate => Current._owner.Gate;
        public ICdcProjectionRuntime Runtime => Current._owner.Runtime;

        public Task<CdcControllerStatusResult> StatusAsync(CancellationToken token) =>
            Current._status.StatusAsync([Current.CurrentTarget()], token, token);

        public Task<CdcManagedLifecycleResult> ManageAsync(
            CdcManagedLifecycleOperation operation,
            CancellationToken token
        ) => Current._lifecycle.ExecuteAsync(Current.CurrentTarget(), operation, token, token);

        public Task StopRuntimeAsync() => Current._owner.StopAsync();

        public Task OpenRuntimeAsync(CancellationToken token) => Current._owner.OpenAsync(token);

        public void Dispose() => _ended = true;
    }

    private static T Require<T>(CdcTransportResult<T> result)
        where T : notnull =>
        result is CdcTransportResult<T>.Observed observed
            ? observed.Value
            : throw new InvalidOperationException("CDC_API_ATTACHMENT_UNAVAILABLE");

    private static async Task VerifyWrapperAsync(
        CdcCommandConfiguration config,
        string path,
        CancellationToken token
    )
    {
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (
            string argument in new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-File",
                Path.Combine(Path.GetDirectoryName(config.ComposeFile)!, "assert-cdc-api-attachment.ps1"),
                "-HandoffPath",
                path,
            }
        )
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException();
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    public ValueTask DisposeAsync() => _resources.DisposeAsync();

    public Task DisposeAfterFailureAsync() => _resources.DisposeAfterFailureAsync();
}

internal sealed record CdcApiHandoff(
    string SettingsPath,
    string StatePath,
    string DeploymentPath,
    string HttpComposePath,
    Uri DmsBaseUrl
)
{
    public static CdcApiHandoff Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != 1)
        {
            throw new InvalidOperationException();
        }

        string ReadPath(string name)
        {
            string value = root.GetProperty(name).GetString()!;
            if (
                !Path.IsPathFullyQualified(value)
                || (name == "statePath" ? !Directory.Exists(value) : !File.Exists(value))
            )
            {
                throw new InvalidOperationException();
            }

            return value;
        }
        var endpoint = new Uri(
            root.GetProperty("dmsBaseUrl").GetString()!.TrimEnd('/') + "/",
            UriKind.Absolute
        );
        if (endpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException();
        }

        return new(
            ReadPath("settingsPath"),
            ReadPath("statePath"),
            ReadPath("deploymentPath"),
            ReadPath("httpComposePath"),
            endpoint
        );
    }

    public override string ToString() => nameof(CdcApiHandoff);
}
