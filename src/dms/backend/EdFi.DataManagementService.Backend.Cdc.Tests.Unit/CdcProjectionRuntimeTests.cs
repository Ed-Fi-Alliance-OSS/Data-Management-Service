// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.DocumentCacheRuntime;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Serilog;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
public class Given_CdcProjectionRuntimeComposition
{
    private static readonly DocumentCacheTargetKey Target = DocumentCacheTargetKey.Create("TenantA", 7);

    [TestCase("postgresql")]
    [TestCase("mssql")]
    public async Task It_selects_only_the_configured_target_and_reuses_provider_administration_without_hosted_services(
        string datastore
    )
    {
        IServiceCollection services = Services(Configuration(datastore));
        services
            .OfType<ServiceDescriptor>()
            .Should()
            .NotContain(d => d.ServiceType == typeof(IHostedService));
        await using ServiceProvider provider = services.BuildServiceProvider();
        provider
            .GetRequiredService<IOptions<DocumentCacheOptions>>()
            .Value.GetTargetKeys()
            .Should()
            .Equal(Target);
        provider
            .GetRequiredService<DocumentCacheProcessProviderToken>()
            .ProviderToken.Should()
            .Be(
                datastore == "postgresql"
                    ? RelationalProviderToken.Postgresql
                    : RelationalProviderToken.SqlServer
            );
        provider
            .GetRequiredService<IDocumentCacheGuardedNewEmptyActivationCommand>()
            .Should()
            .BeOfType<DocumentCacheGuardedNewEmptyActivationCommand>();
        provider
            .GetRequiredService<IDocumentCacheAdministrativeMutex>()
            .GetType()
            .Name.Should()
            .Be(
                datastore == "postgresql"
                    ? "PostgresqlDocumentCacheAdministrativeMutex"
                    : "MssqlDocumentCacheAdministrativeMutex"
            );
    }

