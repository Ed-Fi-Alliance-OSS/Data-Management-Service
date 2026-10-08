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

    [Test]
    public async Task It_records_bounded_topic_policy_evidence_without_exposing_supplied_text()
    {
        CdcKafkaTopicEvidence topic = new(
            "secret-topic",
            new Dictionary<int, IReadOnlyList<int>> { [0] = new[] { 1 } },
            new Dictionary<string, CdcKafkaConfigurationValue>
            {
                ["cleanup.policy"] = new("compact", true),
                ["min.insync.replicas"] = new("1", true),
                ["retention.ms"] = new("password=sentinel", true),
                ["secret-key"] = new("secret-value", true),
            }
        );
        var original = new CdcTransportResult<CdcKafkaTopicEvidence>.Observed(topic);
        for (int index = 0; index < 129; index++)
        {
            (
                await _hooks.InvokeAsync(
                    CdcControllerBoundary.Observation,
                    _ => Task.FromResult(original),
                    CancellationToken.None
                )
            )
                .Should()
                .BeSameAs(original);
        }
        await _hooks.InvokeAsync(
            CdcControllerBoundary.Observation,
            _ => Task.FromResult(new CdcTransportResult<CdcKafkaTopicEvidence>.Absent()),
            CancellationToken.None
        );
        await _hooks.InvokeAsync(
            CdcControllerBoundary.Observation,
            _ =>
                Task.FromResult(
                    new CdcTransportResult<CdcKafkaTopicEvidence>.Unavailable(
                        new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.Unavailable)
                    )
                ),
            CancellationToken.None
        );
        _hooks.KafkaTopics.Should().HaveCount(128);
        string text = System.Text.Json.JsonSerializer.Serialize(_hooks.KafkaTopics);
        text.Should().NotContain("secret").And.NotContain("sentinel");
        using var document = System.Text.Json.JsonDocument.Parse(text);
        document.RootElement[126].GetProperty("State").GetString().Should().Be("Absent");
        document.RootElement[127].GetProperty("State").GetString().Should().Be("Unavailable");
        var configuration = document.RootElement[0].GetProperty("Configuration");
        configuration[0].GetProperty("Compact").GetBoolean().Should().BeTrue();
        configuration[1].GetProperty("Number").GetInt64().Should().Be(1);
        configuration[1].GetProperty("IsTopicOverride").GetBoolean().Should().BeTrue();
        configuration[4].GetProperty("Numeric").GetBoolean().Should().BeFalse();
    }

    [TestCase("-1", true, true, true, false)]
    [TestCase("-2", true, true, false, false)]
    [TestCase("5", true, false, false, false)]
    [TestCase("NaN", false, false, false, false)]
    [TestCase("9223372036854775808", true, false, false, true)]
    public async Task It_preserves_the_consumed_metrics_response_and_records_only_numeric_classification(
        string value,
        bool finite,
        bool negative,
        bool uninitialized,
        bool overflow
    )
    {
        string body =
            "# TYPE edfi_cdc_source_lag_current_milliseconds gauge\n"
            + "edfi_cdc_source_lag_current_milliseconds{connector=\"secret-source\",provider=\"postgres\"} "
            + value
            + "\n";
        using var evidence = new CdcControllerFixtureMetrics(new MetricsResponse(body));
        using var client = new HttpClient(evidence);
        (await client.GetStringAsync("http://fixture/metrics")).Should().Be(body);
        string text = System.Text.Json.JsonSerializer.Serialize(evidence.Observations);
        text.Should().NotContain("secret-source").And.NotContain("postgres");
        using var document = System.Text.Json.JsonDocument.Parse(text);
        var observation = document.RootElement[0];
        observation.GetProperty("GaugeTypeCount").GetInt32().Should().Be(1);
        observation.GetProperty("SampleCount").GetInt32().Should().Be(1);
        var sample = observation.GetProperty("Samples")[0];
        sample.GetProperty("Finite").GetBoolean().Should().Be(finite);
        sample.GetProperty("Negative").GetBoolean().Should().Be(negative);
        sample.GetProperty("Uninitialized").GetBoolean().Should().Be(uninitialized);
        sample.GetProperty("Overflow").GetBoolean().Should().Be(overflow);
    }

    [Test]
    public void It_bounds_metric_evidence_and_distinguishes_an_absent_current_sample()
    {
        using var evidence = new CdcControllerFixtureMetrics();
        for (int index = 0; index < 129; index++)
        {
            evidence.Record("private-unrelated-metric{source=\"secret\"} 0");
        }
        evidence.Observations.Should().HaveCount(128);
        string text = System.Text.Json.JsonSerializer.Serialize(evidence.Observations);
        text.Should().NotContain("private").And.NotContain("secret");
        using var document = System.Text.Json.JsonDocument.Parse(text);
        document.RootElement[0].GetProperty("SampleCount").GetInt32().Should().Be(0);
    }

    [TestCase(CdcConnectOffsetState.AwaitingStreaming)]
    [TestCase(CdcConnectOffsetState.Malformed)]
    public async Task It_retains_bounded_parsed_offset_states_without_source_or_position_values(
        CdcConnectOffsetState state
    )
    {
        var observed = new CdcTransportResult<CdcConnectOffsetEvidence>.Observed(
            new(
                state,
                "private-source-hash",
                new(CoreCdc.CdcConnectorOffsetMatchResult.Exact, false, false, 123),
                new(
                    CoreCdc.CdcConnectorOffsetMatchResult.Exact,
                    false,
                    false,
                    "private-commit",
                    "private-change",
                    7
                )
            )
        );
        for (int index = 0; index < 130; index++)
        {
            (
                await _hooks.InvokeAsync(
                    CdcControllerBoundary.Observation,
                    _ => Task.FromResult(observed),
                    CancellationToken.None
                )
            )
                .Should()
                .BeSameAs(observed);
        }
        await _hooks.InvokeAsync(
            CdcControllerBoundary.Observation,
            _ =>
                Task.FromResult(
                    new CdcTransportResult<CdcConnectOffsetEvidence>.Unavailable(
                        new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable)
                    )
                ),
            CancellationToken.None
        );
        await _hooks.InvokeAsync(
            CdcControllerBoundary.Observation,
            _ => Task.FromResult(new CdcTransportResult<CdcConnectOffsetEvidence>.Absent()),
            CancellationToken.None
        );
        _hooks.OffsetObservations.Should().HaveCount(128);
        string text = System.Text.Json.JsonSerializer.Serialize(_hooks.OffsetObservations);
        text.Should().NotContain("private").And.NotContain("CommitLsn").And.NotContain("LsnProc");
        using var document = System.Text.Json.JsonDocument.Parse(text);
        document.RootElement[125].GetProperty("State").GetString().Should().Be(state.ToString());
        document.RootElement[126].GetProperty("State").GetString().Should().Be("Unavailable");
        document.RootElement[127].GetProperty("State").GetString().Should().Be("Absent");
    }

    private sealed class MetricsResponse(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) }
            );
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
                Func<Task> act = () =>
                    session.CreateAsync(
                        workflow,
                        target,
                        CancellationToken.None,
                        CdcWorkflowPurpose.InitialCdcProvisioning
                    );
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
                        kafkaConfig.BootstrapServers,
                        fixture.Resources.KafkaBootstrapServers
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
            await session.CreateAsync(
                Guid.NewGuid(),
                request.TargetIdentity,
                token,
                CdcWorkflowPurpose.InitialCdcProvisioning
            );
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
