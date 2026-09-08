// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
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
        await fixture.QualifyTelemetryAsync(request, timeout.Token);
    }
}

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    public async Task QualifyTelemetryAsync(CdcConnectorTemplateRequest request, CancellationToken token)
    {
        using var metrics = await CreateMetricsClientAsync(token);
        string initial = await metrics.GetStringAsync("/metrics", token);
        CdcTelemetryQualification.AssertStreamingMetrics(initial, Provider, request.ConnectorName.Value);
        double start = CdcTelemetryQualification.Scalar(initial, "edfi_cdc_worker_start_time_seconds");
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
        string restartedWorker = await restartedMetrics.GetStringAsync("/metrics", token);
        CdcTelemetryQualification.AssertStreamingMetrics(
            restartedWorker,
            Provider,
            request.ConnectorName.Value
        );
        CdcTelemetryQualification
            .Scalar(restartedWorker, "edfi_cdc_worker_start_time_seconds")
            .Should()
            .BeGreaterThan(start);
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
                    && line.Contains($"provider=\"{providerLabel}\"", StringComparison.Ordinal)
                )
                .ToArray();
            if (statistic != "current" && samples.Length == 0)
            {
                continue;
            }

            text.Split('\n').Should().Contain($"# TYPE {name} gauge");
            samples.Should().ContainSingle();
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
