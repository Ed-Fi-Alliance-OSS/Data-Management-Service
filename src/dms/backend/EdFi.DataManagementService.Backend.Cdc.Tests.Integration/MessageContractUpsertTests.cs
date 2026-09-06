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
public sealed class Given_MessageContractUpsert(CdcProvider provider)
{
    private IReadOnlyDictionary<string, MessageContractFixture> _fixtures = null!;
    private IReadOnlyDictionary<string, MessageContractRunnerObservation> _observations = null!;
    private CoreCdc.CdcArtifactInventory _artifacts = null!;

    private string ProviderPrefix => provider == CdcProvider.Postgresql ? "PG" : "SQL";

    // Stable case names are shared across providers; the temporal input retains provider precision.
    public static IEnumerable<TestCaseData> Cases()
    {
        string[] cases =
        [
            "ORDINARY-LINK-BEARING-STUDENT-SCHOOL-ASSOCIATION",
            "DESCRIPTOR-SCHOOL-TYPE",
            "EXTENSION-STUDENT-SCHOOL-ASSOCIATION",
            "SCHOOL-ADDRESS-PROPERTY-ABSENCE",
            "EXACT-NUMBERS",
            "SIGNED-INT64-MIN",
            "FRACTIONAL-SECOND",
        ];
        foreach (string name in cases)
        {
            foreach (string operation in new[] { "c", "u", "r" })
            {
                yield return new TestCaseData(name, operation);
            }
        }
    }

