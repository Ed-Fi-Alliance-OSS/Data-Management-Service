// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Confluent.Kafka;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Core.Startup;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Serilog;
using CoreProvider = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcProvider;

namespace EdFi.DataManagementService.SchemaTools.Cdc;

public sealed class CdcCommandRunner(IApiSchemaFileLoader loader, EffectiveSchemaSetBuilder schemaBuilder)
    : ICdcCommandRunner
{
    // Narrow host seams retain the production command/controllers in transport and initialization tests.
    internal Func<HttpClient> CreateConnectClient { get; init; } = CdcConnectRestAdapter.CreateHttpClient;
    internal Func<CdcCommandConfiguration, ICdcWorkerInspectionTransport> CreateWorker { get; init; } =
        config => new CdcWorkerDeployment(config.Project, "kafka-cdc-worker");
    internal delegate Task<CdcTransportResult<ICdcProjectionRuntime>> ProjectionRuntimeFactory(
        IConfiguration settings,
        ILogger logger,
        DocumentCacheTargetKey target,
        CancellationToken token
    );
    internal ProjectionRuntimeFactory CreateProjectionRuntime { get; init; } =
        CdcProjectionRuntimeFactory.CreateAsync;

    internal delegate Task<CdcDeploymentRequest> DeploymentRequestFactory(
        CdcCommandConfiguration config,
        string statePath,
        DbConnection connection,
        IApiSchemaFileLoader loader,
        EffectiveSchemaSetBuilder schemaBuilder,
        CancellationToken token,
        bool deferProjection,
        bool useRetainedBinding
    );
    internal DeploymentRequestFactory CreateRequest { get; init; } =
        (config, state, connection, loader, builder, token, defer, retained) =>
            config.CreateRequestAsync(state, connection, loader, builder, token, defer, retained);
    internal Func<CdcCommandConfiguration, ICdcWorkerStartupTransport> CreateStartupTransport { get; init; } =
        config => new CdcComposeWorkerStartupTransport(
            config.ComposeFile,
            config.EnvironmentFile,
            config.Project,
            config.BrokerSizeOverride
        );
    internal Func<CdcKafkaProvisioning, CdcKafkaProvisioning> ConfigureKafkaProvisioning { get; init; } =
        provisioning => provisioning;
    internal Func<CdcInitialEnableWorkflow, CdcInitialEnableWorkflow> ConfigureEnableWorkflow { get; init; } =
        workflow => workflow;
    internal Func<CdcEstablishedValidation, CdcEstablishedValidation> ConfigureValidation { get; init; } =
        validation => validation;
    internal Func<CdcControllerStatus, CdcControllerStatus> ConfigureStatus { get; init; } = status => status;
    internal Func<CdcManagedLifecycle, CdcManagedLifecycle> ConfigureManagedLifecycle { get; init; } =
        lifecycle => lifecycle;

    internal Func<CdcBindingRetirement, CdcBindingRetirement> ConfigureRetirement { get; init; } =
        retirement => retirement;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Sonar",
        "S1854",
        Justification = "Catch paths use the current boundary when a call throws."
    )]
    public async Task<CdcCommandResult> RunAsync(
        CdcCommandInvocation invocation,
        TextWriter progress,
        CancellationToken token
    )
    {
        string name = CdcCommandHost.Name(invocation.Operation);
        CdcDeploymentComponent component = CdcDeploymentComponent.Request;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            var config = CdcCommandConfiguration.Load(invocation.SettingsPath);
            var settings = config.Settings;
            using var settingsLifetime = (IDisposable)settings;
            bool deferProjection =
                invocation.Operation
                is CdcCommandOperation.Status
                    or CdcCommandOperation.Watch
                    or CdcCommandOperation.Stop
                    or CdcCommandOperation.Start
                    or CdcCommandOperation.Restart
                    or CdcCommandOperation.Resume
                    or CdcCommandOperation.IncreaseRecordSize;
            config.ValidateControllerSettings();
            settings["Cdc:PublicationHistory:StatePath"] = invocation.StatePath;
            settings["Cdc:PublicationHistory:DeploymentKey"] = config.Target.DeploymentKey;
            timeout.CancelAfter(config.Timing.WaitTimeout);
            var ct = timeout.Token;
            if (
                invocation.Operation == CdcCommandOperation.Retire
                && invocation.Generation != config.Target.Generation
            )
            {
                throw new ArgumentException("CDC command input is invalid.");
            }
            await using var connection = config.CreateConnection();
            component = CdcDeploymentComponent.WorkflowState;
            var request = await CreateRequest(
                config,
                invocation.StatePath,
                connection,
                loader,
                schemaBuilder,
                ct,
                deferProjection,
                invocation.Operation is CdcCommandOperation.Status or CdcCommandOperation.Watch
            );
            var targetKey = DocumentCacheTargetKey.Create(
                settings["Cdc:TenantKey"] ?? "",
                long.Parse(request.Binding.DataStoreId, CultureInfo.InvariantCulture)
            );
            // Runtime logging can include database/connection metadata. This administrative surface
            // emits only the controllers' allow-listed diagnostics, even with --verbose.
            using var logger = new LoggerConfiguration().CreateLogger();
            var services = new ServiceCollection();
            services.AddCdcCommandControlPlane(settings, logger);
            await using var provider = services.BuildServiceProvider();
            using var connectClient = CreateConnectClient();
            using var metricsClient = CdcConnectorTelemetryAdapter.CreateHttpClient();
            var connect = new CdcConnectRestAdapter(connectClient);
            var worker = CreateWorker(config);
            var metrics = new CdcConnectorTelemetryAdapter(metricsClient, connect, worker);
            var sizes = new CdcComposeBrokerSizeDeployment(
                config.ComposeFile,
                config.EnvironmentFile,
                config.Project,
                config.BrokerSizeOverride
            );
            component = CdcDeploymentComponent.Kafka;
            var adminConfig = new AdminClientConfig(config.Properties("Cdc:KafkaAdminProperties"));
            adminConfig.BootstrapServers = config.Required("Cdc:KafkaAdminBootstrapServers");
            using var kafka = Require(
                await Task.FromResult(
                    CdcKafkaAdminAdapter.Create(
                        adminConfig,
                        new CdcComposeKafkaAuthorizationInspection(
                            config.Project,
                            adminConfig.BootstrapServers,
                            request.ConnectorPolicy.KafkaBootstrapServers
                        ),
                        sizes
                    )
                )
            );
            var setup = provider.GetRequiredService<ICdcProviderSetupService>();
            var templates = provider.GetRequiredService<ICdcConnectorTemplateService>();
            var positions = provider
                .GetServices<ICdcProviderSourcePositionAdapter>()
                .Single(p => p.Provider == request.Binding.Provider);
            component = CdcDeploymentComponent.Projection;
            await using var runtime = new CdcDeferredProjectionRuntime(async cancellation =>
            {
                // Includes schema loading and emitted inventory validation. These are observation inputs,
                // and must not precede retained-incident containment or be required for explicit stop.
                _ = request.ProviderSetup;
                return await CreateProjectionRuntime(settings, logger, targetKey, cancellation);
            });
            if (invocation.Operation == CdcCommandOperation.Retire)
            {
                var cleanup = new CdcProviderArtifactCleanupAdapter(
                    request.Binding.Provider == CoreProvider.Postgresql
                        ? NpgsqlFactory.Instance
                        : SqlClientFactory.Instance,
                    connection.ConnectionString
                );
                var result = await ConfigureRetirement(
                        new CdcBindingRetirement(invocation.StatePath, connect, kafka, cleanup)
                        {
                            WorkerStartup = new CdcWorkerStartup(
                                ConfigureKafkaProvisioning(
                                    new CdcKafkaProvisioning(
                                        invocation.StatePath,
                                        kafka,
                                        runtime,
                                        new CdcKafkaProducerInspection(connect, worker)
                                    )
                                ),
                                CreateStartupTransport(config)
                            ),
                        }
                    )
                    .RetireAsync(request, invocation.Generation, invocation.DestructiveCleanup, ct);
                return Result(result.Succeeded, result, result.Diagnostics);
            }
            if (!deferProjection)
            {
                await runtime.InitializeAsync(ct);
            }
            var target = new CdcControllerStatusTarget(request, runtime, config.LagThreshold);
            var validation = ConfigureValidation(
                new CdcEstablishedValidation(
                    invocation.StatePath,
                    setup,
                    templates,
                    kafka,
                    connect,
                    worker,
                    metrics,
                    positions
                )
            );
            var status = ConfigureStatus(
                new CdcControllerStatus(
                    invocation.StatePath,
                    setup,
                    templates,
                    kafka,
                    connect,
                    worker,
                    metrics,
                    [positions]
                )
            );
            switch (invocation.Operation)
            {
                case CdcCommandOperation.StartWorker:
                    // The wrapper holds its deployment inventory lock and accounts for every peer.
                    // This target still needs the controller's original completed shutdown intent.
                    var retained = Require(
                        await new CdcWorkerStartup(
                            ConfigureKafkaProvisioning(
                                new CdcKafkaProvisioning(
                                    invocation.StatePath,
                                    kafka,
                                    runtime,
                                    new CdcKafkaProducerInspection(connect, worker)
                                )
                            ),
                            CreateStartupTransport(config)
                        ).StartRetainedAsync(request, ct)
                    );
                    return Result(true, retained, []);
                case CdcCommandOperation.Enable:
                    var publication = Require(
                        await ConfigureEnableWorkflow(
                                new CdcInitialEnableWorkflow(
                                    invocation.StatePath,
                                    setup,
                                    templates,
                                    kafka,
                                    connect,
                                    worker,
                                    new CdcComposeWorkerStartupTransport(
                                        config.ComposeFile,
                                        config.EnvironmentFile,
                                        config.Project,
                                        config.BrokerSizeOverride
                                    ),
                                    metrics,
                                    positions
                                )
                            )
                            .EnableAsync(request, runtime, config.LagThreshold, ct)
                    );
                    return Result(true, publication, []);
                case CdcCommandOperation.Validate:
                    var validated = Require(
                        await validation.ValidateAsync(
                            request,
                            runtime,
                            CdcEstablishedValidationMode.RunningPublication,
                            config.LagThreshold,
                            cancellationToken: ct
                        )
                    );
                    return Result(validated.PublicationReady, validated, validated.Diagnostics);
                case CdcCommandOperation.Status:
                    return StatusResult(await status.StatusAsync([target], token, ct));
                case CdcCommandOperation.Watch:
                    CdcControllerStatusResult last = null!;
                    await foreach (
                        var pass in status.WatchAsync(
                            [target],
                            invocation.MaximumPasses,
                            request.Timing.PollInterval,
                            token,
                            ct
                        )
                    )
                    {
                        last = pass;
                        await progress.WriteLineAsync(
                            JsonSerializer.Serialize(pass, CdcCommandHost.JsonOptions)
                        );
                    }
                    if (
                        last is null
                        || (
                            ct.IsCancellationRequested
                            && !last.Targets.Any(t =>
                                t.Status.SourceHistory.Continuity == CdcSourceHistoryContinuity.Lost
                            )
                        )
                    )
                    {
                        ct.ThrowIfCancellationRequested();
                    }
                    return StatusResult(last!);
                case CdcCommandOperation.IncreaseRecordSize:
                    var acknowledgement = await ReadAcknowledgementAsync(invocation, request, ct);
                    var scope = new CdcRecordSizeIncreaseScope(
                        acknowledgement.OperationId,
                        request.Binding.ToCompleteBindingIdentity(),
                        acknowledgement.PreviousMaxRecordBytes,
                        acknowledgement.RequestedMaxRecordBytes
                    );
                    var increase = await new CdcRecordSizeIncrease(
                        invocation.StatePath,
                        setup,
                        templates,
                        kafka,
                        kafka,
                        connect,
                        worker,
                        metrics,
                        positions
                    ).IncreaseAsync(
                        target,
                        scope,
                        acknowledgement.RequestedProducerBufferBytes,
                        (current, cancellation) =>
                        {
                            cancellation.ThrowIfCancellationRequested();
                            // Explicit confirmation flag belongs to this process invocation. A stored
                            // acknowledgement cannot supply current.InvocationId or reuse a timestamp.
                            return Task.FromResult(
                                new CdcRecordSizeIncreaseConfirmation(
                                    scope,
                                    new(
                                        current.InvocationId,
                                        acknowledgement.OperatorIdentity,
                                        DateTimeOffset.UtcNow,
                                        acknowledgement.NoConsumers,
                                        acknowledgement.Consumers.ToImmutableArray()
                                    ),
                                    invocation.ConfirmConsumerCapacity
                                )
                            );
                        },
                        token,
                        ct
                    );
                    return Result(increase.Succeeded && increase.Ready, increase, increase.Diagnostics);
                default:
                    var operation = invocation.Operation switch
                    {
                        CdcCommandOperation.Start => CdcManagedLifecycleOperation.Start,
                        CdcCommandOperation.Restart => CdcManagedLifecycleOperation.Restart,
                        CdcCommandOperation.Resume => CdcManagedLifecycleOperation.Resume,
                        CdcCommandOperation.Stop => CdcManagedLifecycleOperation.Stop,
                        _ => throw new ArgumentException("CDC command input is invalid."),
                    };
                    var lifecycle = await ConfigureManagedLifecycle(
                            new CdcManagedLifecycle(
                                invocation.StatePath,
                                setup,
                                templates,
                                kafka,
                                connect,
                                worker,
                                metrics,
                                [positions]
                            )
                        )
                        .ExecuteAsync(target, operation, token, ct);
                    return Result(lifecycle.Succeeded, lifecycle, lifecycle.Diagnostics);
            }

            CdcCommandResult Result(
                bool succeeded,
                object data,
                IReadOnlyList<CdcDeploymentDiagnostic> diagnostics
            ) =>
                new(
                    name,
                    succeeded,
                    succeeded ? 0 : 1,
                    diagnostics,
                    data,
                    request.Binding,
                    new(request.WorkerPolicy.AuthorizationProfile, AclIsolationProven: false)
                );
            CdcCommandResult StatusResult(CdcControllerStatusResult value) =>
                Result(
                    value.Aggregate.Readiness == CdcReadiness.Ready,
                    value,
                    value.Targets.SelectMany(t => t.Diagnostics).ToArray()
                );
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return CdcCommandHost.Failure(name, 1, component, CdcDeploymentFailure.Timeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CdcCommandEvidenceException exception)
        {
            return new(name, false, 1, exception.Diagnostics);
        }
        catch (Exception exception)
        {
            bool invalidInput =
                component == CdcDeploymentComponent.Request
                || exception is ArgumentException or JsonException or FormatException;
            return CdcCommandHost.Failure(
                name,
                invalidInput ? 2 : 1,
                invalidInput ? CdcDeploymentComponent.Request : component,
                invalidInput
                    ? CdcDeploymentFailure.InvalidInput
                    : CdcDeploymentDiagnostic.FromException(component, exception).Failure
            );
        }
    }

    private static T Require<T>(CdcTransportResult<T> result)
        where T : notnull
    {
        if (result is CdcTransportResult<T>.Observed observed)
        {
            return observed.Value;
        }
        var diagnostics =
            result.Diagnostics.Count > 0
                ? result.Diagnostics
                :
                [
                    new CdcDeploymentDiagnostic(
                        CdcDeploymentComponent.WorkflowState,
                        CdcDeploymentFailure.ValidationFailed
                    ),
                ];
        throw new CdcCommandEvidenceException(diagnostics);
    }

    public static async Task<CdcCommandAcknowledgement> ReadAcknowledgementAsync(
        CdcCommandInvocation invocation,
        CdcDeploymentRequest request,
        CancellationToken token
    )
    {
        if (!invocation.ConfirmConsumerCapacity)
        {
            throw new ArgumentException("CDC command input is invalid.");
        }
        var info = new FileInfo(invocation.AcknowledgementPath);
        if (info.Length > 1024 * 1024)
        {
            throw new ArgumentException("CDC command input is invalid.");
        }
        await using var file = info.OpenRead();
        var options = new JsonSerializerOptions(CdcCommandHost.JsonOptions)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            AllowDuplicateProperties = false,
        };
        var input =
            await JsonSerializer.DeserializeAsync<CdcCommandAcknowledgement>(file, options, token)
            ?? throw new ArgumentException("CDC command input is invalid.");
        if (
            input.BindingIdentity != request.Binding.ToCompleteBindingIdentity()
            || input.PreviousMaxRecordBytes != request.ConnectorPolicy.MaxRecordBytes
        )
        {
            throw new ArgumentException("CDC command input is invalid.");
        }
        return input;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Sonar",
        "S3871",
        Justification = "Private control-flow exception is caught inside RunAsync and never escapes this boundary."
    )]
    private sealed class CdcCommandEvidenceException(IReadOnlyList<CdcDeploymentDiagnostic> diagnostics)
        : Exception
    {
        internal IReadOnlyList<CdcDeploymentDiagnostic> Diagnostics { get; } = diagnostics;
    }
}

public sealed record CdcCommandAcknowledgement(
    Guid OperationId,
    CdcCompleteBindingIdentity BindingIdentity,
    int PreviousMaxRecordBytes,
    int RequestedMaxRecordBytes,
    int RequestedProducerBufferBytes,
    string OperatorIdentity,
    bool NoConsumers,
    CdcConsumerCapacityEvidence[] Consumers
);
