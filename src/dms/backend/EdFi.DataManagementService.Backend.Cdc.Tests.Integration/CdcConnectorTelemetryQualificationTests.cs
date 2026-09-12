// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql, Category = "PostgresqlIntegration")]
[TestFixture(CdcProvider.SqlServer, Category = "MssqlIntegration")]
[Category("CdcConnectorTelemetryQualification")]
[Category("DatabaseIntegration")]
public sealed class Given_CdcConnectorTelemetryQualification(CdcProvider provider)
{
    [Test]
    public async Task It_qualifies_the_pinned_exporter_with_real_streaming_and_replaces_task_and_worker_metrics()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await using var fixture = await CdcConnectorTemplatePinnedImageFixture.StartAsync(
            provider,
            timeout.Token
        );
        var request = await fixture.CreateRequestAsync(timeout.Token);
        var rendered = fixture.Render(request);
        await fixture.AssertRuntimeLoadsRequiredClassesAsync(rendered, timeout.Token);
        await fixture.RegisterRenderedConnectorConfigDirectlyAsync(rendered, timeout.Token);
        await fixture.AssertHeartbeatAndCommittedOffsetProgressAsync(request, timeout.Token);
        await using var peer = await fixture.StartTelemetryPeerAsync(timeout.Token);
        var peerRequest = peer.BuildTelemetryPeerRequest();
        await fixture.QualifyTelemetryAsync(request, peer, peerRequest, timeout.Token);
    }
}

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    public async Task<CdcConnectorTemplatePinnedImageFixture> StartTelemetryPeerAsync(CancellationToken token)
    {
        var peer = new CdcConnectorTemplatePinnedImageFixture(
            Provider,
            _settings,
            new Uri("http://127.0.0.1:8083"),
            "dms-cdc-template-" + Guid.NewGuid().ToString("N"),
            _docker
        );
        try
        {
            await _docker.RunAsync(["network", "create", peer.NetworkName], token);
            await peer.StartProviderAsync(token);
            await peer.CreateMinimalProviderObjectsAsync(token);
            await peer.AssertSqlServer2025Async(token);
            peer._telemetrySetup = await peer.RunProviderSetupAsync(
                CdcProviderSetupMode.InitialCreateOrExactMatch,
                token
            );
            return peer;
        }
        catch
        {
            await peer.DisposeAsync();
            throw;
        }
    }

    private CdcProviderSetupResult _telemetrySetup = null!;

    public CdcConnectorTemplateRequest BuildTelemetryPeerRequest() => BuildRequest(_telemetrySetup);

    public async Task QualifyTelemetryAsync(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplatePinnedImageFixture peer,
        CdcConnectorTemplateRequest peerRequest,
        CancellationToken token
    )
    {
        // Independent physical source, same worker: stopping one connector must remove only its beans.
        await _docker.RunAsync(["network", "connect", NetworkName, peer.ProviderContainerName], token);
        string peerName = request.ConnectorName.Value + "-peer";
        var peerConfig = new Dictionary<string, string>(peer.Render(peerRequest).Config)
        {
            ["name"] = peerName,
            ["topic.prefix"] = peerName,
            ["transforms.documentState.target.topic"] = request.PublicTopicName + ".peer",
            ["transforms.documentState.progress.topic"] = request.PublicTopicName + ".peer.cdc-progress",
        };
        List<string> peerTopics =
        [
            request.PublicTopicName + ".peer",
            request.PublicTopicName + ".peer.cdc-progress",
        ];
        if (Provider == CdcProvider.SqlServer)
        {
            peerConfig["schema.history.internal.kafka.bootstrap.servers"] = KafkaBootstrapServers;
            peerConfig["schema.history.internal.kafka.topic"] = request.SchemaHistoryTopicName + ".peer";
            peerTopics.Add(request.SchemaHistoryTopicName + ".peer");
        }
        await _docker.RunAsync(
            [
                "exec",
                BrokerContainerName,
                "rpk",
                "topic",
                "create",
                "--if-not-exists",
                .. peerTopics,
                "--brokers",
                KafkaBootstrapServers,
            ],
            token
        );
        using var created = await _httpClient.PostAsJsonAsync(
            "/connectors",
            new CdcKafkaConnectRegistrationPayload(new(peerName), peerConfig),
            token
        );
        created.EnsureSuccessStatusCode();
        await WaitForRegisteredConnectorRunningAsync(peerName, token);
        using var metrics = await CreateMetricsClientAsync(token);
        await WaitForTelemetryAsync(
            metrics,
            text => CdcTelemetryQualification.HasCurrentLag(text, peerName),
            token
        );
        string initial = await metrics.GetStringAsync("/metrics", token);
        CdcTelemetryQualification.AssertStreamingMetrics(initial, Provider, request.ConnectorName.Value);
        CdcTelemetryQualification.AssertStreamingMetrics(initial, Provider, peerName);
        string processIdentity = await QualifyWorkerDeploymentAsync(request, metrics, token);
        double start = CdcTelemetryQualification.Scalar(initial, "edfi_cdc_worker_start_time_seconds");
        start.Should().BeGreaterThan(0);
        CdcTelemetryQualification.Scalar(initial, "edfi_cdc_worker_heap_max_bytes").Should().BeGreaterThan(0);

        using var stopped = await _httpClient.PutAsync(
            $"/connectors/{request.ConnectorName.Value}/stop",
            null,
            token
        );
        stopped.EnsureSuccessStatusCode();
        await WaitForTelemetryAsync(
            metrics,
            text => !CdcTelemetryQualification.HasConnector(text, request.ConnectorName.Value),
            token
        );
        CdcTelemetryQualification.AssertStreamingMetrics(
            await metrics.GetStringAsync("/metrics", token),
            Provider,
            peerName
        );
        using var resumed = await _httpClient.PutAsync(
            $"/connectors/{request.ConnectorName.Value}/resume",
            null,
            token
        );
        resumed.EnsureSuccessStatusCode();
        await AssertHeartbeatAndCommittedOffsetProgressAsync(request, token);
        string restartedTask = await metrics.GetStringAsync("/metrics", token);
        CdcTelemetryQualification.AssertStreamingMetrics(
            restartedTask,
            Provider,
            request.ConnectorName.Value
        );
        CdcTelemetryQualification
            .Scalar(restartedTask, "edfi_cdc_worker_start_time_seconds")
            .Should()
            .Be(start);

        await _docker.RunAsync(["restart", ConnectContainerName], token);
        // Docker may assign new ephemeral host ports on restart.
        _httpClient.Dispose();
        _httpClient = new HttpClient { BaseAddress = await ReadMappedConnectBaseUriAsync(token) };
        using var restartedMetrics = await CreateMetricsClientAsync(token);
        await WaitForKafkaConnectAsync(token);
        await AssertHeartbeatAndCommittedOffsetProgressAsync(request, token);
        await WaitForTelemetryAsync(
            restartedMetrics,
            text => CdcTelemetryQualification.HasCurrentLag(text, peerName),
            token
        );
        string restartedWorker = await restartedMetrics.GetStringAsync("/metrics", token);
        CdcTelemetryQualification.AssertStreamingMetrics(
            restartedWorker,
            Provider,
            request.ConnectorName.Value
        );
        CdcTelemetryQualification.AssertStreamingMetrics(restartedWorker, Provider, peerName);
        (await QualifyWorkerDeploymentAsync(request, restartedMetrics, token))
            .Should()
            .NotBe(processIdentity);
        CdcTelemetryQualification
            .Scalar(restartedWorker, "edfi_cdc_worker_start_time_seconds")
            .Should()
            .BeGreaterThan(start);
    }

    private async Task<string> QualifyWorkerDeploymentAsync(
        CdcConnectorTemplateRequest template,
        HttpClient metrics,
        CancellationToken token
    )
    {
        string digest = _settings.ConnectImage[(_settings.ConnectImage.IndexOf('@') + 1)..];
        // Inspection and telemetry correlation must describe this live connector and broker.
        var request = await CreateControllerSmokeObservationRequestAsync(template, token);
        var inspector = new CdcWorkerDeployment(
            _resourcePrefix,
            "kafka-cdc-worker",
            new HashSet<string> { digest }
        );
        var result = await inspector.InspectAsync(request, token);
        result
            .State.Should()
            .Be(
                CdcTransportEvidenceState.Observed,
                "the real Docker worker must satisfy the local deployment inspection contract"
            );
        var worker = ((CdcTransportResult<CdcWorkerInspection>.Observed)result).Value;
        worker.EffectiveConfiguration["bootstrap.servers"].Should().Be(KafkaBootstrapServers);
        var status = await _httpClient.GetFromJsonAsync<JsonElement>(
            $"/connectors/{BuildBinding(Provider).ConnectorName}/status",
            token
        );
        worker
            .ConnectWorkerId.Should()
            .Be(status.GetProperty("tasks").EnumerateArray().Single().GetProperty("worker_id").GetString());
        // Exercise the production T15 HTTP/parser/status/deployment path against this qualified image.
        using var telemetryPass = new CdcTelemetryObservationPass(
            request,
            "telemetry-qualification",
            long.MaxValue
        );
        var telemetry = new CdcConnectorTelemetryAdapter(
            metrics,
            new CdcConnectRestAdapter(_httpClient),
            inspector
        );
        var collected = await telemetry.CollectAsync(request, telemetryPass, token);
        collected
            .State.Should()
            .Be(
                CdcTransportEvidenceState.Observed,
                "current lag must survive production parsing and live worker/task correlation: {0}",
                string.Join(",", collected.Diagnostics)
            );
        var observation = (
            (CdcTransportResult<CdcConnectorTelemetryObservation>.Observed)collected
        ).Value.ReadForEvaluation(telemetryPass);
        observation.LagState.Should().Be(Core.DocumentCache.Cdc.CdcConnectorLagState.WithinThreshold);
        observation.CurrentLagMilliseconds.Should().BeGreaterThanOrEqualTo(0);
        return worker.ProcessIdentity;
    }

    private async Task<HttpClient> CreateMetricsClientAsync(CancellationToken token)
    {
        var port = await _docker.RunAsync(["port", ConnectContainerName, "9404/tcp"], token);
        return new HttpClient
        {
            BaseAddress = new Uri(
                $"http://127.0.0.1:{ParseMappedPort(ConnectContainerName, port.StandardOutput, "metrics")}"
            ),
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    private static async Task WaitForTelemetryAsync(
        HttpClient client,
        Func<string, bool> predicate,
        CancellationToken token
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (!predicate(await client.GetStringAsync("/metrics", timeout.Token)))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
        }
    }
}

