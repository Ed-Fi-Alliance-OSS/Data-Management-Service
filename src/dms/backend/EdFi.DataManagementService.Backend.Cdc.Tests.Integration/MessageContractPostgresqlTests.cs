// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private IReadOnlyList<MessageContractProviderRow> _rows = null!;
    private IReadOnlyList<MessageContractProviderRow> _updates = null!;
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
        (_rows, _updates) = MessageContractProviderRows.Load("postgresql", "2026-08-01T23:59:59.999999Z");
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

        foreach (MessageContractProviderRow row in _rows.Skip(1))
        {
            await fixture.WriteMaterializedRowAsync(row.CacheRow, false, token);
        }
        foreach (MessageContractProviderRow row in _updates)
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

        foreach (MessageContractProviderRow row in _rows)
        {
            await fixture.DeleteCanonicalRowAsync(row.DocumentId, token);
        }
        await CapturePhaseAsync(fixture, "DELETE", token);
        await fixture.TruncatePostgresqlDocumentsAsync(token);
        await CapturePhaseAsync(fixture, "TRUNCATE", token);
        _source = await fixture.ReadSourceObservationsAsync(token);
        await MessageContractProviderAssertions.AssertTopicInventoryAsync(fixture, _request, token);
        await MessageContractProviderObservations.RetainEvidenceAsync(
            "MessageContractPostgresql",
            "PostgreSQL",
            _fences,
            _source,
            _public,
            _progress,
            token
        );
        await TestContext.Out.WriteLineAsync(
            $"Source observations: {_source.Count}; bounded schema/routing metadata only; no provider values retained."
        );
    }

    private async Task CapturePhaseAsync(
        CdcConnectorTemplatePinnedImageFixture fixture,
        string phase,
        CancellationToken token
    )
    {
        _fences.Add(await fixture.FencePostgresqlSourceAsync(_request, phase, token));
        await MessageContractProviderObservations.CapturePhaseAsync(
            fixture,
            _request,
            phase,
            _public,
            _progress,
            token
        );
    }

    [Test]
    [Property("ScenarioId", "MC-POSTGRESQL-SNAPSHOT-LIVE")]
    public void It_publishes_complete_shared_snapshot_and_live_envelopes_on_fixed_key_partitions()
    {
        _public["SNAPSHOT"].Records.Should().HaveCount(1);
        MessageContractProviderAssertions.AssertUpsert(
            _request,
            _public["SNAPSHOT"].Records.Single(),
            _rows[0]
        );
        _public["LIVE"].Records.Should().HaveCount(7);
        foreach (MessageContractProviderRow row in _rows.Skip(1).Concat(_updates))
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
            MessageContractProviderAssertions.AssertUpsert(_request, record, row);
        }
        foreach (MessageContractProviderRow row in _rows)
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
            MessageContractProviderAssertions.AssertSchema(
                input.GetProperty("jsonSchema"),
                "STRING",
                "io.debezium.data.Json",
                1
            );
            MessageContractProviderAssertions.AssertSchema(
                input.GetProperty("timeSchema"),
                "STRING",
                "io.debezium.time.ZonedTimestamp",
                1
            );
            MessageContractProviderAssertions.AssertSchema(
                input.GetProperty("versionSchema"),
                "INT64",
                "",
                0
            );
        }
    }

    [Test]
    [Property("ScenarioId", "MC-POSTGRESQL-DELETE-EXCLUSION")]
    public void It_emits_one_true_tombstone_per_canonical_delete_and_drops_excluded_operations()
    {
        _public["DELETE"].Records.Should().HaveCount(4);
        foreach (MessageContractProviderRow row in _rows)
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

    private static void AssertUuidKey(JsonElement input)
    {
        input.GetProperty("keyIsStruct").GetBoolean().Should().BeTrue();
        input.GetProperty("keyFieldCount").GetInt32().Should().Be(1);
        input.GetProperty("uuid").GetString().Should().NotBeNullOrEmpty();
        input.GetProperty("schemaIsDms").GetBoolean().Should().BeTrue();
        MessageContractProviderAssertions.AssertSchema(
            input.GetProperty("keyUuidSchema"),
            "STRING",
            "io.debezium.data.Uuid",
            1
        );
    }

    private static string Kind(JsonElement input) => input.GetProperty("kind").GetString()!;

    private static string Operation(JsonElement input) => input.GetProperty("operation").GetString()!;
}
