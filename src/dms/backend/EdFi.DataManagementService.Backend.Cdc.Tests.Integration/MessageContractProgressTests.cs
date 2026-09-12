// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql, Category = "PostgresqlIntegration")]
[TestFixture(CdcProvider.SqlServer, Category = "MssqlIntegration")]
[Category("DatabaseIntegration")]
[Category("CdcMessageContract")]
[Category("CdcMessageContractSerialized")]
public sealed class Given_MessageContractProgress(CdcProvider provider)
{
    private MessageContractFixture _fixture = null!;
    private CoreCdc.CdcArtifactInventory _artifacts = null!;
    private readonly Dictionary<string, JsonElement> _inputs = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, MessageContractRunnerObservation> _observations = null!;

    public static IEnumerable<TestCaseData> ProgressCases()
    {
        foreach (string key in new[] { "STRUCTURED", "NULL", "STRING", "INVALID-PUBLIC-UUID" })
        {
            foreach (string operation in new[] { "c", "u", "r", "d", "t" })
            {
                yield return new TestCaseData($"RELATIONAL-{operation.ToUpperInvariant()}-{key}");
            }
            foreach (
                string value in new[] { "STRUCT", "STRING", "SCHEMALESS", "NULL", "TYPED-NULL", "DECIMAL" }
            )
            {
                yield return new TestCaseData($"NATIVE-{value}-{key}");
            }
        }
        yield return new("COLLISION-HEARTBEAT");
    }

    public static IEnumerable<TestCaseData> RejectedCases()
    {
        foreach (string server in new[] { "MISSING", "EMPTY", "NULL", "NUMBER", "OBJECT" })
        {
            yield return new($"SERVER-{server}", "MALFORMED_NATIVE_HEARTBEAT");
        }
        foreach (string topic in new[] { "EMPTY-SUFFIX", "MISMATCH", "EXTRA-SUFFIX", "CASE", "DELIMITER" })
        {
            yield return new($"TOPIC-{topic}", "MISSING_SOURCE_METADATA");
        }
    }

