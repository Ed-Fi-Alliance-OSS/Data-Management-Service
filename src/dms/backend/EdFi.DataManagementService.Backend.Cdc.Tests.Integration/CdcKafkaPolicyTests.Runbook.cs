// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.SchemaTools.Tests.Unit;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed partial class CdcKafkaPolicyFixture
{
    internal async Task InspectRunbookRetentionAsync(
        CdcDeploymentRequest request,
        int broker,
        int sample,
        bool denied,
        CancellationToken token
    )
    {
        const string id = "cdc-kafka-retention-inspect";
        string properties = "/tmp/runbook-" + Guid.NewGuid().ToString("N") + ".properties";
        const string prepare = """
            umask 077
            printf 'security.protocol=SASL_PLAINTEXT\nsasl.mechanism=PLAIN\nsasl.jaas.config=org.apache.kafka.common.security.plain.PlainLoginModule required username="%s" password="%s";\n' "$PROBE_USER" "$PROBE_PASSWORD" > "$PROBE_FILE"
            """;
        try
        {
            await DockerAsync(
                [
                    "exec",
                    "-e",
                    "PROBE_PASSWORD=" + _password,
                    "-e",
                    "PROBE_USER=" + (denied ? "consumer-a" : "admin"),
                    "-e",
                    "PROBE_FILE=" + properties,
                    Broker(broker),
                    "sh",
                    "-ec",
                    prepare,
                ],
                token
            );
            // Pinned image's effective default; the live DescribeLogDirs result below
            // must match before this df observation can qualify the broker directory.
            const string dataPath = "/tmp/kafka-logs";
            var plan = CdcDeploymentKafkaPolicy.Build(request);
            var result = await CdcRunbookLiveCommands.InvokeInspectionAsync(
                id,
                new Dictionary<string, string>
                {
                    ["<kafka-broker-container>"] = Broker(broker),
                    ["<kafka-inspection-properties>"] = properties,
                    ["<public-topic>"] = request.Binding.TopicName,
                    ["<progress-topic>"] = plan
                        .BindingTopics.Single(t => t.Role == CdcKafkaTopicRole.Progress)
                        .Name,
                    ["<history-topic>"] = plan
                        .BindingTopics.Single(t => t.Role == CdcKafkaTopicRole.SchemaHistory)
                        .Name,
                    ["<offset-topic>"] = OffsetTopic,
                    ["<private-jmx-url>"] = "service:jmx:rmi:///jndi/rmi://127.0.0.1:9999/jmxrmi",
                    ["<kafka-data-path>"] = dataPath,
                },
                new Dictionary<string, string>(),
                token
            );
            if (denied)
            {
                result.ExitCode.Should().NotBe(0);
                result.Error.Should().Contain("Topic configuration unavailable.");
                await KafkaRunbookEvidence.WriteAsync(
                    id,
                    new
                    {
                        Broker = broker,
                        Sample = sample,
                        result.ExitCode,
                        Role = "InstanceConsumer",
                        Action = "InspectionUnavailableNeverSafeIsolation",
                    },
                    token
                );
                return;
            }
            result.ExitCode.Should().Be(0, "bounded inspection must complete: {0}", result.Error);
            List<object> topics = [];
            foreach (var topic in plan.BindingTopics.Append(plan.OffsetStore))
            {
                result.Output.Should().Contain("All configs for topic " + topic.Name + " are:");
                result.Output.Should().Contain("Topic: " + topic.Name);
                foreach (var config in topic.Configuration)
                {
                    result.Output.Should().Contain(config.Key + "=" + config.Value);
                }
                var observed = Value(await Adapter.InspectTopicAsync(request, topic.Name, token));
                CdcDeploymentKafkaPolicy
                    .ObserveTopic(
                        request,
                        topic,
                        new CdcTransportResult<CdcKafkaTopicEvidence>.Observed(observed)
                    )
                    .State.Should()
                    .Be(Core.DocumentCache.Cdc.CdcKafkaPolicyItemState.Satisfied);
                topics.Add(
                    new
                    {
                        Role = topic.Role.ToString(),
                        observed.PartitionReplicas,
                        observed.Configuration,
                    }
                );
            }
            var lines = result.Output.Split('\n').Select(l => l.Trim()).ToArray();
            var json = lines.Single(l => l.StartsWith('{'));
            using var directories = JsonDocument.Parse(json);
            var replicas = directories
                .RootElement.GetProperty("brokers")
                .EnumerateArray()
                .SelectMany(b => b.GetProperty("logDirs").EnumerateArray())
                .ToArray();
            replicas.Should().HaveCount(3);
            foreach (var directory in replicas)
            {
                directory.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
                directory.GetProperty("logDir").GetString().Should().Be(dataPath);
            }
            long retainedBytes = replicas
                .SelectMany(d => d.GetProperty("partitions").EnumerateArray())
                .Sum(p => p.GetProperty("size").GetInt64());
            retainedBytes.Should().BeGreaterThan(0);
            var bounds = lines
                .Where(l => l.StartsWith(request.Binding.TopicName + ":", StringComparison.Ordinal))
                .Select(l => l.Split(':'))
                .ToArray();
            bounds.Should().HaveCount(request.Binding.PartitionCount * 2);
            var earliest = bounds
                .Take(request.Binding.PartitionCount)
                .ToDictionary(
                    v => int.Parse(v[1], CultureInfo.InvariantCulture),
                    v => long.Parse(v[2], CultureInfo.InvariantCulture)
                );
            var ends = bounds
                .Skip(request.Binding.PartitionCount)
                .ToDictionary(
                    v => int.Parse(v[1], CultureInfo.InvariantCulture),
                    v => long.Parse(v[2], CultureInfo.InvariantCulture)
                );
            ends.Keys.Should().BeEquivalentTo(earliest.Keys);
            ends.Should().OnlyContain(p => p.Value >= earliest[p.Key]);
            Dictionary<string, double> metrics = [];
            for (int i = 0; i < lines.Length - 1; i++)
            {
                if (!lines[i].StartsWith("\"time\",", StringComparison.Ordinal))
                {
                    continue;
                }
                // JmxTool's original format surrounds each ObjectName with quotes but does
                // not escape embedded logDirectory quotes. Split only its column delimiters.
                var names = lines[i].Trim('"').Split("\",\"", StringSplitOptions.None);
                var values = lines[i + 1].Split(',');
                names
                    .Length.Should()
                    .Be(values.Length, "JMX header {0} must match sample {1}", lines[i], lines[i + 1]);
                for (int n = 1; n < names.Length; n++)
                {
                    metrics.Add(names[n], double.Parse(values[n], CultureInfo.InvariantCulture));
                }
            }
            metrics.Keys.Should().Contain(k => k.Contains("max-dirty-percent", StringComparison.Ordinal));
            metrics.Keys.Should().Contain(k => k.Contains("DeadThreadCount", StringComparison.Ordinal));
            metrics
                .Where(p => p.Key.Contains("DeadThreadCount", StringComparison.Ordinal))
                .Should()
                .OnlyContain(p => Math.Abs(p.Value) < double.Epsilon);
            // df reports the containing mount, which need not equal the requested directory.
            var fields = lines
                .Select(l => l.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries))
                .Single(f => f.Length == 6 && f[4].EndsWith('%') && long.TryParse(f[3], out _));
            long availableKilobytes = long.Parse(fields[3], CultureInfo.InvariantCulture);
            availableKilobytes.Should().BeGreaterThan(0);
            await KafkaRunbookEvidence.WriteAsync(
                id,
                new
                {
                    Broker = broker,
                    Sample = sample,
                    result.ExitCode,
                    Role = "DeploymentAdministrator",
                    Topics = topics,
                    RetainedBytes = retainedBytes,
                    PartitionCount = ends.Count,
                    BoundsObservedAndOrdered = true,
                    CleanerMetrics = metrics,
                    AvailableKilobytes = availableKilobytes,
                    Action = "RepeatUnderWorkloadNoPurgeOrCapacityCertification",
                },
                token
            );
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await DockerAsync(["exec", Broker(broker), "rm", "-f", properties], cleanup.Token);
        }
    }
}

internal static class KafkaRunbookEvidence
{
    internal static async Task WriteAsync(string id, object observation, CancellationToken token)
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "cdc-controller-kafka-" + Guid.NewGuid().ToString("N") + ".json"
        );
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                new
                {
                    ObservedAt = DateTimeOffset.UtcNow,
                    TestId = TestContext.CurrentContext.Test.MethodName,
                    SnippetId = id,
                    SnippetSha256 = Convert.ToHexStringLower(
                        SHA256.HashData(Encoding.UTF8.GetBytes(CdcRunbookExamples.Read(id)))
                    ),
                    Profile = "AuthorizationEnabled/ProductionDurability",
                    BrokerImage = CdcComposeBrokerSizeDeployment.BrokerImage,
                    ConnectImage = CdcQualifiedWorkerImage.Image,
                    Observation = observation,
                }
            ),
            token
        );
        TestContext.AddTestAttachment(
            path,
            "Bounded inspection fields; no credentials, physical names or raw offsets"
        );
    }
}