    [OneTimeSetUp]
    public async Task Setup()
    {
        string providerName = provider == CdcProvider.Postgresql ? "postgresql" : "sqlserver";
        _fixtures = MessageContractFixtureCatalog
            .LoadAll(AppContext.BaseDirectory)
            .Where(f =>
                f.SourceRecord.GetProperty("provider").GetString() == providerName
                && f.SourceRecord.GetProperty("operation").GetString() == "c"
            )
            .ToDictionary(f => f.ScenarioId, StringComparer.Ordinal);
        _artifacts =
            CoreCdc
                .CdcArtifactNameGenerator.Render(
                    new(
                        "contract",
                        "edfi",
                        "upserts",
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
        foreach (TestCaseData testCase in Cases())
        {
            string name = (string)testCase.Arguments[0]!;
            string operation = (string)testCase.Arguments[1]!;
            JsonNode source = JsonNode.Parse(Fixture(name).SourceRecord.GetRawText())!;
            source["operation"] = operation;
            source["value"]!["op"] = operation;
            if (operation == "u")
            {
                // Exercise UUID normalization as well as the update's available before row.
                string uuid = Fixture(name)
                    .CacheRow.GetProperty("documentUuid")
                    .GetString()!
                    .ToUpperInvariant();
                source["key"]!["DocumentUuid"] = uuid;
                source["value"]!["after"]!["DocumentUuid"] = uuid;
                JsonNode before = source["value"]!["after"]!.DeepClone();
                before["ContentVersion"] = 111;
                before["StreamEtag"] = "previous-opaque-etag";
                JsonNode previousDocument = JsonNode.Parse(before["DocumentJson"]!.GetValue<string>())!;
                previousDocument["beforeOnly"] = true;
                before["DocumentJson"] = previousDocument.ToJsonString();
                source["value"]!["before"] = before;
            }
            JsonElement input = JsonSerializer.SerializeToElement(source);
            MessageContractFixtureCatalog.ValidateSourceRecord(input);
            scenarios.Add(new(ScenarioId(name, operation), input, config, 7));
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
    }

    [TestCaseSource(nameof(Cases))]
    public void It_emits_the_complete_public_envelope_with_exact_document_values(
        string name,
        string operation
    )
    {
        JsonElement actual = Envelope(name, operation);
        MessageContractJson.ShouldEqual(actual, Fixture(name).ExpectedEnvelope);
        actual.GetProperty("contractVersion").ValueKind.Should().Be(JsonValueKind.Number);
        actual.GetProperty("contractVersion").GetInt32().Should().Be(1);
        actual.GetProperty("contentVersion").ValueKind.Should().Be(JsonValueKind.Number);
        actual
            .GetProperty("contentVersion")
            .GetInt64()
            .Should()
            .Be(Fixture(name).CacheRow.GetProperty("contentVersion").GetInt64());
        actual.GetProperty("document").ValueKind.Should().Be(JsonValueKind.Object);
        actual
            .GetProperty("documentUuid")
            .GetString()
            .Should()
            .Be(actual.GetProperty("document").GetProperty("id").GetString());
    }

    [TestCaseSource(nameof(Cases))]
    public void It_routes_the_unquoted_lowercase_uuid_key_to_the_derived_public_topic(
        string name,
        string operation
    )
    {
        JsonElement record = Record(name, operation);
        record.GetProperty("topic").GetString().Should().Be(_artifacts.TopicName);
        string uuid = Guid.Parse(Fixture(name).CacheRow.GetProperty("documentUuid").GetString()!)
            .ToString("D");
        Bytes(record, "keyBytes").Should().Equal(Encoding.UTF8.GetBytes(uuid));
        record.GetProperty("key").GetString().Should().Be(uuid);
        MessageContractJson.ShouldEqual(
            record.GetProperty("keySchema"),
            JsonSerializer.Deserialize<JsonElement>("""{"type":"STRING","optional":false}""")
        );
    }

    [TestCaseSource(nameof(Cases))]
    public void It_uses_the_exact_logical_bytes_handshake_and_converter_defensive_copy(
        string name,
        string operation
    )
    {
        JsonElement record = Record(name, operation);
        MessageContractJson.ShouldEqual(
            record.GetProperty("valueSchema"),
            JsonSerializer.Deserialize<JsonElement>(
                """
                {"type":"BYTES","optional":false,"name":"org.edfi.kafka.connect.data.DocumentStateJson","version":1}
                """
            )
        );
        record.GetProperty("converterBytesEqual").GetBoolean().Should().BeTrue();
        record.GetProperty("converterDefensiveCopy").GetBoolean().Should().BeTrue();
        record
            .GetProperty("value")
            .GetBytesFromBase64()
            .SequenceEqual(Bytes(record, "valueBytes"))
            .Should()
            .BeTrue("conversion must preserve the transform's bytes (payloads redacted)");
    }

    [TestCaseSource(nameof(Cases))]
    public void It_publishes_whole_second_document_time_and_removes_connect_metadata(
        string name,
        string operation
    )
    {
        JsonElement record = Record(name, operation);
        JsonElement envelope = Envelope(name, operation);
        string expected = Fixture(name).ExpectedEnvelope.GetProperty("lastModifiedAt").GetString()!;
        envelope.GetProperty("lastModifiedAt").GetString().Should().Be(expected);
        envelope.GetProperty("document").GetProperty("_lastModifiedDate").GetString().Should().Be(expected);
        expected.Should().MatchRegex(@"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z\z");
        record.GetProperty("headers").GetArrayLength().Should().Be(0);
        record.GetProperty("timestamp").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [TestCaseSource(nameof(Cases))]
    public void It_copies_the_opaque_etag_only_into_the_document_and_excludes_internal_fields(
        string name,
        string operation
    )
    {
        JsonElement envelope = Envelope(name, operation);
        envelope
            .GetProperty("document")
            .GetProperty("_etag")
            .GetString()
            .Should()
            .Be(Fixture(name).CacheRow.GetProperty("streamEtag").GetString());
        string[] names = PropertyNames(envelope).ToArray();
        names.Count(n => n == "_etag").Should().Be(1);
        string[] forbidden =
        [
            "DocumentId",
            "documentId",
            "ComputedAt",
            "computedAt",
            "StreamEtag",
            "streamEtag",
            "schema",
            "payload",
            "before",
            "after",
            "source",
            "op",
            "EdFiDoc",
            "deleted",
            "DocumentProjectionWork",
            "DocumentCacheState",
            "CacheGeneration",
            "cacheGeneration",
        ];
        names.Should().NotIntersectWith(forbidden);
    }

    private MessageContractFixture Fixture(string name) => _fixtures[$"MC-FIX-{ProviderPrefix}-{name}"];

    private string ScenarioId(string name, string operation) =>
        $"MC-UPSERT-{ProviderPrefix}-{name}-{operation.ToUpperInvariant()}";

    private JsonElement Record(string name, string operation)
    {
        MessageContractRunnerObservation observation = _observations[ScenarioId(name, operation)];
        observation.Status.Should().Be("retained", observation.ToString());
        return observation.Data.GetProperty("record");
    }

    private JsonElement Envelope(string name, string operation)
    {
        // Strict decoding rejects malformed UTF-8 instead of silently replacing invalid sequences.
        string json = new UTF8Encoding(false, true).GetString(Bytes(Record(name, operation), "valueBytes"));
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static byte[] Bytes(JsonElement record, string property)
    {
        JsonElement serialized = record.GetProperty(property);
        serialized.GetProperty("kind").GetString().Should().Be("bytes");
        byte[] bytes = serialized.GetProperty("base64").GetBytesFromBase64();
        bytes.Length.Should().Be(serialized.GetProperty("length").GetInt32()).And.BePositive();
        return bytes;
    }

    private static IEnumerable<string> PropertyNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                yield return property.Name;
                foreach (string name in PropertyNames(property.Value))
                {
                    yield return name;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                foreach (string name in PropertyNames(item))
                {
                    yield return name;
                }
            }
        }
    }
}
