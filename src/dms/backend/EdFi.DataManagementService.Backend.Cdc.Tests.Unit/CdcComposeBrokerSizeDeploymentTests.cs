// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Platform(Exclude = "Win", Reason = "Durable local deployment uses Unix directory fsync.")]
public class Given_CdcRecordSizeIncrease_compose_broker
{
    private string _root = null!;
    private string _path = null!;
    private CdcDeploymentRequest _request = null!;
    private CdcComposeBrokerSizeDeployment _deployment = null!;
    private ICdcWorkerDockerCommand _docker = null!;
    private List<string[]> _commands = null!;
    private Action _up = null!;
    private string _project = "selected";
    private string _image = CdcComposeBrokerSizeDeployment.BrokerImage;
    private string _node = "1";
    private const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "broker-size.json");
        _commands = [];
        _up = () => { };
        _project = "selected";
        _image = CdcComposeBrokerSizeDeployment.BrokerImage;
        _node = "1";
        var original = CdcDeploymentRequestTestData.Request();
        _request = new(
            original.Binding,
            original.DmsSettings,
            original.ProviderSetup,
            original.ConnectEndpoint,
            original.WorkerMetricsEndpoint,
            new("dms-kafka1:9092", 100_000_000),
            original.WorkerPolicy,
            original.ProviderConnectionProperties,
            original.KafkaClientSecurityProperties,
            original.Timing
        );
        _docker = new Docker(arguments =>
        {
            _commands.Add(arguments.ToArray());
            if (arguments[0] == "inspect")
            {
                return JsonSerializer.Serialize(
                    new[]
                    {
                        new
                        {
                            Id,
                            State = new { Running = true },
                            Config = new
                            {
                                Image = _image,
                                Env = new[]
                                {
                                    "KAFKA_NODE_ID=" + _node,
                                    "KAFKA_ADVERTISED_LISTENERS=INTERNAL://dms-kafka1:9092,EXTERNAL://127.0.0.1:9092",
                                },
                                Labels = new Dictionary<string, string>
                                {
                                    ["com.docker.compose.project"] = _project,
                                    ["com.docker.compose.service"] = "kafka",
                                },
                            },
                        },
                    }
                );
            }
            if (arguments.Contains("config"))
            {
                return JsonSerializer.Serialize(
                    new
                    {
                        services = new
                        {
                            kafka = new
                            {
                                image = CdcComposeBrokerSizeDeployment.BrokerImage,
                                environment = new
                                {
                                    KAFKA_NODE_ID = "1",
                                    KAFKA_ADVERTISED_LISTENERS = "INTERNAL://dms-kafka1:9092,EXTERNAL://127.0.0.1:9092",
                                    KAFKA_SOCKET_REQUEST_MAX_BYTES = "200000000",
                                },
                            },
                        },
                    }
                );
            }
            if (arguments.Contains("ps"))
            {
                return Id;
            }
            _up();
            return "";
        });
        _deployment = new(
            Path.Combine(_root, "compose.yml"),
            Path.Combine(_root, "selected.env"),
            "selected",
            _path,
            _docker
        );
    }

    private Task Apply() =>
        _deployment.ApplyAsync(
            _request,
            [new(1, 100_000_000, 100_000_000, 100_000_000)],
            CancellationToken.None
        );

    [Test]
    public async Task It_persists_size_only_override_before_recreating_only_the_broker()
    {
        _up = () => File.Exists(_path).Should().BeTrue();
        await Apply();
        using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(_path));
        var environment = stored
            .RootElement.GetProperty("services")
            .GetProperty("kafka")
            .GetProperty("environment");
        environment.EnumerateObject().Should().HaveCount(3);
        environment.GetProperty("KAFKA_SOCKET_REQUEST_MAX_BYTES").GetString().Should().Be("200000000");
        environment.GetProperty("KAFKA_REPLICA_FETCH_MAX_BYTES").GetString().Should().Be("100000000");
        var command = _commands[^1];
        command.Should().ContainInOrder("-f", _path, "up", "--detach", "--no-deps", "--wait");
        command[^1].Should().Be("kafka");
        command.Should().NotContain("kafka-cdc-worker");
    }

    [Test]
    public async Task It_retains_stronger_override_across_failed_recreation()
    {
        _up = () => throw new IOException("lost reply");
        await FluentActions.Awaiting(Apply).Should().ThrowAsync<IOException>();
        string retained = await File.ReadAllTextAsync(_path);
        _up = () => { };
        await Apply();
        (await File.ReadAllTextAsync(_path)).Should().Be(retained);
        _commands.Last(c => c.Contains("config")).Should().Contain(_path);
    }

    [TestCase("project")]
    [TestCase("image")]
    [TestCase("node")]
    public async Task It_rejects_foreign_broker_before_persistence_or_restart(string scenario)
    {
        switch (scenario)
        {
            case "project":
                _project = "foreign";
                break;
            case "image":
                _image = "foreign";
                break;
            case "node":
                _node = "2";
                break;
        }
        await FluentActions.Awaiting(Apply).Should().ThrowAsync<CdcWorkflowStateException>();
        File.Exists(_path).Should().BeFalse();
        _commands.Should().NotContain(c => c.Contains("up"));
    }

    [Test]
    public async Task It_rejects_foreign_override_properties()
    {
        await File.WriteAllTextAsync(_path, "{\"services\":{\"kafka\":{\"image\":\"foreign\"}}}");
        await FluentActions.Awaiting(Apply).Should().ThrowAsync<Exception>();
        _commands.Should().BeEmpty();
    }

    private sealed class Docker(Func<IReadOnlyList<string>, string> run) : ICdcWorkerDockerCommand
    {
        public Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(run(arguments));
        }
    }

    [TearDown]
    public void Cleanup() => Directory.Delete(_root, recursive: true);
}
