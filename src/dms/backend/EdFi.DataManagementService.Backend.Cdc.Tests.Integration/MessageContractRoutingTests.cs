// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
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
public sealed class Given_MessageContractRouting(CdcProvider provider)
{
    private const string Unavailable = "__debezium_unavailable_value";
    private MessageContractFixture _upsert = null!;
    private MessageContractFixture _delete = null!;
    private CoreCdc.CdcArtifactInventory _artifacts = null!;
    private IReadOnlyDictionary<string, MessageContractRunnerObservation> _observations = null!;

    private string ProviderPrefix => provider == CdcProvider.Postgresql ? "PG" : "SQL";

    public static IEnumerable<string> DeleteCases() =>
        ["MATCHING", "UPPERCASE", "BEFORE-ABSENT", "BEFORE-OMITTED", "BEFORE-NULL", "BEFORE-NO-UUID"];

    public static IEnumerable<TestCaseData> DroppedCases()
    {
        foreach (
            (string table, string[] operations) in new[]
            {
                ("DocumentCache", new[] { "d", "t" }),
                ("Document", new[] { "c", "u", "r", "t" }),
            }
        )
        {
            foreach (string operation in operations)
            {
                foreach (string key in new[] { "VALID", "INVALID", "NULL" })
                {
                    yield return new TestCaseData(table, operation, key);
                }
            }
        }
    }

    public static IEnumerable<TestCaseData> RejectedCases()
    {
        yield return new("DELETE-CONFLICT", "DOCUMENT_UUID_MISMATCH");
        yield return new("DELETE-UNAVAILABLE-OPTIONAL", "UNSUPPORTED_DOCUMENT_UUID_SHAPE");
        yield return new("DELETE-UNAVAILABLE-NAMED", "UNSUPPORTED_DOCUMENT_UUID_SHAPE");
        yield return new("UPSERT-UNAVAILABLE-KEY", "INVALID_DOCUMENT_UUID");
        yield return new("UPSERT-UNAVAILABLE-ROW", "INVALID_DOCUMENT_UUID");
    }