    [TestCase("Missing", "7")]
    [TestCase("TenantA", "8")]
    [TestCase(" TenantA", "7")]
    [TestCase("TenantA", "not-an-id")]
    public async Task It_rejects_missing_or_malformed_membership_before_schema_or_target_initialization(
        string tenant,
        string id
    )
    {
        IServiceCollection services = Services(Configuration("postgresql", tenant, id));
        IEffectiveSchemaBootstrapper bootstrapper = A.Fake<IEffectiveSchemaBootstrapper>();
        IDocumentCacheTargetRegistry registry = A.Fake<IDocumentCacheTargetRegistry>();
        services.Replace(ServiceDescriptor.Singleton(bootstrapper));
        services.Replace(ServiceDescriptor.Singleton(registry));
        ServiceProvider provider = services.BuildServiceProvider();
        var result = await CdcProjectionRuntimeFactory.OpenAsync(provider, Target, CancellationToken.None);
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
        A.CallTo(() => bootstrapper.InitializeAsync(A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => registry.RefreshAsync(A<DocumentCacheTargetRefreshReason>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        Action resolve = () => provider.GetRequiredService<IOptions<DocumentCacheOptions>>();
        resolve.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public async Task It_rejects_an_empty_configured_target_list()
    {
        var result = await CdcProjectionRuntimeFactory.CreateAsync(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?> { ["AppSettings:Datastore"] = "postgresql" }
                )
                .Build(),
            new LoggerConfiguration().CreateLogger(),
            Target,
            CancellationToken.None
        );
        result.State.Should().Be(CdcTransportEvidenceState.Unavailable);
    }

    [Test]
    public async Task It_initializes_schema_before_resolving_the_selected_target_and_does_not_start_processing()
    {
        IServiceCollection services = Services(Configuration("postgresql", "tenanta"));
        List<string> order = [];
        IEffectiveSchemaBootstrapper bootstrapper = A.Fake<IEffectiveSchemaBootstrapper>();
        A.CallTo(() => bootstrapper.InitializeAsync(A<CancellationToken>._))
            .Invokes(() => order.Add("schema"));
        IDocumentCacheTargetRegistry registry = A.Fake<IDocumentCacheTargetRegistry>();
        var context = Context();
        var snapshot = new DocumentCacheTargetRegistrySnapshot(
            [
                DocumentCacheTargetObservation.ResolvedEligible(
                    Target,
                    context.EffectiveSettings,
                    context.Generation,
                    context.ProviderToken,
                    context.PhysicalSourceFingerprint,
                    context.Lifecycle,
                    context.Inventory,
                    context.EnqueueTrigger,
                    context.SqlServerPrerequisites
                ),
            ],
            DateTimeOffset.UtcNow
        );
        A.CallTo(() =>
                registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup, A<CancellationToken>._)
            )
            .Invokes(() => order.Add("target"))
            .Returns(snapshot);
        A.CallTo(() => registry.CurrentRuntimeSnapshot)
            .Returns(new DocumentCacheTargetRuntimeSnapshot([context], DateTimeOffset.UtcNow));
        services.Replace(ServiceDescriptor.Singleton(bootstrapper));
        services.Replace(ServiceDescriptor.Singleton(registry));
        ServiceProvider provider = services.BuildServiceProvider();
        var result = await CdcProjectionRuntimeFactory.OpenAsync(provider, Target, CancellationToken.None);
        var runtime = result
            .Should()
            .BeOfType<CdcTransportResult<ICdcProjectionRuntime>.Observed>()
            .Subject.Value;
        await using (runtime)
        {
            order.Should().Equal("schema", "target");
            provider.GetRequiredService<DocumentCacheProjectionSupervisor>().ExecuteTask.Should().BeNull();
            provider
                .GetRequiredService<DocumentCacheProjectionSupervisor>()
                .CurrentTargetContexts.Should()
                .BeEmpty();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_rejects_unresolved_or_unexpected_registry_membership(bool unexpected)
    {
        IServiceCollection services = Services(Configuration("postgresql"));
        services.Replace(ServiceDescriptor.Singleton(A.Fake<IEffectiveSchemaBootstrapper>()));
        IDocumentCacheTargetRegistry registry = A.Fake<IDocumentCacheTargetRegistry>();
        A.CallTo(() =>
                registry.RefreshAsync(DocumentCacheTargetRefreshReason.Startup, A<CancellationToken>._)
            )
            .Returns(
                new DocumentCacheTargetRegistrySnapshot(
                    [
                        DocumentCacheTargetObservation.Configured(
                            unexpected ? DocumentCacheTargetKey.Create("Other", 9) : Target,
                            DocumentCacheTargetEffectiveSettings.FromOptions(new())
                        ),
                    ],
                    DateTimeOffset.UtcNow
                )
            );
        services.Replace(ServiceDescriptor.Singleton(registry));
        var result = await CdcProjectionRuntimeFactory.OpenAsync(
            services.BuildServiceProvider(),
            Target,
            CancellationToken.None
        );
        result
            .Should()
            .BeOfType<CdcTransportResult<ICdcProjectionRuntime>.Unavailable>()
            .Which.Diagnostic.Failure.Should()
            .Be(CdcDeploymentFailure.ValidationFailed);
    }

    [Test]
    public async Task It_preserves_cancellation_and_disposes_failed_initialization()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        ServiceProvider provider = Services(Configuration("postgresql")).BuildServiceProvider();
        Func<Task> open = () => CdcProjectionRuntimeFactory.OpenAsync(provider, Target, cancellation.Token);
        var failure = await open.Should().ThrowAsync<OperationCanceledException>();
        failure.Which.CancellationToken.Should().Be(cancellation.Token);
        Action resolve = () => provider.GetRequiredService<IDocumentCacheTargetRegistry>();
        resolve.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public async Task It_sanitizes_initialization_failures()
    {
        IServiceCollection services = Services(Configuration("postgresql"));
        IEffectiveSchemaBootstrapper bootstrapper = A.Fake<IEffectiveSchemaBootstrapper>();
        A.CallTo(() => bootstrapper.InitializeAsync(A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("Host=private;Password=secret"));
        services.Replace(ServiceDescriptor.Singleton(bootstrapper));
        var result = await CdcProjectionRuntimeFactory.OpenAsync(
            services.BuildServiceProvider(),
            Target,
            CancellationToken.None
        );
        JsonSerializer.Serialize(result).Should().NotContain("private").And.NotContain("secret");
        result
            .Should()
            .BeOfType<CdcTransportResult<ICdcProjectionRuntime>.Unavailable>()
            .Which.Diagnostic.Component.Should()
            .Be(CdcDeploymentComponent.Projection);
    }

    private static IServiceCollection Services(IConfiguration configuration)
    {
        IServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddDocumentCacheRuntimeServices(
            configuration,
            new LoggerConfiguration().CreateLogger(),
            Target,
            DocumentCacheRuntimeTargetSelection.RequireConfiguredMembership
        );
        return services;
    }

    private static IConfiguration Configuration(
        string datastore,
        string tenant = "TenantA",
        string id = "7"
    ) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:Datastore"] = datastore,
                    ["AppSettings:DefaultPartitionCount"] = "10",
                    ["ConfigurationServiceSettings:BaseUrl"] = "https://cms.example.org",
                    ["ConfigurationServiceSettings:ClientId"] = "client-id",
                    ["ConfigurationServiceSettings:ClientSecret"] = "client-secret",
                    ["ConfigurationServiceSettings:Scope"] = "scope",
                    ["ConfigurationServiceSettings:EncryptionKey"] =
                        "TestEncryptionKey123456789012345678901234567890",
                    ["DataManagement:DocumentCache:Targets:0:TenantKey"] = tenant,
                    ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = id,
                    ["DataManagement:DocumentCache:Targets:1:TenantKey"] = "Other",
                    ["DataManagement:DocumentCache:Targets:1:DataStoreId"] = "9",
                }
            )
            .Build();

    private static DocumentCacheTargetExecutionContext Context() =>
        new(
            Target,
            new(1),
            DocumentCacheTargetEffectiveSettings.FromOptions(new()),
            new(Target.DataStoreId, "postgresql"),
            new(RelationalProviderToken.Postgresql, "connection-not-to-be-opened"),
            new("sha256:" + new string('a', 64)),
            new(DocumentCacheLifecycleState.Disabled, false),
            new(DocumentCacheInventoryStatus.Satisfied, "Satisfied"),
            new(DocumentCacheEnqueueTriggerStatus.Satisfied, "Satisfied"),
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable()
        );
}

[TestFixture]
public class Given_CdcProjectionRuntimeLifetime
{
    private readonly DocumentCacheTargetKey _target = DocumentCacheTargetKey.Create("TenantA", 7);
    private ServiceProvider _provider = null!;
    private RecordingSupervisor _lifetime = null!;
    private IDocumentCacheProjectionSupervisor _supervisor = null!;
    private IDocumentCacheGuardedNewEmptyActivationCommand _activation = null!;
    private IDocumentCacheStatusService _status = null!;
    private CdcProjectionRuntime _runtime = null!;

    [SetUp]
    public void Setup()
    {
        _lifetime = new();
        _supervisor = A.Fake<IDocumentCacheProjectionSupervisor>();
        _activation = A.Fake<IDocumentCacheGuardedNewEmptyActivationCommand>();
        _status = A.Fake<IDocumentCacheStatusService>();
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton(_ => _lifetime);
        services.AddSingleton(_supervisor);
        services.AddSingleton(_activation);
        services.AddSingleton(_status);
        _provider = services.BuildServiceProvider();
        _ = _provider.GetRequiredService<RecordingSupervisor>();
        _runtime = new(_provider, _target, _lifetime);
    }

    [TearDown]
    public async Task Cleanup()
    {
        await _runtime.DisposeAsync();
        await _provider.DisposeAsync();
    }

    [Test]
    public async Task It_passes_the_exact_activation_request_and_cancellation_to_the_existing_guarded_command()
    {
        using CancellationTokenSource cancellation = new();
        DocumentCacheGuardedNewEmptyActivationRequest request = new(
            DocumentCacheAdministrativeTargetKey.FromTargetKey(_target)
        );
        A.CallTo(() => _supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup, cancellation.Token))
            .Invokes(() => _lifetime.Events.Add("refresh"));
        A.CallTo(() => _activation.ExecuteAsync(request, cancellation.Token))
            .Invokes(() => _lifetime.Events.Add("activate"));
        await _runtime.ActivateAsync(request, cancellation.Token);
        A.CallTo(() => _activation.ExecuteAsync(request, cancellation.Token)).MustHaveHappenedOnceExactly();
        _lifetime.Events.Should().Equal("refresh", "activate");
        _lifetime.StartCount.Should().Be(0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_does_not_activate_when_prerequisite_context_refresh_fails(bool canceled)
    {
        using CancellationTokenSource cancellation = new();
        Exception failure = canceled
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("Prerequisites unavailable");
        A.CallTo(() => _supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup, cancellation.Token))
            .ThrowsAsync(failure);
        Func<Task> activate = () =>
            _runtime.ActivateAsync(
                new(DocumentCacheAdministrativeTargetKey.FromTargetKey(_target)),
                cancellation.Token
            );
        (await activate.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        A.CallTo(() =>
                _activation.ExecuteAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        _lifetime.StartCount.Should().Be(0);
    }

    [Test]
    public async Task It_rejects_activation_for_another_target_without_mutation()
    {
        Func<Task> activate = () => _runtime.ActivateAsync(new(new("Other", 7)), CancellationToken.None);
        await activate.Should().ThrowAsync<ArgumentException>();
        A.CallTo(() =>
                _activation.ExecuteAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_uses_standalone_status_observation()
    {
        using CancellationTokenSource cancellation = new();
        await _runtime.ObserveAsync(cancellation.Token);
        A.CallTo(() =>
                _status.GetStatusAsync(
                    cancellation.Token,
                    DocumentCacheStatusEvaluationMode.StandaloneDirectObservation,
                    null
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_refreshes_before_start_and_stops_before_disposing_owned_services()
    {
        A.CallTo(() =>
                _supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup, A<CancellationToken>._)
            )
            .Invokes(() => _lifetime.Events.Add("refresh"));
        await _runtime.StartProcessingAsync(CancellationToken.None);
        await _runtime.DisposeAsync();
        _lifetime.Events.Should().Equal("refresh", "start", "stop", "dispose");
        _lifetime.CleanupToken.CanBeCanceled.Should().BeFalse();
    }

    [Test]
    public async Task It_preserves_the_initial_offline_guard_after_processing_starts()
    {
        await _runtime.StartProcessingAsync(CancellationToken.None);
        Func<Task> observe = () => _runtime.ObserveInitialDatabaseAsync(CancellationToken.None);
        await observe
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Initial eligibility requires an offline projection runtime.");
    }

    [Test]
    public async Task It_cleans_up_when_start_fails()
    {
        _lifetime.FailStart = true;
        Func<Task> start = () => _runtime.StartProcessingAsync(CancellationToken.None);
        await start.Should().ThrowAsync<InvalidOperationException>();
        _lifetime.Events.Should().Equal("start", "stop", "dispose");
    }

    [Test]
    public async Task It_cleans_up_when_context_refresh_is_canceled()
    {
        using CancellationTokenSource cancellation = new();
        A.CallTo(() => _supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.Startup, cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        Func<Task> start = () => _runtime.StartProcessingAsync(cancellation.Token);
        var failure = await start.Should().ThrowAsync<OperationCanceledException>();
        failure.Which.CancellationToken.Should().Be(cancellation.Token);
        _lifetime.Events.Should().Equal("stop", "dispose");
    }

    [Test]
    public async Task It_disposes_even_when_stop_fails()
    {
        _lifetime.FailStop = true;
        Func<Task> dispose = async () => await _runtime.DisposeAsync();
        await dispose.Should().ThrowAsync<InvalidOperationException>();
        _lifetime.Events.Should().Equal("stop", "dispose");
    }

    [Test]
    public async Task It_stops_an_unstarted_runtime_and_disposes_only_once()
    {
        await _runtime.DisposeAsync();
        await _runtime.DisposeAsync();
        _lifetime.Events.Should().Equal("stop", "dispose");
    }

    [Test]
    public async Task It_cancels_and_awaits_background_processing_before_disposal()
    {
        _lifetime.RunBackground = true;
        await _runtime.StartProcessingAsync(CancellationToken.None);
        await _lifetime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _runtime.DisposeAsync();
        _lifetime.ExecuteTask!.IsCompleted.Should().BeTrue();
        _lifetime.Events.Should().Equal("start", "stop", "dispose");
    }

    [Test]
    public async Task It_propagates_invocation_cancellation_to_background_processing()
    {
        using CancellationTokenSource cancellation = new();
        _lifetime.RunBackground = true;
        await _runtime.StartProcessingAsync(cancellation.Token);
        await _lifetime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        Func<Task> execution = () => _lifetime.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await execution.Should().ThrowAsync<OperationCanceledException>();
        await _runtime.DisposeAsync();
        _lifetime.Events.Should().Equal("start", "stop", "dispose");
    }

    private sealed class RecordingSupervisor : BackgroundService
    {
        public List<string> Events { get; } = [];
        public bool RunBackground { get; set; }
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailStart { get; set; }
        public bool FailStop { get; set; }
        public int StartCount { get; private set; }
        public CancellationToken CleanupToken { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            Events.Add("start");
            StartCount++;
            if (FailStart)
            {
                throw new InvalidOperationException("Start failed");
            }
            return RunBackground ? base.StartAsync(cancellationToken) : Task.CompletedTask;
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            Events.Add("stop");
            CleanupToken = cancellationToken;
            if (FailStop)
            {
                throw new InvalidOperationException("Stop failed");
            }
            return RunBackground ? base.StopAsync(cancellationToken) : Task.CompletedTask;
        }

        public override void Dispose()
        {
            Events.Add("dispose");
            base.Dispose();
        }
    }
}
