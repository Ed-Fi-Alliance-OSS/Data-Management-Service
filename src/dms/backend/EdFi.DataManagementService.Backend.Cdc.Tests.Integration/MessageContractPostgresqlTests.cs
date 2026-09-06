// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("CdcMessageContract")]
[Category("CdcMessageContractKafka")]
[Category("PostgresqlIntegration")]
public sealed class Given_MessageContractPostgresql
{
    private CdcConnectorTemplateRequest _request = null!;
    private IReadOnlyList<JsonElement> _source = null!;
    private readonly Dictionary<string, MessageContractKafkaScan> _public = [];
    private readonly Dictionary<string, MessageContractKafkaScan> _progress = [];
    private readonly List<ProviderRow> _rows = [];
    private readonly List<ProviderRow> _updates = [];
    private readonly List<MessageContractPostgresqlFence> _fences = [];

    [OneTimeSetUp]
    public async Task Setup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        CancellationToken token = timeout.Token;
        await using CdcConnectorTemplatePinnedImageFixture fixture =
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(CdcProvider.Postgresql, token);
        _request = await fixture.CreateRequestAsync(token, partitionCount: 7);
        await fixture.AssertPostgresqlCaptureInventoryAsync(token);
        await fixture.InstallSourceObserverAsync(token);
        LoadRows();
        await fixture.WriteMaterializedRowAsync(_rows[0].CacheRow, false, token);
        CdcConnectorTemplateResult rendered = fixture.Render(_request);
        rendered
            .Config["message.key.columns"]
            .Should()
            .Be(@"dms\.DocumentCache:DocumentUuid;dms\.Document:DocumentUuid");
        rendered
            .Config["table.include.list"]
            .Should()
            .Be(@"dms\.DocumentCache,dms\.Document,dms\.CdcHeartbeat");
        rendered.Config["tombstones.on.delete"].Should().Be("false");
        await fixture.AssertRuntimeLoadsRequiredClassesAsync(rendered, token);
        await fixture.RegisterRenderedConnectorConfigDirectlyAsync(
            rendered,
            token,
            observeSourceRecords: true
        );
        await fixture.AssertRegisteredConnectorReachesRunningStateAsync(_request, token);
        await fixture.AssertObservedConnectorConfigAsync(rendered, token);
        await CapturePhaseAsync(fixture, "SNAPSHOT", token);

        foreach (ProviderRow row in _rows.Skip(1))
        {
            await fixture.WriteMaterializedRowAsync(row.CacheRow, false, token);
        }
        foreach (ProviderRow row in _updates)
        {
            await fixture.WriteMaterializedRowAsync(row.CacheRow, true, token);
        }
        await CapturePhaseAsync(fixture, "LIVE", token);

        // Cache delete, canonical create/update, and the later truncate must have no public output.
        await fixture.DeleteCacheRowAsync(_rows[1].DocumentId, token);
        JsonNode canonicalOnly = JsonNode.Parse(_rows[0].CacheRow.GetRawText())!;
        canonicalOnly["documentId"] = 980001;
        canonicalOnly["documentUuid"] = "aaaaaaaa-bbbb-cccc-dddd-000000000999";
        await fixture.WriteCanonicalRowAsync(JsonSerializer.SerializeToElement(canonicalOnly), token);
        await fixture.UpdateCanonicalRowAsync(_updates[0].CacheRow, token);
        await CapturePhaseAsync(fixture, "EXCLUDED", token);

        await fixture.WriteProjectionWorkAsync(_rows[0].DocumentId, token);
        await fixture.UpdateProjectionWorkAsync(_rows[0].DocumentId, token);
        await fixture.DeleteProjectionWorkAsync(_rows[0].DocumentId, token);
        await CapturePhaseAsync(fixture, "WORK", token);
        await fixture.AssertPostgresqlCaptureInventoryAsync(token);

