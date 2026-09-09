// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Confluent.Kafka;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
public sealed class Given_CdcControllerFixtureHooks
{
    private CdcControllerFixtureHooks _hooks = null!;

    [SetUp]
    public void Setup() => _hooks = new();

    [TestCaseSource(nameof(Boundaries))]
    public async Task It_can_interrupt_before_or_lose_a_reply_after_each_real_effect(
        CdcControllerBoundary boundary
    )
    {
        int effects = 0;
        _hooks.OnBoundary = e =>
        {
            if (e.Edge == CdcControllerEdge.Before)
            {
                throw new IOException("secret-source");
            }
        };
        Func<Task> before = () =>
            _hooks.InvokeAsync(boundary, _ => Task.FromResult(++effects), CancellationToken.None);
        await before.Should().ThrowAsync<IOException>();
        effects.Should().Be(0);
        _hooks.OnBoundary = e =>
        {
            if (e.Edge == CdcControllerEdge.After)
            {
                throw new IOException("secret-source");
            }
        };
        await before.Should().ThrowAsync<IOException>();
        effects.Should().Be(1);
        _hooks.OnBoundary = _ => { };
        (await beforeResult()).Should().Be(2);
        _hooks.SerializeTrace().Should().NotContain("secret-source");
        Task<int> beforeResult() =>
            _hooks.InvokeAsync(boundary, _ => Task.FromResult(++effects), CancellationToken.None);
    }

    private static IEnumerable<CdcControllerBoundary> Boundaries() => Enum.GetValues<CdcControllerBoundary>();

    [Test]
    public async Task It_decorates_real_Task_and_ValueTask_calls_and_preserves_exception_identity()
    {
        var original = new OperationCanceledException("password=sentinel;Host=private-source");
        var inner = new AsyncBoundary(original);
        var decorated = _hooks.Decorate<IAsyncBoundary>(inner, _ => CdcControllerBoundary.Activation);
        (await decorated.ReadAsync(CancellationToken.None)).Should().Be(42);
        await decorated.WriteAsync(CancellationToken.None);
        await decorated.DisposeAsync();
        Func<Task> act = () => decorated.FailAsync(CancellationToken.None);
        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(original);
        inner.Effects.Should().Be(2);
        _hooks
            .Trace.Select(e => e.Edge)
            .Should()
            .Equal(
                CdcControllerEdge.Before,
                CdcControllerEdge.After,
                CdcControllerEdge.Before,
                CdcControllerEdge.After,
                CdcControllerEdge.Before,
                CdcControllerEdge.After,
                CdcControllerEdge.Before,
                CdcControllerEdge.Failed
            );
        _hooks.SerializeTrace().Should().NotContain("sentinel").And.NotContain("private-source");
    }