    [OneTimeSetUp]
    public async Task Setup()
    {
        string providerName = provider == CdcProvider.Postgresql ? "postgresql" : "sqlserver";
        string prefix = provider == CdcProvider.Postgresql ? "PG" : "SQL";
        _fixture = MessageContractFixtureCatalog
            .LoadAll(AppContext.BaseDirectory)
            .Single(f => f.ScenarioId == $"MC-FIX-{prefix}-ORDINARY-LINK-BEARING-STUDENT-SCHOOL-ASSOCIATION");
        _artifacts =
            CoreCdc
                .CdcArtifactNameGenerator.Render(
                    new(
                        "contract",
                        "edfi",
                        "progress",
                        1,
                        provider == CdcProvider.Postgresql
                            ? CoreCdc.CdcProvider.Postgresql
                            : CoreCdc.CdcProvider.SqlServer
                    )
                )
                .Inventory
            ?? throw new InvalidOperationException("Invalid message contract artifact input.");
        foreach (TestCaseData testCase in ProgressCases())
        {
            string name = (string)testCase.Arguments[0]!;
            JsonNode input;
            if (name == "COLLISION-HEARTBEAT")
            {
                input = Relational("u");
                Collision(input);
            }
            else
            {
                string[] parts = name.Split('-', 3);
                // TYPED-NULL contains a hyphen, so split value and key explicitly.
                if (name.StartsWith("NATIVE-TYPED-NULL-", StringComparison.Ordinal))
                {
                    parts = ["NATIVE", "TYPED-NULL", name["NATIVE-TYPED-NULL-".Length..]];
                }
                input = parts[0] == "RELATIONAL" ? Relational(parts[1].ToLowerInvariant()) : Native(parts[1]);
                SetKey(input, parts[2]);
            }
            Add(name, input);
        }
        JsonNode collisionUpsert = Clone();
        Collision(collisionUpsert);
        Add("COLLISION-UPSERT", collisionUpsert);
        JsonNode collisionDrop = Clone();
        collisionDrop["operation"] = "d";
        collisionDrop["value"]!["op"] = "d";
        Collision(collisionDrop);
        Add("COLLISION-DROP", collisionDrop);
        foreach (TestCaseData testCase in RejectedCases())
        {
            string name = (string)testCase.Arguments[0]!;
            JsonNode input = Native("NULL");
            JsonObject partition = input["sourcePartition"]!.AsObject();
            switch (name)
            {
                case "SERVER-MISSING":
                    partition.Remove("server");
                    break;
                case "SERVER-EMPTY":
                    partition["server"] = "";
                    break;
                case "SERVER-NULL":
                    partition["server"] = null;
                    break;
                case "SERVER-NUMBER":
                    partition["server"] = 42;
                    break;
                case "SERVER-OBJECT":
                    partition["server"] = new JsonObject();
                    break;
                case "TOPIC-EMPTY-SUFFIX":
                    input["sourceTopic"] = "__debezium-heartbeat.";
                    break;
                case "TOPIC-MISMATCH":
                    input["sourceTopic"] = "__debezium-heartbeat.other-server";
                    break;
                case "TOPIC-EXTRA-SUFFIX":
                    input["sourceTopic"] = "__debezium-heartbeat.contract-source.extra";
                    break;
                case "TOPIC-CASE":
                    input["sourceTopic"] = "__debezium-heartbeat.CONTRACT-SOURCE";
                    break;
                case "TOPIC-DELIMITER":
                    input["sourceTopic"] = "__debezium-heartbeat-contract-source";
                    break;
                default:
                    throw new InvalidOperationException("Unknown progress scenario.");
            }
            Add(name, input);
        }
        Dictionary<string, string> config = new()
        {
            ["provider"] = providerName,
            ["target.topic"] = _artifacts.TopicName,
            ["progress.topic"] = _artifacts.ProgressTopicName,
        };
        try
        {
            MessageContractRunnerResult result = await MessageContractRunner
                .FromEnvironment()
                .RunAsync(
                    _inputs
                        .Select(p => new MessageContractRunnerScenario(ScenarioId(p.Key), p.Value, config, 1))
                        .ToArray()
                );
            _observations = result.Observations.ToDictionary(o => o.ScenarioId, StringComparer.Ordinal);
        }
        catch (MessageContractRunnerPrerequisiteException failure)
        {
            CdcConnectorTemplateSmokeSettings
                .FromEnvironment(provider)
                .StopOnPrerequisiteFailure(provider, failure.Message);
        }
    }

    [TestCaseSource(nameof(ProgressCases))]
    public void It_routes_to_the_binding_progress_topic_with_exact_fixed_key_bytes(string name)
    {
        JsonElement record = Record(name);
        record.GetProperty("topic").GetString().Should().Be(_artifacts.ProgressTopicName);
        _artifacts.ProgressTopicName.Should().Be(_artifacts.TopicName + ".cdc-progress");
        MessageContractJson.ShouldEqual(record.GetProperty("keySchema"), Element(StringSchema()));
        record.GetProperty("key").GetString().Should().Be("cdc-progress");
        Bytes(record, "keyBytes")
            .Should()
            .Equal(0x63, 0x64, 0x63, 0x2d, 0x70, 0x72, 0x6f, 0x67, 0x72, 0x65, 0x73, 0x73);
        record.GetProperty("partitionCount").GetInt32().Should().Be(1);
        record.GetProperty("partition").GetInt32().Should().Be(0);
    }

    [TestCaseSource(nameof(ProgressCases))]
    public void It_preserves_source_metadata_headers_and_connect_timestamp(string name)
    {
        JsonElement record = Record(name);
        foreach (string property in new[] { "sourcePartition", "sourceOffset", "headers", "timestamp" })
        {
            MessageContractJson.ShouldEqual(
                record.GetProperty(property),
                _inputs[name].GetProperty(property)
            );
        }
    }

