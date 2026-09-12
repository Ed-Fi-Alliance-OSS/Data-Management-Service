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

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql, Category = "PostgresqlIntegration")]
[TestFixture(CdcProvider.SqlServer, Category = "MssqlIntegration")]
[Category("DatabaseIntegration")]
[Category("CdcMessageContract")]
[Category("CdcMessageContractSerialized")]
public sealed class Given_MessageContractRunner_smoke(CdcProvider provider)
{
    private MessageContractRunnerResult _result = null!;
    private MessageContractFixture _upsert = null!;
    private MessageContractRunnerScenario[] _scenarios = [];

    [OneTimeSetUp]
    public async Task Setup()
    {
        string providerName = provider == CdcProvider.Postgresql ? "postgresql" : "sqlserver";
        MessageContractFixture[] fixtures = MessageContractFixtureCatalog
            .LoadAll(AppContext.BaseDirectory)
            .Where(f => f.SourceRecord.GetProperty("provider").GetString() == providerName)
            .ToArray();
        _upsert = fixtures[0];
        JsonNode dropped = JsonNode.Parse(_upsert.SourceRecord.GetRawText())!;
        dropped["operation"] = "d";
        dropped["value"]!["op"] = "d";
        JsonNode failed = JsonNode.Parse(_upsert.SourceRecord.GetRawText())!;
        failed["key"]!["DocumentUuid"] = "synthetic-secret-body-sentinel";
        JsonNode progress = JsonNode.Parse(_upsert.SourceRecord.GetRawText())!;
        progress["sourceTopic"] = "__debezium-heartbeat.contract-source";
        progress["operation"] = "native-heartbeat";
        progress["keySchema"] = null;
        progress["key"] = null;
        progress["valueSchema"] = null;
        progress["value"] = null;
        Dictionary<string, string> config = new()
        {
            ["provider"] = providerName,
            ["target.topic"] = "edfi.documents",
            ["progress.topic"] = "edfi.documents.cdc-progress",
        };
        _scenarios =
        [
            new("MC-RUN-UPSERT", _upsert.SourceRecord, config, 7),
            new(
                "MC-RUN-TOMBSTONE",
                fixtures
                    .Single(f => f.ScenarioId.EndsWith("DOCUMENT-DELETE", StringComparison.Ordinal))
                    .SourceRecord,
                config,
                7
            ),
            new("MC-RUN-DROP", JsonSerializer.SerializeToElement(dropped), config, 7),
            new("MC-RUN-FAIL", JsonSerializer.SerializeToElement(failed), config, 7),
            new("MC-RUN-PROGRESS", JsonSerializer.SerializeToElement(progress), config, 1),
        ];
        try
        {
            _result = await MessageContractRunner.FromEnvironment().RunAsync(_scenarios);
        }
        catch (MessageContractRunnerPrerequisiteException failure)
        {
            CdcConnectorTemplateSmokeSettings
                .FromEnvironment(provider)
                .StopOnPrerequisiteFailure(provider, failure.Message);
        }
    }

    [Test]
    public void It_distinguishes_retained_dropped_and_failed_records() =>
        _result
            .Observations.Select(o => o.Status)
            .Should()
            .Equal("retained", "retained", "dropped", "failed", "retained");