/// <summary>
/// Reusable assertions for the image contract; current lag is required for both providers.
/// Historical statistics are optional diagnostics, but exported values must have valid types and units.
/// </summary>
internal static class CdcTelemetryQualification
{
    public static bool HasCurrentLag(string text, string connector) =>
        Array.Exists(
            text.Split('\n'),
            line =>
                line.StartsWith("edfi_cdc_source_lag_current_milliseconds{", StringComparison.Ordinal)
                && line.Contains($"connector=\"{connector}\"", StringComparison.Ordinal)
                && double.TryParse(
                    line[(line.LastIndexOf('}') + 1)..],
                    CultureInfo.InvariantCulture,
                    out double value
                )
                && double.IsFinite(value)
                && value >= 0
        );

    public static bool HasConnector(string text, string connector) =>
        text.Contains($"connector=\"{connector}\"", StringComparison.Ordinal);

    public static void AssertStreamingMetrics(string text, CdcProvider provider, string connector)
    {
        string providerLabel = provider == CdcProvider.Postgresql ? "postgres" : "sql_server";
        foreach (string statistic in new[] { "current", "min", "max", "average", "p50", "p95", "p99" })
        {
            string name = $"edfi_cdc_source_lag_{statistic}_milliseconds";
            var samples = text.Split('\n')
                .Where(line =>
                    line.StartsWith(name + "{", StringComparison.Ordinal)
                    && line.Contains($"connector=\"{connector}\"", StringComparison.Ordinal)
                )
                .ToArray();
            if (statistic != "current" && samples.Length == 0)
            {
                continue;
            }

            text.Split('\n').Should().Contain($"# TYPE {name} gauge");
            samples.Should().ContainSingle();
            samples.Single().Should().Contain($"provider=\"{providerLabel}\"");
            double value = double.Parse(
                samples.Single()[(samples.Single().LastIndexOf('}') + 1)..],
                CultureInfo.InvariantCulture
            );
            double.IsFinite(value).Should().BeTrue();
            value.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    public static double Scalar(string text, string name)
    {
        text.Split('\n').Should().Contain($"# TYPE {name} gauge");
        string sample = text.Split('\n')
            .Single(line => line.StartsWith(name + " ", StringComparison.Ordinal));
        return double.Parse(sample[(name.Length + 1)..], CultureInfo.InvariantCulture);
    }
}

[TestFixture]
public sealed class Given_CdcTelemetryMetricContract
{
    [TestCase(CdcProvider.Postgresql, "postgres")]
    [TestCase(CdcProvider.SqlServer, "sql_server")]
    public void It_accepts_current_lag_without_optional_statistics(CdcProvider provider, string label)
    {
        string text =
            $"# TYPE edfi_cdc_source_lag_current_milliseconds gauge\nedfi_cdc_source_lag_current_milliseconds{{connector=\"one\",provider=\"{label}\"}} 5\n";
        CdcTelemetryQualification.AssertStreamingMetrics(text, provider, "one");
    }

    [TestCase("postgres", "NaN")]
    [TestCase("postgres", "-1")]
    [TestCase("wrong", "10")]
    public void It_rejects_exported_optional_values_with_invalid_identity_or_value(string label, string value)
    {
        string text =
            "# TYPE edfi_cdc_source_lag_current_milliseconds gauge\nedfi_cdc_source_lag_current_milliseconds{connector=\"one\",provider=\"postgres\"} 5\n"
            + $"# TYPE edfi_cdc_source_lag_p95_milliseconds gauge\nedfi_cdc_source_lag_p95_milliseconds{{connector=\"one\",provider=\"{label}\"}} {value}\n";
        Action act = () =>
            CdcTelemetryQualification.AssertStreamingMetrics(text, CdcProvider.Postgresql, "one");
        act.Should().Throw<AssertionException>();
    }
}