    [TestCaseSource(nameof(ProgressCases))]
    public void It_preserves_values_or_substitutes_only_native_nulls_and_delegates_json_conversion(
        string name
    )
    {
        JsonElement record = Record(name);
        JsonElement input = _inputs[name];
        bool nativeNull =
            name.StartsWith("NATIVE-", StringComparison.Ordinal)
            && input.GetProperty("value").ValueKind == JsonValueKind.Null;
        JsonElement expectedSchema = nativeNull ? Element(StringSchema()) : input.GetProperty("valueSchema");
        JsonElement expectedValue = input.GetProperty("value");
        if (nativeNull)
        {
            expectedValue = JsonSerializer.SerializeToElement("native-heartbeat");
        }
        else if (name.StartsWith("NATIVE-DECIMAL-", StringComparison.Ordinal))
        {
            expectedValue = JsonSerializer.SerializeToElement(123.45m);
        }
        MessageContractJson.ShouldEqual(record.GetProperty("valueSchema"), expectedSchema);
        MessageContractJson.ShouldEqual(record.GetProperty("value"), expectedValue);
        using JsonDocument serialized = JsonDocument.Parse(Bytes(record, "valueBytes"));
        // Full comparison proves schemas.enable=false; Decimal must be numeric, never base64.
        MessageContractJson.ShouldEqual(serialized.RootElement, expectedValue);
        record.TryGetProperty("converterBytesEqual", out _).Should().BeFalse();
        record.TryGetProperty("converterDefensiveCopy", out _).Should().BeFalse();
    }

    [TestCaseSource(nameof(RejectedCases))]
    public void It_fails_closed_for_malformed_native_heartbeat_identity(string name, string reason)
    {
        MessageContractRunnerObservation observation = Observation(name);
        observation.Status.Should().Be("failed", observation.ToString());
        observation.Data.TryGetProperty("record", out _).Should().BeFalse();
        JsonElement failure = observation.Data.GetProperty("failure");
        failure.GetProperty("stage").GetString().Should().Be("transform");
        failure.GetProperty("category").GetString().Should().Be("DataException");
        failure.GetProperty("reason").GetString().Should().Be(reason);
    }

    [Test]
    public void It_classifies_heartbeat_prefix_collisions_using_relational_metadata()
    {
        JsonElement upsert = Record("COLLISION-UPSERT");
        upsert.GetProperty("topic").GetString().Should().Be(_artifacts.TopicName);
        using JsonDocument value = JsonDocument.Parse(Bytes(upsert, "valueBytes"));
        MessageContractJson.ShouldEqual(value.RootElement, _fixture.ExpectedEnvelope);
        MessageContractRunnerObservation dropped = Observation("COLLISION-DROP");
        dropped.Status.Should().Be("dropped", dropped.ToString());
        dropped.Data.TryGetProperty("record", out _).Should().BeFalse();
        dropped.Data.TryGetProperty("failure", out _).Should().BeFalse();
    }

    private JsonNode Relational(string operation)
    {
        JsonNode input = Clone();
        input["sourceTopic"] = input["sourceTopic"]!
            .GetValue<string>()
            .Replace("DocumentCache", "CdcHeartbeat", StringComparison.Ordinal);
        input["operation"] = operation;
        input["value"]!["op"] = operation;
        input["value"]!["source"]!["table"] = "CdcHeartbeat";
        JsonNode rowSchema = JsonNode.Parse(
            """
            {"type":"STRUCT","optional":true,"fields":{
              "HeartbeatId":{"type":"INT16","optional":false},
              "HeartbeatSequence":{"type":"INT64","optional":false},
              "HeartbeatAt":{"type":"STRING","optional":false,"version":1}}}
            """
        )!;
        rowSchema["fields"]!["HeartbeatAt"]!["name"] =
            provider == CdcProvider.Postgresql
                ? "io.debezium.time.ZonedTimestamp"
                : "io.debezium.time.IsoTimestamp";
        input["valueSchema"]!["name"] = "contract.CdcHeartbeat.Envelope";
        input["valueSchema"]!["fields"]!["before"] = rowSchema.DeepClone();
        input["valueSchema"]!["fields"]!["after"] = rowSchema;
        JsonObject row = new()
        {
            ["HeartbeatId"] = 1,
            ["HeartbeatSequence"] = 9007199254740993L,
            ["HeartbeatAt"] = "2026-08-01T01:02:03.123456Z",
        };
        input["value"]!["before"] = operation is "u" or "d" ? row.DeepClone() : null;
        input["value"]!["after"] = operation is "c" or "u" or "r" ? row : null;
        return input;
    }