    [Test]
    public void It_serializes_the_shared_upsert_with_the_real_converters()
    {
        JsonElement record = Record(0);
        record.GetProperty("topic").GetString().Should().Be("edfi.documents");
        using JsonDocument value = JsonDocument.Parse(Bytes(record, "valueBytes"));
        MessageContractJson
            .Equivalent(value.RootElement, _upsert.ExpectedEnvelope)
            .Should()
            .BeTrue("the emitted envelope must match the shared fixture");
        Encoding
            .UTF8.GetString(Bytes(record, "keyBytes"))
            .Should()
            .Be(_upsert.CacheRow.GetProperty("documentUuid").GetString());
        record.GetProperty("valueSchema").GetProperty("type").GetString().Should().Be("BYTES");
        record.GetProperty("converterBytesEqual").GetBoolean().Should().BeTrue();
        record.GetProperty("converterDefensiveCopy").GetBoolean().Should().BeTrue();
        record.GetProperty("headers").GetArrayLength().Should().Be(0);
        record.GetProperty("timestamp").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Test]
    public void It_reports_a_true_kafka_null_and_same_partition_for_the_delete()
    {
        JsonElement record = Record(1);
        record.GetProperty("valueBytes").GetProperty("kind").GetString().Should().Be("kafka-null");
        record.GetProperty("valueBytes").TryGetProperty("base64", out _).Should().BeFalse();
        record.GetProperty("valueSchema").ValueKind.Should().Be(JsonValueKind.Null);
        Bytes(record, "keyBytes").Should().Equal(Bytes(Record(0), "keyBytes"));
        record.GetProperty("partition").GetInt32().Should().Be(Record(0).GetProperty("partition").GetInt32());
    }

    [Test]
    public void It_preserves_progress_source_metadata_and_uses_the_real_string_converter()
    {
        JsonElement record = Record(4);
        Encoding.UTF8.GetString(Bytes(record, "keyBytes")).Should().Be("cdc-progress");
        Encoding.UTF8.GetString(Bytes(record, "valueBytes")).Should().Be("\"native-heartbeat\"");
        record.GetProperty("topic").GetString().Should().Be("edfi.documents.cdc-progress");
        record.GetProperty("partition").GetInt32().Should().Be(0);
        foreach (string property in new[] { "sourcePartition", "sourceOffset", "timestamp", "headers" })
        {
            MessageContractJson
                .Equivalent(record.GetProperty(property), _scenarios[4].SourceRecord.GetProperty(property))
                .Should()
                .BeTrue($"progress preserves {property}");
        }
    }

    [Test]
    public void It_exposes_only_bounded_artifact_failure_metadata()
    {
        JsonElement failure = _result.Observations[3].Data.GetProperty("failure");
        failure.GetProperty("stage").GetString().Should().Be("transform");
        failure.GetProperty("category").GetString().Should().Be("DataException");
        failure.GetProperty("reason").GetString().Should().Be("INVALID_DOCUMENT_UUID");
        failure.GetRawText().Should().NotContain("synthetic-secret-body-sentinel");
        failure.GetRawText().Length.Should().BeLessThan(1024);
        _result.Observations[3].Data.TryGetProperty("record", out _).Should().BeFalse();
        _result.Observations[2].Data.TryGetProperty("record", out _).Should().BeFalse();
    }

    [Test]
    public void It_reports_a_missing_published_class_without_plugin_logs()
    {
        MessageContractRunnerPrerequisiteException failure =
            Assert.ThrowsAsync<MessageContractRunnerPrerequisiteException>(async () =>
                await MessageContractRunner
                    .FromEnvironment()
                    .RunAsync([], CancellationToken.None, ["org.edfi.contract.MissingClass"])
            )!;
        failure.Reason.Should().Be("required-class-missing");
        failure.InnerException.Should().BeNull();
    }

    [Test]
    public void It_records_the_immutable_runtime_provenance() =>
        _result
            .ConnectImage.Should()
            .Be(Environment.GetEnvironmentVariable(MessageContractRunner.ImageVariable));

    private JsonElement Record(int index)
    {
        _result.Observations[index].Status.Should().Be("retained", _result.Observations[index].ToString());
        return _result.Observations[index].Data.GetProperty("record");
    }

    private static byte[] Bytes(JsonElement record, string property)
    {
        record.GetProperty(property).GetProperty("kind").GetString().Should().Be("bytes");
        byte[] bytes = record.GetProperty(property).GetProperty("base64").GetBytesFromBase64();
        bytes.Length.Should().Be(record.GetProperty(property).GetProperty("length").GetInt32());
        return bytes;
    }
}