    [TestCase(CdcControllerEdge.BeforeTemporaryWrite, false)]
    [TestCase(CdcControllerEdge.AfterTemporaryFlush, false)]
    [TestCase(CdcControllerEdge.AfterAtomicReplacement, true)]
    public async Task It_reopens_the_same_durable_state_after_a_journal_write_interruption(
        CdcControllerEdge edge,
        bool committed
    )
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "cdc-controller-journal-" + Guid.NewGuid().ToString("N")
        );
        var target = new CoreCdc.CdcTargetIdentity(
            "local",
            "default",
            "1",
            "instance",
            1,
            CoreCdc.CdcProvider.Postgresql
        );
        var workflow = Guid.NewGuid();
        try
        {
            _hooks.OnBoundary = e =>
            {
                if (e.Edge == edge)
                {
                    throw new IOException("injected");
                }
            };
            await using (
                var session = await _hooks
                    .CreateJournalStore(root)
                    .AcquireAsync(
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromMilliseconds(10),
                        CancellationToken.None
                    )
            )
            {
                Func<Task> act = () => session.CreateAsync(workflow, target, CancellationToken.None);
                await act.Should().ThrowAsync<CdcWorkflowStateException>();
            }
            await using var reopened = await new LocalCdcWorkflowJournalStore(root).AcquireAsync(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None
            );
            if (committed)
            {
                (await reopened.ReadAsync(target, CancellationToken.None)).WorkflowId.Should().Be(workflow);
            }
            else
            {
                Func<Task> read = () => reopened.ReadAsync(target, CancellationToken.None);
                await read.Should().ThrowAsync<CdcWorkflowStateException>();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    public interface IAsyncBoundary : IAsyncDisposable
    {
        Task<int> ReadAsync(CancellationToken token);
        Task WriteAsync(CancellationToken token);
        Task FailAsync(CancellationToken token);
    }

    private sealed class AsyncBoundary(Exception exception) : IAsyncBoundary
    {
        public int Effects { get; private set; }

        public Task<int> ReadAsync(CancellationToken token) => Task.FromResult(42);

        public Task WriteAsync(CancellationToken token)
        {
            Effects++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Effects++;
            return ValueTask.CompletedTask;
        }

        public Task FailAsync(CancellationToken token) => throw exception;
    }
}

[TestFixture]
public sealed class Given_CdcControllerFixtureWaits
{
    [Test]
    public async Task It_polls_until_the_observation_changes()
    {
        int calls = 0;
        await CdcControllerFixture.WaitAsync(
            _ => Task.FromResult(++calls == 3),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None
        );
        calls.Should().Be(3);
    }

    [Test]
    public async Task It_bounds_a_probe_that_never_completes()
    {
        var pending = new TaskCompletionSource<bool>();
        Func<Task> act = () =>
            CdcControllerFixture.WaitAsync(
                _ => pending.Task,
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(1),
                CancellationToken.None
            );
        await act.Should()
            .ThrowAsync<TimeoutException>()
            .WithMessage("Controller fixture observation deadline expired.");
        pending.SetResult(false);
    }

    [Test]
    public async Task It_preserves_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Func<Task> act = () =>
            CdcControllerFixture.WaitAsync(
                ct => Task.FromCanceled<bool>(ct),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(1),
                cancellation.Token
            );
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

[TestFixture]
public sealed class Given_CdcControllerFixturePrerequisites
{
    [TestCase("", "broker", "provider")]
    [TestCase("unqualified@sha256:bad", "broker", "provider")]
    [TestCase("qualified", "", "provider")]
    [TestCase("qualified", "broker", "")]
    public void It_fails_missing_or_unqualified_inputs_without_skipping(
        string image,
        string broker,
        string provider
    )
    {
        var settings = new CdcConnectorTemplateSmokeSettings(
            image == "qualified" ? CdcQualifiedWorkerImage.Image : image,
            broker,
            provider,
            true,
            false
        );
        Action act = () =>
        {
            using var isolated = new NUnit.Framework.Internal.TestExecutionContext.IsolatedContext();
            CdcControllerFixture.ValidatePrerequisites(settings, CdcProvider.Postgresql);
        };
        act.Should().Throw<AssertionException>().Which.Message.Should().NotContain("sha256:bad");
    }

    [TestCase("Docker")]
    [TestCase("provider")]
    [TestCase("broker")]
    [TestCase("metrics")]
    public async Task It_promotes_unavailable_live_prerequisites_to_failures_without_raw_diagnostics(
        string component
    )
    {
        var settings = new CdcConnectorTemplateSmokeSettings(
            CdcQualifiedWorkerImage.Image,
            "broker",
            "provider",
            true,
            false
        );
        Func<Task> act = async () =>
        {
            using var isolated = new NUnit.Framework.Internal.TestExecutionContext.IsolatedContext();
            await settings.StopOnPrerequisiteFailureAsync(
                CdcProvider.Postgresql,
                Task.FromException(new IOException("password=sentinel;Host=private-source")),
                component + " unavailable."
            );
        };
        (await act.Should().ThrowAsync<AssertionException>())
            .Which.Message.Should()
            .NotContain("sentinel")
            .And.NotContain("private-source");
    }
}

// Infrastructure smoke only. These do not claim admission, continuity, ACL, or record-size qualification.
[TestFixture(CdcProvider.Postgresql, Category = "PostgresqlIntegration")]
[TestFixture(CdcProvider.SqlServer, Category = "MssqlIntegration")]
[Category("CdcControllerFixture")]
[Category("CdcAuthorizationDisabledLocal")]
[Category("DatabaseIntegration")]
[NonParallelizable]
public sealed class Given_CdcControllerFixtureLive(CdcProvider provider)
{
    [Test]
    public async Task It_invokes_live_transports_and_recovers_the_worker_with_retained_state()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = timeout.Token;
        string preWorkerRoot = string.Empty;
        Uri preWorkerConnect = null!;
        Uri preWorkerMetrics = null!;
        await using var fixture = await CdcControllerFixture.StartAsync(
            provider,
            token,
            async (preparing, ct) =>
            {
                preWorkerRoot = preparing.StateRoot;
                preWorkerConnect = preparing.ConnectEndpoint;
                preWorkerMetrics = await preparing.MetricsEndpointAsync(ct);
                var bindings = await preparing.Bindings.ListBindingsAsync("dms", ct);
                bindings.Status.Should().Be(CoreCdc.CdcControlPlaneOperationStatus.Succeeded);
                bindings.States.Should().BeEmpty();
            }
        );
        fixture.StateRoot.Should().Be(preWorkerRoot);
        fixture.ConnectEndpoint.Should().Be(preWorkerConnect);
        (await fixture.MetricsEndpointAsync(token)).Should().Be(preWorkerMetrics);
        var template = await fixture.Resources.CreateRequestAsync(token);
        await fixture.Resources.RegisterRenderedConnectorConfigDirectlyAsync(
            fixture.Resources.Render(template),
            token
        );
        await fixture.Resources.AssertHeartbeatAndCommittedOffsetProgressAsync(template, token);
        var request = await fixture.Resources.CreateControllerSmokeObservationRequestAsync(template, token);
        var registrations = new ServiceCollection()
            .AddLogging()
            .AddSingleton<Serilog.ILogger>(_ => new Serilog.LoggerConfiguration().CreateLogger())
            .AddCdcProviderSetup()
            .AddCdcConnectorTemplates();
        if (provider == CdcProvider.Postgresql)
        {
            registrations.AddPostgresqlDmsCdcControlPlane();
        }
        else
        {
            registrations.AddMssqlDmsCdcControlPlane();
        }
        await using var services = registrations.BuildServiceProvider();
        var kafkaConfig = new AdminClientConfig
        {
            BootstrapServers = fixture.Resources.ControllerKafkaBootstrapServers,
        };
        using (
            var broker = new AdminClientBuilder(kafkaConfig)
                .SetLogHandler((_, _) => { })
                .SetErrorHandler((_, _) => { })
                .Build()
        )
        {
            broker.GetMetadata(TimeSpan.FromSeconds(10)).Brokers.Should().ContainSingle();
        }
        using var kafka = (
            (CdcTransportResult<CdcKafkaAdminAdapter>.Observed)
                CdcKafkaAdminAdapter.Create(
                    kafkaConfig,
                    new CdcComposeKafkaAuthorizationInspection(
                        fixture.Resources.ControllerProject,
                        kafkaConfig.BootstrapServers
                    )
                )
        ).Value;
        var controllers = new CdcControllerFixtureControllers(
            fixture,
            services.GetRequiredService<ICdcProviderSetupService>(),
            services.GetRequiredService<ICdcConnectorTemplateService>(),
            kafka,
            services
                .GetServices<CoreCdc.ICdcProviderSourcePositionAdapter>()
                .Single(p => p.Provider == request.Binding.Provider)
        );
        await using (var connection = await fixture.OpenAdminConnectionAsync(token))
        {
            await connection.ChangeDatabaseAsync(
                request.ProviderConnectionProperties.Properties[
                    provider == CdcProvider.Postgresql ? "database.dbname" : "database.names"
                ],
                token
            );
            var cleanup = new CdcProviderArtifactCleanupAdapter(
                provider == CdcProvider.Postgresql
                    ? Npgsql.NpgsqlFactory.Instance
                    : Microsoft.Data.SqlClient.SqlClientFactory.Instance,
                connection.ConnectionString
            );
            // A real controller invocation must not treat isolated fixture resources as ownership proof.
            var rejected = await fixture.InvokeAsync(
                CdcControllerBoundary.Cleanup,
                ct =>
                    controllers
                        .Retirement(kafka, cleanup)
                        .RetireAsync(request, request.Binding.Generation, true, ct),
                token
            );
            rejected.Succeeded.Should().BeFalse();
            fixture.Hooks.Trace.Count(e => e.Boundary == CdcControllerBoundary.Cleanup).Should().Be(2);
        }
        var before = await fixture.Worker.InspectAsync(request, token);
        before.State.Should().Be(CdcTransportEvidenceState.Observed);
        using (var pass = new CdcTelemetryObservationPass(request, "fixture-smoke", long.MaxValue))
        {
            (await fixture.Metrics.CollectAsync(request, pass, token))
                .State.Should()
                .Be(CdcTransportEvidenceState.Observed);
        }

        await using (
            var session = await fixture
                .CreateJournalStore()
                .AcquireAsync(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(10), token)
        )
        {
            await session.CreateAsync(Guid.NewGuid(), request.TargetIdentity, token);
        }

        // Exercise a real successful external stop with a lost reply; independently reconcile REST.
        fixture.Hooks.OnBoundary = e =>
        {
            if (e.Boundary == CdcControllerBoundary.Observation && e.Edge == CdcControllerEdge.After)
            {
                throw new IOException("lost reply");
            }
        };
        Func<Task> stop = () => fixture.Connect.StopAsync(request, token);
        await stop.Should().ThrowAsync<IOException>();
        fixture.Hooks.OnBoundary = _ => { };
        await WaitStoppedAsync();
        var offsets = await fixture.Connect.ReadOffsetEvidenceAsync(request, token);
        offsets.State.Should().Be(CdcTransportEvidenceState.Observed);
        await fixture.RecoverWorkerAsync(crash: true, token);
        await WaitStoppedAsync();
        (await fixture.Connect.ReadOffsetEvidenceAsync(request, token)).Should().BeEquivalentTo(offsets);
        var after = await fixture.Worker.InspectAsync(request, token);
        after.State.Should().Be(CdcTransportEvidenceState.Observed);
        ((CdcTransportResult<CdcWorkerInspection>.Observed)after)
            .Value.ProcessIdentity.Should()
            .NotBe(((CdcTransportResult<CdcWorkerInspection>.Observed)before).Value.ProcessIdentity);
        await using (
            var reopened = await new LocalCdcWorkflowJournalStore(fixture.StateRoot).AcquireAsync(
                TimeSpan.FromSeconds(3),
                TimeSpan.FromMilliseconds(10),
                token
            )
        )
        {
            (await reopened.ReadAsync(request.TargetIdentity, token)).Operations.Should().BeEmpty();
        }
        // This raw resume tests the fixture control, not controller-managed authorization.
        (await fixture.Connect.ResumeAsync(request, token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Observed);
        await fixture.Resources.AssertHeartbeatAndCommittedOffsetProgressAsync(template, token);

        Task WaitStoppedAsync() =>
            CdcControllerFixture.WaitAsync(
                async ct =>
                    await fixture.Connect.ReadStatusAsync(request, ct)
                        is CdcTransportResult<CdcConnectStatus>.Observed status
                    && status.Value.Runtime.ConnectorState == CoreCdc.CdcConnectorRuntimeState.Stopped
                    && status.Value.Tasks.Count == 0,
                TimeSpan.FromSeconds(60),
                TimeSpan.FromMilliseconds(250),
                token
            );
    }
}
