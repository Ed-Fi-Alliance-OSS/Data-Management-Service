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
    private bool _controllerComposeKafka;
    private string _controllerComposeDirectory = "";
    private readonly HashSet<string> _controllerComposeVolumes = [];
    internal string ControllerComposeFile => Path.Combine(_controllerComposeDirectory, "compose.json");
    internal string ControllerComposeEnvironment => Path.Combine(_controllerComposeDirectory, "fixture.env");
    internal string ControllerSizeOverride => Path.Combine(_controllerComposeDirectory, "broker-size.json");

    internal ICdcKafkaBrokerSizeDeployment ComposeBrokerSizes =>
        new CdcComposeBrokerSizeDeployment(
            ControllerComposeFile,
            ControllerComposeEnvironment,
            ControllerProject,
            ControllerSizeOverride
        );

    private async Task PrepareControllerComposeAsync(CancellationToken token)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "eng", "docker-compose", "kafka-cdc.yml")))
        {
            directory =
                directory.Parent ?? throw new DirectoryNotFoundException("Repository root unavailable.");
        }
        string shipped = Path.Combine(directory.FullName, "eng", "docker-compose", "kafka-cdc.yml");
        _controllerComposeDirectory = Path.Combine(Path.GetTempPath(), _resourcePrefix + "-compose");
        Directory.CreateDirectory(_controllerComposeDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                _controllerComposeDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        // Only isolate names, network and ports and select a small initial size. Inherit the
        // shipped broker image, storage layout, startup command and worker deployment.
        var services = new Dictionary<string, object>
        {
            ["kafka"] = new
            {
                extends = new { file = shipped, service = "kafka" },
                container_name = BrokerContainerName,
                environment = new Dictionary<string, string>
                {
                    ["KAFKA_ADVERTISED_LISTENERS"] =
                        $"INTERNAL://{BrokerContainerName}:9092,EXTERNAL://{ControllerKafkaBootstrapServers}",
                },
                networks = new { dms = new { aliases = new[] { "dms-kafka1" } } },
            },
            ["kafka-cdc-worker"] = new
            {
                extends = new { file = shipped, service = "kafka-cdc-worker" },
                container_name = ConnectContainerName,
                environment = new Dictionary<string, string>
                {
                    ["BOOTSTRAP_SERVERS"] = KafkaBootstrapServers,
                },
            },
        };
        await File.WriteAllTextAsync(
            ControllerComposeFile,
            JsonSerializer.Serialize(
                new
                {
                    services,
                    networks = new { dms = new { external = true, name = NetworkName } },
                    volumes = new Dictionary<string, object>
                    {
                        ["kafka-data"] = new { },
                        ["kafka-logs"] = new { },
                    },
                }
            ),
            token
        );
        await File.WriteAllLinesAsync(
            ControllerComposeEnvironment,
            [
                $"KAFKA_PORT={_controllerBrokerPort}",
                $"CONNECT_SOURCE_PORT={_controllerConnectPort}",
                $"CDC_METRICS_PORT={_controllerMetricsPort}",
                $"CDC_CONNECT_IMAGE={_settings.ConnectImage}",
                $"CDC_WORKER_KEY={ControllerProject}",
                $"CDC_CONFIG_STORAGE_TOPIC={ControllerProject}.connect.configs",
                $"CDC_OFFSET_STORAGE_TOPIC={ControllerProject}.connect.offsets",
                $"CDC_STATUS_STORAGE_TOPIC={ControllerProject}.connect.status",
                $"CDC_DATABASE_PASSWORD={ConnectorDatabasePassword}",
                "CDC_WORKER_HEAP_MIB=1024",
                "CDC_MAX_RECORD_BYTES=1000000",
            ],
            token
        );
        // A real resolved service check catches an accidental fixture storage substitution.
        using var resolved = JsonDocument.Parse(
            (await RunControllerComposeAsync(["config", "--format", "json"], token)).StandardOutput
        );
        var mounts = resolved.RootElement.GetProperty("services").GetProperty("kafka").GetProperty("volumes");
        mounts
            .EnumerateArray()
            .Should()
            .Contain(v =>
                v.GetProperty("source").GetString() == "kafka-data"
                && v.GetProperty("target").GetString() == "/tmp/kraft-combined-logs"
            );
    }

    private Task<DockerCommandResult> RunControllerComposeAsync(string[] command, CancellationToken token) =>
        _docker.RunAsync(
            [
                "compose",
                "-f",
                ControllerComposeFile,
                "--env-file",
                ControllerComposeEnvironment,
                "-p",
                ControllerProject,
                .. (File.Exists(ControllerSizeOverride) ? new[] { "-f", ControllerSizeOverride } : []),
                .. command,
            ],
            token
        );

    internal async Task RecreateControllerComposeAsync(
        CdcDeploymentRequest request,
        CdcKafkaProvisioning kafka,
        CancellationToken token
    )
    {
        // Caller first obtains a durable verified stop through the managed lifecycle controller.
        // Match the wrapper's non-destructive down/up: retain named volumes and the provider.
        await RunControllerComposeAsync(
            ["--profile", "cdc-managed-worker", "down", "--remove-orphans"],
            token
        );
        var startup = new CdcWorkerStartup(
            kafka,
            new CdcComposeWorkerStartupTransport(
                ControllerComposeFile,
                ControllerComposeEnvironment,
                ControllerProject,
                ControllerSizeOverride
            )
        );
        (await startup.StartRetainedAsync(request, token))
            .Should()
            .BeOfType<CdcTransportResult<CdcTransportAcknowledgement>.Observed>();
        await WaitForKafkaConnectAsync(token);
        await AssertControllerMetricsPrerequisiteAsync(token);
    }

    internal async Task<string> ComposeBrokerContainerIdAsync(CancellationToken token) =>
        (await RunControllerComposeAsync(["ps", "--quiet", "kafka"], token)).StandardOutput.Trim();

    internal async Task<string> ComposeWorkerContainerIdAsync(CancellationToken token) =>
        (
            await RunControllerComposeAsync(
                ["--profile", "cdc-managed-worker", "ps", "--quiet", "kafka-cdc-worker"],
                token
            )
        ).StandardOutput.Trim();

    internal async Task AssertComposeDataMountAsync(CancellationToken token)
    {
        using var inspected = JsonDocument.Parse(
            (
                await _docker.RunAsync(["inspect", BrokerContainerName, ConnectContainerName], token)
            ).StandardOutput
        );
        // Non-destructive down retains the image's otherwise unused anonymous volumes too.
        // Remember exact ownership before replacement so final fixture cleanup removes them.
        foreach (
            var mount in inspected
                .RootElement.EnumerateArray()
                .SelectMany(container => container.GetProperty("Mounts").EnumerateArray())
        )
        {
            if (mount.GetProperty("Type").GetString() == "volume")
            {
                _controllerComposeVolumes.Add(mount.GetProperty("Name").GetString()!);
            }
        }
        inspected
            .RootElement[0]
            .GetProperty("Mounts")
            .EnumerateArray()
            .Should()
            .Contain(m =>
                m.GetProperty("Type").GetString() == "volume"
                && m.GetProperty("Name").GetString() == ControllerProject + "_kafka-data"
                && m.GetProperty("Destination").GetString() == "/tmp/kraft-combined-logs"
            );
        var config = await _docker.RunAsync(
            ["exec", BrokerContainerName, "cat", "/opt/kafka/config/server.properties"],
            token
        );
        config.StandardOutput.Should().Contain("log.dirs=/tmp/kraft-combined-logs");
        await _docker.RunAsync(
            [
                "exec",
                BrokerContainerName,
                "ls",
                "-d",
                "/tmp/kraft-combined-logs/meta.properties",
                "/tmp/kraft-combined-logs/__cluster_metadata-0",
            ],
            token
        );
        var process = await _docker.RunAsync(["exec", BrokerContainerName, "cat", "/proc/1/status"], token);
        process
            .StandardOutput.Should()
            .MatchRegex(
                @"(?m)^Uid:\s+[1-9][0-9]*\s",
                "volume initialization must hand PID 1 to the unprivileged Kafka process"
            );
    }
}