    private JsonNode Native(string shape)
    {
        JsonNode input = Clone();
        input["sourceTopic"] = "__debezium-heartbeat.contract-source";
        input["operation"] = "native-heartbeat";
        input["valueSchema"] = null;
        input["value"] = null;
        SetKey(input, "NULL");
        switch (shape)
        {
            case "STRUCT":
                input["valueSchema"] = JsonNode.Parse(
                    """{"type":"STRUCT","optional":false,"name":"io.debezium.connector.common.Heartbeat","fields":{"ts_ms":{"type":"INT64","optional":false}}}"""
                );
                input["value"] = new JsonObject { ["ts_ms"] = 1785420916123L };
                break;
            case "STRING":
                input["valueSchema"] = StringSchema();
                input["value"] = "heartbeat-value";
                break;
            case "SCHEMALESS":
                input["value"] = JsonNode.Parse("""{"ts_ms":1785420916123,"tags":["heartbeat",null,true]}""");
                break;
            case "NULL":
                break;
            case "TYPED-NULL":
                input["valueSchema"] = new JsonObject { ["type"] = "STRING", ["optional"] = true };
                break;
            case "DECIMAL":
                input["valueSchema"] = JsonNode.Parse(
                    """{"type":"BYTES","optional":false,"name":"org.apache.kafka.connect.data.Decimal","version":1,"parameters":{"scale":"2"}}"""
                );
                // Kafka Decimal physical bytes: signed unscaled 12345 (0x3039), scale 2.
                input["value"] = "MDk=";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
        return input;
    }

    private static void SetKey(JsonNode input, string shape)
    {
        input["keySchema"] = null;
        input["key"] = null;
        switch (shape)
        {
            case "STRUCTURED":
                input["keySchema"] = JsonNode.Parse(
                    """{"type":"STRUCT","optional":false,"name":"contract.CdcHeartbeat.Key","fields":{"HeartbeatId":{"type":"INT16","optional":false}}}"""
                );
                input["key"] = new JsonObject { ["HeartbeatId"] = 1 };
                break;
            case "NULL":
                break;
            case "STRING":
                input["keySchema"] = StringSchema();
                input["key"] = "provider-key";
                break;
            case "INVALID-PUBLIC-UUID":
                input["keySchema"] = JsonNode.Parse(
                    """{"type":"STRUCT","optional":false,"fields":{"DocumentUuid":{"type":"STRING","optional":false}}}"""
                );
                input["key"] = new JsonObject { ["DocumentUuid"] = "invalid-public-uuid" };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    private static void Collision(JsonNode input)
    {
        const string prefix = "__debezium-heartbeat.instance";
        input["sourceTopic"] = input["sourceTopic"]!
            .GetValue<string>()
            .Replace("contract-source", prefix, StringComparison.Ordinal);
        input["sourcePartition"]!["server"] = prefix;
        input["value"]!["source"]!["name"] = prefix;
    }

    private JsonNode Clone()
    {
        JsonNode input = JsonNode.Parse(_fixture.SourceRecord.GetRawText())!;
        // Duplicate header names and different types make loss/reordering observable.
        input["headers"]!
            .AsArray()
            .Add(
                JsonNode.Parse(
                    """{"key":"fixture-header","schema":{"type":"INT64","optional":false},"value":9007199254740993}"""
                )
            );
        input["headers"]!
            .AsArray()
            .Add(
                JsonNode.Parse(
                    """{"key":"nullable-header","schema":{"type":"STRING","optional":true},"value":null}"""
                )
            );
        return input;
    }

    private void Add(string name, JsonNode input) => _inputs.Add(name, Element(input));

    private static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node);

    private static JsonObject StringSchema() => new() { ["type"] = "STRING", ["optional"] = false };

    private string ScenarioId(string name) =>
        $"MC-PROGRESS-{(provider == CdcProvider.Postgresql ? "PG" : "SQL")}-{name}";

    private MessageContractRunnerObservation Observation(string name) => _observations[ScenarioId(name)];

    private JsonElement Record(string name)
    {
        MessageContractRunnerObservation observation = Observation(name);
        observation.Status.Should().Be("retained", observation.ToString());
        return observation.Data.GetProperty("record");
    }

    private static byte[] Bytes(JsonElement record, string property)
    {
        JsonElement observed = record.GetProperty(property);
        observed.GetProperty("kind").GetString().Should().Be("bytes");
        byte[] bytes = observed.GetProperty("base64").GetBytesFromBase64();
        bytes.Length.Should().Be(observed.GetProperty("length").GetInt32());
        return bytes;
    }
}