    public static IEnumerable<TestCaseData> PartitionCases()
    {
        using JsonDocument vectors = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "Fixtures",
                    "cdc",
                    "message-contract",
                    "partition-vectors.json"
                )
            )
        );
        foreach (JsonElement vector in vectors.RootElement.GetProperty("vectors").EnumerateArray())
        {
            foreach (JsonElement partition in vector.GetProperty("partitions").EnumerateArray())
            {
                yield return new TestCaseData(
                    vector.GetProperty("id").GetString()!,
                    vector.GetProperty("uuid").GetString()!,
                    partition.GetProperty("count").GetInt32(),
                    partition.GetProperty("expected").GetInt32()
                );
            }
        }
    }

    [OneTimeSetUp]
    public async Task Setup()
    {
        string providerName = provider == CdcProvider.Postgresql ? "postgresql" : "sqlserver";
        IReadOnlyList<MessageContractFixture> fixtures = MessageContractFixtureCatalog.LoadAll(
            AppContext.BaseDirectory
        );
        _upsert = fixtures.Single(f =>
            f.ScenarioId == $"MC-FIX-{ProviderPrefix}-ORDINARY-LINK-BEARING-STUDENT-SCHOOL-ASSOCIATION"
        );
        _delete = fixtures.Single(f => f.ScenarioId == $"MC-FIX-{ProviderPrefix}-DOCUMENT-DELETE");
        _artifacts =
            CoreCdc
                .CdcArtifactNameGenerator.Render(
                    new(
                        "contract",
                        "edfi",
                        "routing",
                        1,
                        provider == CdcProvider.Postgresql
                            ? CoreCdc.CdcProvider.Postgresql
                            : CoreCdc.CdcProvider.SqlServer
                    )
                )
                .Inventory
            ?? throw new InvalidOperationException("Invalid message contract artifact input.");
        Dictionary<string, string> config = new()
        {
            ["provider"] = providerName,
            ["target.topic"] = _artifacts.TopicName,
            ["progress.topic"] = _artifacts.ProgressTopicName,
        };
        List<MessageContractRunnerScenario> scenarios = [];
        // Deletes run first. The runner creates a fresh transform per scenario, so no prior upsert
        // or transform-local state can supply a missing key or before image.
        foreach (string name in DeleteCases())
        {
            Add($"DELETE-{name}", DeleteInput(name));
        }
        Add("DELETE-UNAVAILABLE", DeleteInput("UNAVAILABLE"));
        Add("BASELINE-UPSERT", Clone(_upsert));
        foreach (TestCaseData testCase in RejectedCases())
        {
            string name = (string)testCase.Arguments[0]!;
            JsonNode input;
            if (name.StartsWith("UPSERT", StringComparison.Ordinal))
            {
                input = Clone(_upsert);
                JsonNode row = name.EndsWith("KEY", StringComparison.Ordinal)
                    ? input["key"]!
                    : input["value"]!["after"]!;
                row["DocumentUuid"] = Unavailable;
            }
            else
            {
                input = DeleteInput(name["DELETE-".Length..]);
            }
            Add(name, input);
        }
        foreach (TestCaseData testCase in DroppedCases())
        {
            string table = (string)testCase.Arguments[0]!;
            string operation = (string)testCase.Arguments[1]!;
            string key = (string)testCase.Arguments[2]!;
            JsonNode input = table == "DocumentCache" ? Clone(_upsert) : DeleteInput("MATCHING");
            input["operation"] = operation;
            input["value"]!["op"] = operation;
            JsonNode row = (input["value"]!["after"] ?? input["value"]!["before"])!.DeepClone();
            input["value"]!["before"] = operation is "d" or "u" ? row.DeepClone() : null;
            input["value"]!["after"] = operation is "c" or "u" or "r" ? row : null;
            if (key == "INVALID")
            {
                input["key"]!["DocumentUuid"] = "invalid-key-must-not-be-validated-for-dropped-records";
            }
            else if (key == "NULL")
            {
                input["keySchema"]!["optional"] = true;
                input["key"] = null;
            }
            Add(DropName(table, operation, key), input);
        }
        foreach (TestCaseData testCase in PartitionCases())
        {
            string name = (string)testCase.Arguments[0]!;
            string uuid = (string)testCase.Arguments[1]!;
            int count = (int)testCase.Arguments[2]!;
            JsonNode upsert = Clone(_upsert);
            upsert["key"]!["DocumentUuid"] = uuid.ToUpperInvariant();
            upsert["value"]!["after"]!["DocumentUuid"] = uuid.ToUpperInvariant();
            JsonNode document = JsonNode.Parse(
                upsert["value"]!["after"]!["DocumentJson"]!.GetValue<string>()
            )!;
            document["id"] = uuid;
            upsert["value"]!["after"]!["DocumentJson"] = document.ToJsonString();
            JsonNode delete = DeleteInput("BEFORE-NULL");
            delete["key"]!["DocumentUuid"] = uuid.ToUpperInvariant();
            Add($"VECTOR-{name}-{count}-UPSERT", upsert, count);
            Add($"VECTOR-{name}-{count}-DELETE", delete, count);
        }
        try
        {
            MessageContractRunnerResult result = await MessageContractRunner
                .FromEnvironment()
                .RunAsync(scenarios);
            _observations = result.Observations.ToDictionary(o => o.ScenarioId, StringComparer.Ordinal);
        }
        catch (MessageContractRunnerPrerequisiteException failure)
        {
            CdcConnectorTemplateSmokeSettings
                .FromEnvironment(provider)
                .StopOnPrerequisiteFailure(provider, failure.Message);
        }

        void Add(string name, JsonNode input, int partitions = 7)
        {
            JsonElement record = JsonSerializer.SerializeToElement(input);
            MessageContractFixtureCatalog.ValidateSourceRecord(record);
            scenarios.Add(new(ScenarioId(name), record, config, partitions));
        }
    }

    [TestCaseSource(nameof(DeleteCases))]
    public void It_uses_the_authoritative_key_without_requiring_a_prior_upsert(string name)
    {
        JsonElement record = Record($"DELETE-{name}");
        AssertTombstone(record);
        Bytes(record, "keyBytes").Should().Equal(Bytes(Record("BASELINE-UPSERT"), "keyBytes"));
        record
            .GetProperty("partition")
            .GetInt32()
            .Should()
            .Be(Record("BASELINE-UPSERT").GetProperty("partition").GetInt32());
        record
            .GetProperty("key")
            .GetString()
            .Should()
            .Be(_delete.CacheRow.GetProperty("documentUuid").GetString());
    }

    [Test]
    public void It_accepts_the_unavailable_before_uuid_only_for_the_pinned_sql_server_delete()
    {
        if (provider == CdcProvider.SqlServer)
        {
            JsonElement record = Record("DELETE-UNAVAILABLE");
            AssertTombstone(record);
            Bytes(record, "keyBytes").Should().Equal(Bytes(Record("BASELINE-UPSERT"), "keyBytes"));
        }
        else
        {
            AssertFailure("DELETE-UNAVAILABLE", "INVALID_DOCUMENT_UUID");
        }
    }

    [TestCaseSource(nameof(RejectedCases))]
    public void It_rejects_conflicts_and_unavailable_markers_outside_the_pinned_delete_shape(
        string name,
        string reason
    ) => AssertFailure(name, reason);

    [TestCaseSource(nameof(DroppedCases))]
    public void It_drops_excluded_operations_without_public_or_progress_output(
        string table,
        string operation,
        string key
    )
    {
        MessageContractRunnerObservation observation = Observation(DropName(table, operation, key));
        observation.Status.Should().Be("dropped", observation.ToString());
        observation.Data.TryGetProperty("record", out _).Should().BeFalse();
        observation.Data.TryGetProperty("failure", out _).Should().BeFalse();
    }

    [TestCaseSource(nameof(PartitionCases))]
    public void It_matches_fixed_partition_vectors_for_both_upserts_and_tombstones(
        string name,
        string uuid,
        int count,
        int expected
    )
    {
        JsonElement upsert = Record($"VECTOR-{name}-{count}-UPSERT");
        JsonElement delete = Record($"VECTOR-{name}-{count}-DELETE");
        AssertTombstone(delete);
        upsert.GetProperty("valueBytes").GetProperty("kind").GetString().Should().Be("bytes");
        foreach (JsonElement record in new[] { upsert, delete })
        {
            record.GetProperty("topic").GetString().Should().Be(_artifacts.TopicName);
            record.GetProperty("partitionCount").GetInt32().Should().Be(count);
            record.GetProperty("partition").GetInt32().Should().Be(expected);
            Bytes(record, "keyBytes").Should().Equal(Encoding.UTF8.GetBytes(uuid));
            record.GetProperty("key").GetString().Should().Be(uuid);
        }
        Bytes(upsert, "keyBytes").Should().Equal(Bytes(delete, "keyBytes"));
    }

    private JsonNode DeleteInput(string name)
    {
        JsonNode input = Clone(_delete);
        string uuid = _delete.CacheRow.GetProperty("documentUuid").GetString()!;
        input["value"]!["before"]!["DocumentUuid"] = uuid;
        JsonObject fields = input["valueSchema"]!["fields"]!.AsObject();
        switch (name)
        {
            case "MATCHING":
                break;
            case "UPPERCASE":
                input["key"]!["DocumentUuid"] = uuid.ToUpperInvariant();
                break;
            case "BEFORE-ABSENT":
                fields.Remove("before");
                input["value"]!.AsObject().Remove("before");
                break;
            case "BEFORE-OMITTED":
                input["value"]!.AsObject().Remove("before");
                break;
            case "BEFORE-NULL":
                input["value"]!["before"] = null;
                break;
            case "BEFORE-NO-UUID":
                fields["before"]!["fields"] = JsonNode.Parse(
                    """{"DocumentId":{"type":"INT64","optional":false}}"""
                );
                input["value"]!["before"] = new JsonObject { ["DocumentId"] = 123 };
                break;
            case "CONFLICT":
                input["value"]!["before"]!["DocumentUuid"] = "ffffffff-ffff-ffff-ffff-ffffffffffff";
                break;
            case "UNAVAILABLE":
            case "UNAVAILABLE-OPTIONAL":
            case "UNAVAILABLE-NAMED":
                input["value"]!["before"]!["DocumentUuid"] = Unavailable;
                JsonNode schema = fields["before"]!["fields"]!["DocumentUuid"]!;
                if (name == "UNAVAILABLE-OPTIONAL")
                {
                    schema["optional"] = true;
                }
                if (name == "UNAVAILABLE-NAMED")
                {
                    schema["name"] = "contract.UnsupportedUuid";
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }
        return input;
    }

    private void AssertTombstone(JsonElement record)
    {
        record.GetProperty("topic").GetString().Should().Be(_artifacts.TopicName);
        MessageContractJson.ShouldEqual(
            record.GetProperty("keySchema"),
            JsonSerializer.Deserialize<JsonElement>("""{"type":"STRING","optional":false}""")
        );
        // Exact observation shape distinguishes Kafka null from empty bytes and UTF-8 JSON null.
        MessageContractJson.ShouldEqual(
            record.GetProperty("valueBytes"),
            JsonSerializer.Deserialize<JsonElement>("""{"kind":"kafka-null"}""")
        );
        record.GetProperty("valueSchema").ValueKind.Should().Be(JsonValueKind.Null);
        record.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);
        record.GetProperty("headers").GetArrayLength().Should().Be(0);
        record.GetProperty("timestamp").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private void AssertFailure(string name, string reason)
    {
        MessageContractRunnerObservation observation = Observation(name);
        observation.Status.Should().Be("failed", observation.ToString());
        observation.Data.TryGetProperty("record", out _).Should().BeFalse();
        JsonElement failure = observation.Data.GetProperty("failure");
        failure.GetProperty("stage").GetString().Should().Be("transform");
        failure.GetProperty("category").GetString().Should().Be("DataException");
        failure.GetProperty("reason").GetString().Should().Be(reason);
    }

    private MessageContractRunnerObservation Observation(string name) => _observations[ScenarioId(name)];

    private JsonElement Record(string name)
    {
        MessageContractRunnerObservation observation = Observation(name);
        observation.Status.Should().Be("retained", observation.ToString());
        return observation.Data.GetProperty("record");
    }

    private string ScenarioId(string name) => $"MC-ROUTING-{ProviderPrefix}-{name}";

    private static string DropName(string table, string operation, string key) =>
        $"DROP-{table.ToUpperInvariant()}-{operation.ToUpperInvariant()}-{key}";

    private static JsonNode Clone(MessageContractFixture fixture) =>
        JsonNode.Parse(fixture.SourceRecord.GetRawText())!;

    private static byte[] Bytes(JsonElement record, string property)
    {
        JsonElement observed = record.GetProperty(property);
        observed.GetProperty("kind").GetString().Should().Be("bytes");
        byte[] bytes = observed.GetProperty("base64").GetBytesFromBase64();
        bytes.Length.Should().Be(observed.GetProperty("length").GetInt32());
        return bytes;
    }
}