        foreach (ProviderRow row in _rows)
        {
            await fixture.DeleteCanonicalRowAsync(row.DocumentId, token);
        }
        await CapturePhaseAsync(fixture, "DELETE", token);
        await fixture.TruncatePostgresqlDocumentsAsync(token);
        await CapturePhaseAsync(fixture, "TRUNCATE", token);
        _source = await fixture.ReadSourceObservationsAsync(token);
        await AssertTopicInventoryAsync(fixture, token);
        await RetainEvidenceAsync(token);
        await TestContext.Out.WriteLineAsync(
            $"Source observations: {_source.Count}; bounded schema/routing metadata only; no provider values retained."
        );
    }

    private async Task RetainEvidenceAsync(CancellationToken token)
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "TestResults",
            "MessageContractPostgresql"
        );
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"observations-{Guid.NewGuid():N}.json");
        var evidence = new
        {
            ConnectImage = Environment.GetEnvironmentVariable(MessageContractRunner.ImageVariable)
                ?? string.Empty,
            Fences = _fences,
            Source = _source,
            Scans = new[]
            {
                (Kind: "public", Scans: _public),
                (Kind: "progress", Scans: _progress),
            }.SelectMany(group =>
                group.Scans.Select(phase => new
                {
                    group.Kind,
                    Phase = phase.Key,
                    Bounds = phase.Value.CompletedBoundaries.Select(b => new
                    {
                        b.Partition,
                        b.StartOffset,
                        b.EndOffset,
                    }),
                    Records = phase.Value.Records.Select(r => new
                    {
                        r.Partition,
                        r.Offset,
                        KeyBytes = r.Key.Bytes.Length,
                        ValueBytes = r.Value.Bytes.Length,
                        KafkaNull = r.Value.IsNull,
                    }),
                })
            ),
        };
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }),
            token
        );
        TestContext.AddTestAttachment(
            path,
            "Bounded PostgreSQL source schemas and broker observation summaries; payloads omitted"
        );
    }

    private void LoadRows()
    {
        MessageContractFixture[] shared = MessageContractFixtureCatalog
            .LoadAll(AppContext.BaseDirectory)
            .Where(f =>
                f.SourceRecord.GetProperty("provider").GetString() == "postgresql"
                && f.SourceRecord.GetProperty("operation").GetString() == "c"
            )
            .DistinctBy(f => f.MaterializedCase)
            .ToArray();
        shared.Should().HaveCount(4);
        string root = MessageContractFixtureCatalog.ResolveFixtureRoot(AppContext.BaseDirectory);
        using JsonDocument vectors = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "cdc/message-contract/partition-vectors.json"))
        );
        JsonElement[] keys = vectors.RootElement.GetProperty("vectors").EnumerateArray().ToArray();
        for (int index = 0; index < shared.Length; index++)
        {
            JsonNode cache = JsonNode.Parse(shared[index].CacheRow.GetRawText())!;
            JsonNode expected = JsonNode.Parse(shared[index].ExpectedEnvelope.GetRawText())!;
            string uuid = keys[index].GetProperty("uuid").GetString()!;
            int partition = keys[index]
                .GetProperty("partitions")
                .EnumerateArray()
                .Single(p => p.GetProperty("count").GetInt32() == 7)
                .GetProperty("expected")
                .GetInt32();
            cache["documentUuid"] = uuid;
            cache["documentJson"]!["id"] = uuid;
            expected["documentUuid"] = uuid;
            expected["document"]!["id"] = uuid;
            _rows.Add(
                new(
                    JsonSerializer.SerializeToElement(cache),
                    JsonSerializer.SerializeToElement(expected),
                    partition
                )
            );
            long version = cache["contentVersion"]!.GetValue<long>() + 1000;
            cache["contentVersion"] = version;
            cache["streamEtag"] = $"opaque-live-{version}";
            cache["lastModifiedAt"] = "2026-08-01T23:59:59.999999Z";
            cache["documentJson"]!["_lastModifiedDate"] = "2026-08-01T23:59:59Z";
            expected["contentVersion"] = version;
            expected["lastModifiedAt"] = "2026-08-01T23:59:59Z";
            expected["document"]!["_lastModifiedDate"] = "2026-08-01T23:59:59Z";
            expected["document"]!["_etag"] = $"opaque-live-{version}";
            _updates.Add(
                new(
                    JsonSerializer.SerializeToElement(cache),
                    JsonSerializer.SerializeToElement(expected),
                    partition
                )
            );
        }
    }

    private async Task CapturePhaseAsync(
        CdcConnectorTemplatePinnedImageFixture fixture,
        string phase,
        CancellationToken token
    )
    {
        _fences.Add(await fixture.FencePostgresqlSourceAsync(_request, phase, token));
        await CaptureAsync(_public, _request.PublicTopicName);
        await CaptureAsync(_progress, _request.ProgressTopicName);
        async Task CaptureAsync(Dictionary<string, MessageContractKafkaScan> scans, string topic)
        {
            var bounds = await fixture.CaptureKafkaBoundariesAsync(topic, token);
            if (scans.Count > 0)
            {
                var previous = scans.Last().Value.CompletedBoundaries;
                bounds = bounds
                    .Select(b =>
                        b with
                        {
                            StartOffset = previous.Single(p => p.Partition == b.Partition).EndOffset,
                        }
                    )
                    .ToArray();
            }
            MessageContractKafkaScan scan = await fixture.ConsumeThroughAsync(bounds, token);
            scans.Add(phase, scan);
            string kind = topic == _request.PublicTopicName ? "public" : "progress";
            await TestContext.Out.WriteLineAsync(
                $"{phase} {kind}: {scan.Records.Count} records; "
                    + string.Join(", ", bounds.Select(b => $"p{b.Partition}=[{b.StartOffset},{b.EndOffset})"))
            );
        }
    }

    [Test]
    [Property("ScenarioId", "MC-POSTGRESQL-SNAPSHOT-LIVE")]
    public void It_publishes_complete_shared_snapshot_and_live_envelopes_on_fixed_key_partitions()
    {
        _public["SNAPSHOT"].Records.Should().HaveCount(1);
        AssertUpsert(_public["SNAPSHOT"].Records.Single(), _rows[0]);
        _public["LIVE"].Records.Should().HaveCount(7);
        foreach (ProviderRow row in _rows.Skip(1).Concat(_updates))
        {
            MessageContractKafkaRecord record = _public["LIVE"]
                .Records.Single(r =>
                    Encoding.UTF8.GetString(r.Key.Bytes) == row.Uuid
                    && !r.Value.IsNull
                    && JsonSerializer
                        .Deserialize<JsonElement>(r.Value.Bytes)
                        .GetProperty("contentVersion")
                        .GetInt64() == row.Expected.GetProperty("contentVersion").GetInt64()
                );
            AssertUpsert(record, row);
        }
        foreach (ProviderRow row in _rows)
        {
            var records = _public
                .Values.SelectMany(s => s.Records)
                .Where(r => Encoding.UTF8.GetString(r.Key.Bytes) == row.Uuid)
                .ToArray();
            records.Select(r => r.Partition).Distinct().Should().Equal(row.Partition);
            records.Select(r => r.Offset).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        }
        JsonElement[] cache = _source
            .Where(r => Kind(r) == "DocumentCache" && Operation(r) is "r" or "c" or "u")
            .ToArray();
        cache.Should().HaveCount(8);
        cache.Select(Operation).Should().Contain(["r", "c", "u"]);
        foreach (JsonElement input in cache)
        {
            AssertUuidKey(input);
            AssertSchema(input.GetProperty("jsonSchema"), "STRING", "io.debezium.data.Json", 1);
            AssertSchema(input.GetProperty("timeSchema"), "STRING", "io.debezium.time.ZonedTimestamp", 1);
            AssertSchema(input.GetProperty("versionSchema"), "INT64", "", 0);
        }
    }

    [Test]
    [Property("ScenarioId", "MC-POSTGRESQL-DELETE-EXCLUSION")]
    public void It_emits_one_true_tombstone_per_canonical_delete_and_drops_excluded_operations()
    {
        _public["DELETE"].Records.Should().HaveCount(4);
        foreach (ProviderRow row in _rows)
        {
            var record = _public["DELETE"]
                .Records.Single(r => Encoding.UTF8.GetString(r.Key.Bytes) == row.Uuid);
            record.Key.IsNull.Should().BeFalse();
            record.Key.Bytes.Should().Equal(Encoding.UTF8.GetBytes(row.Uuid));
            record.Value.IsNull.Should().BeTrue();
            record.Value.Bytes.Should().BeEmpty();
            record.Topic.Should().Be(_request.PublicTopicName);
            record.Partition.Should().Be(row.Partition);
            record.Headers.Should().BeEmpty();
        }
        _public["EXCLUDED"].Records.Should().BeEmpty();
        _public["TRUNCATE"].Records.Should().BeEmpty();
        var deletes = _source.Where(r => Kind(r) == "Document" && Operation(r) == "d").ToArray();
        deletes.Should().HaveCount(4);
        foreach (JsonElement input in deletes)
        {
            AssertUuidKey(input);
            input.GetProperty("beforeUuidMatches").GetBoolean().Should().BeTrue();
        }
        _source.Where(r => Kind(r) == "Document").Select(Operation).Should().Contain(["r", "c", "u", "d"]);
        _source.Where(r => Kind(r) == "DocumentCache").Select(Operation).Should().Contain("d");
        _source
            .Select(Operation)
            .Should()
            .NotContain("t", "the qualified publication excludes truncate capture");
        _source
            .Where(r => Kind(r) is "Document" or "DocumentCache")
            .All(r => !r.GetProperty("valueIsNull").GetBoolean())
            .Should()
            .BeTrue("Debezium automatic tombstones are disabled");
    }

    [Test]
    [Property("ScenarioId", "MC-POSTGRESQL-PROGRESS")]
    public void It_publishes_structured_and_native_heartbeats_with_the_fixed_key_only_on_progress()
    {
        var records = _progress.Values.SelectMany(s => s.Records).ToArray();
        records.Should().NotBeEmpty();
        int relational = 0;
        int native = 0;
        foreach (MessageContractKafkaRecord record in records)
        {
            record.Topic.Should().Be(_request.ProgressTopicName);
            record.Partition.Should().Be(0);
            record.Key.IsNull.Should().BeFalse();
            record.Key.Bytes.Should().Equal(Convert.FromHexString("6364632D70726F6772657373"));
            record.Value.IsNull.Should().BeFalse();
            JsonElement value = JsonSerializer.Deserialize<JsonElement>(record.Value.Bytes);
            value.TryGetProperty("schema", out _).Should().BeFalse();
            value.TryGetProperty("payload", out _).Should().BeFalse();
            if (value.TryGetProperty("source", out JsonElement source))
            {
                source.GetProperty("schema").GetString().Should().Be("dms");
                source.GetProperty("table").GetString().Should().Be("CdcHeartbeat");
                (source.GetProperty("name").GetString() == _request.ConnectorName.Value).Should().BeTrue();
                relational++;
            }
            else
            {
                value.EnumerateObject().Select(p => p.Name).Should().Equal("ts_ms");
                value.GetProperty("ts_ms").GetInt64().Should().BeGreaterThan(0);
                native++;
            }
        }
        relational.Should().BeGreaterThan(0);
        native.Should().BeGreaterThan(0);
        _source
            .Where(r => Kind(r) == "CdcHeartbeat")
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(r => r.GetProperty("keyIsStruct").GetBoolean());
        _source
            .Where(r => Kind(r) == "native")
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(r =>
                r.GetProperty("keyIsStruct").GetBoolean()
                && !r.GetProperty("keyIsNull").GetBoolean()
                && r.GetProperty("keyFieldCount").GetInt32() == 1
            );
        foreach (JsonElement input in _source)
        {
            input.GetProperty("serverMatches").GetBoolean().Should().BeTrue();
            input.GetProperty("topicMatches").GetBoolean().Should().BeTrue();
            input.GetProperty("partitionFields").GetInt32().Should().Be(1);
        }
        _public
            .Values.SelectMany(s => s.Records)
            .All(r => !r.Key.Bytes.SequenceEqual(Encoding.UTF8.GetBytes("cdc-progress")))
            .Should()
            .BeTrue();
    }

    [Test]
    [Property("ScenarioId", "MC-POSTGRESQL-WORK-EXCLUSION")]
    public void It_excludes_work_activity_from_capture_and_fenced_public_and_progress_observations()
    {
        _public["WORK"].Records.Should().BeEmpty();
        _progress["WORK"].Records.Should().NotBeEmpty("a retained heartbeat crosses the post-work WAL fence");
        _source
            .Select(Kind)
            .Should()
            .NotContain("other", "no unexpected table or work-table input may reach the transform");
        // All progress records, including the work window, are classified by source metadata in the progress test.
        foreach (var record in _progress["WORK"].Records)
        {
            JsonElement value = JsonSerializer.Deserialize<JsonElement>(record.Value.Bytes);
            if (value.TryGetProperty("source", out var source))
            {
                source.GetProperty("table").GetString().Should().Be("CdcHeartbeat");
            }
            else
            {
                value.EnumerateObject().Select(p => p.Name).Should().Equal("ts_ms");
            }
        }
    }

    private async Task AssertTopicInventoryAsync(
        CdcConnectorTemplatePinnedImageFixture fixture,
        CancellationToken token
    )
    {
        using IAdminClient admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = fixture.HostKafkaBootstrapServers }
        )
            .SetLogHandler((_, _) => { })
            .Build();
        foreach (string topic in new[] { _request.PublicTopicName, _request.ProgressTopicName })
        {
            var config = await admin.DescribeConfigsAsync([
                new ConfigResource { Type = ResourceType.Topic, Name = topic },
            ]);
            config.Single().Entries["cleanup.policy"].Value.Should().Be("compact");
            long.Parse(config.Single().Entries["delete.retention.ms"].Value)
                .Should()
                .BeGreaterThanOrEqualTo(604800000);
            var bounds = await fixture.CaptureKafkaBoundariesAsync(topic, token);
            bounds.Should().HaveCount(topic == _request.PublicTopicName ? 7 : 1);
        }
        Metadata metadata = admin.GetMetadata(TimeSpan.FromSeconds(10));
        metadata
            .Topics.Select(t => t.Topic)
            .Where(t =>
                t.StartsWith(_request.ConnectorName.Value + ".", StringComparison.Ordinal)
                || t.StartsWith("__debezium-heartbeat.", StringComparison.Ordinal)
            )
            .Should()
            .BeEmpty("raw source topics must not be produced");
    }

    private void AssertUpsert(MessageContractKafkaRecord record, ProviderRow row)
    {
        record.Topic.Should().Be(_request.PublicTopicName);
        record.Partition.Should().Be(row.Partition);
        record.Key.IsNull.Should().BeFalse();
        record.Key.Bytes.Should().Equal(Encoding.UTF8.GetBytes(row.Uuid));
        record.Value.IsNull.Should().BeFalse();
        record.Headers.Should().BeEmpty();
        MessageContractJson.ShouldEqual(
            JsonSerializer.Deserialize<JsonElement>(record.Value.Bytes),
            row.Expected
        );
    }

    private static void AssertUuidKey(JsonElement input)
    {
        input.GetProperty("keyIsStruct").GetBoolean().Should().BeTrue();
        input.GetProperty("keyFieldCount").GetInt32().Should().Be(1);
        input.GetProperty("uuid").GetString().Should().NotBeNullOrEmpty();
        input.GetProperty("schemaIsDms").GetBoolean().Should().BeTrue();
        AssertSchema(input.GetProperty("keyUuidSchema"), "STRING", "io.debezium.data.Uuid", 1);
    }

    private static void AssertSchema(JsonElement schema, string type, string name, int version)
    {
        schema.GetProperty("type").GetString().Should().Be(type);
        schema.GetProperty("name").GetString().Should().Be(name);
        schema.GetProperty("version").GetInt32().Should().Be(version);
        schema.GetProperty("optional").GetBoolean().Should().BeFalse();
    }

    private static string Kind(JsonElement input) => input.GetProperty("kind").GetString()!;

    private static string Operation(JsonElement input) => input.GetProperty("operation").GetString()!;

    private sealed record ProviderRow(JsonElement CacheRow, JsonElement Expected, int Partition)
    {
        public long DocumentId => CacheRow.GetProperty("documentId").GetInt64();
        public string Uuid => CacheRow.GetProperty("documentUuid").GetString()!;

        public override string ToString() => $"Provider fixture row in partition {Partition}";
    }
}
