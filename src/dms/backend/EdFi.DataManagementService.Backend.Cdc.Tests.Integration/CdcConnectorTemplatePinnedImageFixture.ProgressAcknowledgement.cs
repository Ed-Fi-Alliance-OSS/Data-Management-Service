// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed partial class CdcConnectorTemplatePinnedImageFixture
{
    private bool _isolateSourceProducer;

    public async Task StartProducerProxyAsync(CancellationToken token)
    {
        _isolateSourceProducer.Should().BeTrue();
        await _docker.RunAsync(
            [
                "cp",
                Path.Combine(
                    AppContext.BaseDirectory,
                    "MessageContractRunner",
                    "MessageContractProducerProxy.java"
                ),
                $"{ConnectContainerName}:/tmp/MessageContractProducerProxy.java",
            ],
            token
        );
        await _docker.RunAsync(
            [
                "exec",
                "--detach",
                ConnectContainerName,
                "sh",
                "-lc",
                $"java /tmp/MessageContractProducerProxy.java {BrokerContainerName} >/tmp/contract-producer-output 2>&1",
            ],
            token
        );
        await WaitForProducerProxyAsync(false, token);
    }

    public async Task<MessageContractProducerProxyState> SetProducerBlockedAsync(
        bool blocked,
        CancellationToken token
    )
    {
        await _docker.RunAsync(
            blocked
                ? ["exec", ConnectContainerName, "touch", "/tmp/contract-producer-block"]
                : ["exec", ConnectContainerName, "rm", "-f", "/tmp/contract-producer-block"],
            token
        );
        return await WaitForProducerProxyAsync(blocked, token);
    }

    public async Task<MessageContractProducerProxyState> ReadProducerProxyAsync(CancellationToken token)
    {
        var result = await _docker.RunAllowingFailureAsync(
            ["exec", ConnectContainerName, "cat", "/tmp/contract-producer-state"],
            token
        );
        if (result.ExitCode != 0)
        {
            throw new AssertionException("Producer proxy state unavailable; details redacted.");
        }
        return JsonSerializer.Deserialize<MessageContractProducerProxyState>(result.StandardOutput)!;
    }

    private async Task<MessageContractProducerProxyState> WaitForProducerProxyAsync(
        bool blocked,
        CancellationToken token
    )
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await _docker.RunAllowingFailureAsync(
                ["exec", ConnectContainerName, "cat", "/tmp/contract-producer-state"],
                token
            );
            if (result.ExitCode == 0)
            {
                var state = JsonSerializer.Deserialize<MessageContractProducerProxyState>(
                    result.StandardOutput
                )!;
                if (state.Blocked == blocked)
                {
                    return state;
                }
            }
            await Task.Delay(100, token);
        }
        throw new AssertionException("Producer proxy transition timed out; details redacted.");
    }

    public async Task<string> CapturePostgresqlWalAsync(CancellationToken token) =>
        (await ReadPostgresqlScalarAsync("SELECT pg_current_wal_lsn()::text;", token)).Trim();

    public async Task AssertProducerPathIsIsolatedAsync(
        CdcConnectorTemplateRequest request,
        CancellationToken token
    )
    {
        using var response = await _httpClient.GetAsync(
            $"/connectors/{request.ConnectorName.Value}/config",
            token
        );
        response.IsSuccessStatusCode.Should().BeTrue();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        (
            body.RootElement.GetProperty("producer.override.bootstrap.servers").GetString()
            == $"{ConnectContainerName}:19094"
        )
            .Should()
            .BeTrue();
        var worker = await _docker.RunAsync(
            [
                "exec",
                ConnectContainerName,
                "sh",
                "-lc",
                $"test \"$BOOTSTRAP_SERVERS\" = \"{BrokerContainerName}:9092\" && test \"$OFFSET_FLUSH_INTERVAL_MS\" = 1000",
            ],
            token
        );
        worker.ExitCode.Should().Be(0);
    }
}

internal sealed record MessageContractProducerProxyState(bool Blocked, long Rejected, long Connected);
