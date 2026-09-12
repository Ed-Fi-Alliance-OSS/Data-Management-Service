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

[TestFixture(CdcProvider.Postgresql, "MC-RETRY-PG", Category = "PostgresqlIntegration")]
[TestFixture(CdcProvider.SqlServer, "MC-RETRY-SQL", Category = "MssqlIntegration")]
[Category("DatabaseIntegration")]
[Category("CdcMessageContract")]
[Category("CdcMessageContractKafka")]
public sealed class Given_MessageContractRetry(CdcProvider provider, string scenarioPrefix)
{
    private MessageContractFixture _shared = null!;
    private JsonElement _updatedEnvelope;
    private CdcConnectorTemplateRequest _request = null!;
    private MessageContractInterruptionEvidence _interruption = null!;
    private MessageContractKafkaScan _baseline = null!;
    private MessageContractKafkaScan _recovered = null!;
    private readonly List<JsonElement> _fences = [];
    private IReadOnlyList<JsonElement> _pendingSource = [];
    private IReadOnlyList<JsonElement> _replayedSource = [];
    private bool _offsetAdvanced;
    private bool _partitionIdentityRetained;
    private string _phase = "startup";

    [OneTimeSetUp]
    public async Task Setup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        CancellationToken token = timeout.Token;
        await using CdcConnectorTemplatePinnedImageFixture fixture =
            await CdcConnectorTemplatePinnedImageFixture.StartAsync(provider, token);
        try
        {
            _request = await fixture.CreateRequestAsync(token, partitionCount: 7);
            await fixture.AssertMessageContractSourceLayoutAsync(token);
            if (provider == CdcProvider.SqlServer)
            {
                await fixture.AssertSqlServerCaptureInventoryAsync(token);
            }
            await fixture.InstallSourceObserverAsync(token);
            CdcConnectorTemplateResult rendered = fixture.Render(_request);
            rendered.Config["tasks.max"].Should().Be("1");
            rendered.Config["producer.override.enable.idempotence"].Should().Be("true");
            rendered.Config["producer.override.acks"].Should().Be("all");
            rendered.Config["producer.override.retries"].Should().Be(int.MaxValue.ToString());
            int.Parse(rendered.Config["producer.override.max.in.flight.requests.per.connection"])
                .Should()
                .BeInRange(1, 5);
            await fixture.AssertRuntimeLoadsRequiredClassesAsync(rendered, token);
            await fixture.RegisterRenderedConnectorConfigDirectlyAsync(
                rendered,
                token,
                observeSourceRecords: true
            );
            await fixture.AssertRegisteredConnectorReachesRunningStateAsync(_request, token);
            await fixture.AssertObservedConnectorConfigAsync(rendered, token);
            await FenceAsync("STREAMING");

            string prefix = provider == CdcProvider.Postgresql ? "PG" : "SQL";
            _shared = MessageContractFixtureCatalog
                .LoadAll(AppContext.BaseDirectory)
                .Single(f =>
                    f.ScenarioId == $"MC-FIX-{prefix}-ORDINARY-LINK-BEARING-STUDENT-SCHOOL-ASSOCIATION"
                );
            await fixture.WriteMaterializedRowAsync(_shared.CacheRow, false, token);
            await FenceAsync("BASELINE");
            var baselineOffset = await fixture.TryReadCommittedSourceOffsetAsync(_request, token);
            baselineOffset.Should().NotBeNull();
            _baseline = await fixture.ConsumeThroughAsync(
                await fixture.CaptureKafkaBoundariesAsync(_request.PublicTopicName, token),
                token
            );
            _baseline.Records.Count.Should().BeGreaterThan(0);
            _baseline.Records.All(r => !r.Value.IsNull).Should().BeTrue();
            int sourceBeforeFault = (await fixture.ReadSourceObservationsAsync(token)).Count;

            JsonNode updated = JsonNode.Parse(_shared.CacheRow.GetRawText())!;
            JsonNode expected = JsonNode.Parse(_shared.ExpectedEnvelope.GetRawText())!;
            long version = _shared.CacheRow.GetProperty("contentVersion").GetInt64() + 1;
            updated["contentVersion"] = version;
            updated["streamEtag"] = "opaque-retry-update";
            expected["contentVersion"] = version;
            expected["document"]!["_etag"] = "opaque-retry-update";
            _updatedEnvelope = JsonSerializer.SerializeToElement(expected);
            _phase = "interrupted-delivery";
            _interruption = await fixture.InterruptDeliveryAndRestartWorkerAsync(
                async cancellation =>
                {
                    await fixture.WriteMaterializedRowAsync(
                        JsonSerializer.SerializeToElement(updated),
                        true,
                        cancellation
                    );
                    await fixture.DeleteCanonicalRowAsync(
                        _shared.CacheRow.GetProperty("documentId").GetInt64(),
                        cancellation
                    );
                    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(90);
                    while (DateTimeOffset.UtcNow < deadline)
                    {
                        var source = await fixture.ReadSourceObservationsAsync(cancellation);
                        _pendingSource = source.Skip(sourceBeforeFault).ToArray();
                        if (HasUpdateAndDelete(_pendingSource))
                        {
                            return;
                        }
                        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation);
                    }
                    throw new AssertionException(
                        "Pending source update/delete observation timed out; details redacted."
                    );
                },
                token
            );

            // No registration, offset reset, provider setup mutation, or topic/capture recreation on recovery.
            _phase = "recovery";
            await fixture.AssertRegisteredConnectorReachesRunningStateAsync(_request, token);
            await fixture.AssertObservedConnectorConfigAsync(rendered, token);
            await FenceAsync("RECOVERED");
            var recoveredOffset = await fixture.TryReadCommittedSourceOffsetAsync(_request, token);
            recoveredOffset.Should().NotBeNull();
            _offsetAdvanced = CdcConnectorTemplatePinnedImageFixture.CommittedSourceOffsetAdvances(
                provider,
                baselineOffset!.CanonicalOffsetJson,
                recoveredOffset!.CanonicalOffsetJson
            );
            _partitionIdentityRetained = baselineOffset
                .SourcePartitionEvidence.Properties.OrderBy(p => p.Key)
                .SequenceEqual(recoveredOffset.SourcePartitionEvidence.Properties.OrderBy(p => p.Key));
            var bounds = (await fixture.CaptureKafkaBoundariesAsync(_request.PublicTopicName, token))
                .Select(b =>
                    b with
                    {
                        StartOffset = _baseline
                            .CompletedBoundaries.Single(p => p.Partition == b.Partition)
                            .EndOffset,
                    }
                )
                .ToArray();
            _recovered = await fixture.ConsumeThroughAsync(bounds, token);
            _replayedSource = (await fixture.ReadSourceObservationsAsync(token))
                .Skip(sourceBeforeFault + _pendingSource.Count)
                .ToArray();
            await fixture.AssertMessageContractSourceLayoutAsync(token);
            if (provider == CdcProvider.SqlServer)
            {
                await fixture.AssertSqlServerCaptureInventoryAsync(token);
            }
            await RetainEvidenceAsync();
            _phase = "complete";
        }
        catch (AssertionException failure) when (_phase == "interrupted-delivery")
        {
            // This helper owns fixed diagnostic prose and strips callback/Docker exception text.
            throw new AssertionException(
                $"{scenarioPrefix} failed in {_phase}; completed fences={JsonSerializer.Serialize(_fences)}; {failure.Message}"
            );
        }
        catch (OperationCanceledException)
        {
            throw new AssertionException(
                $"{scenarioPrefix} timed out in {_phase}; completed fences={JsonSerializer.Serialize(_fences)}; details redacted."
            );
        }

        async Task FenceAsync(string phase)
        {
            _phase = phase;
            _fences.Add(
                provider == CdcProvider.Postgresql
                    ? JsonSerializer.SerializeToElement(
                        await fixture.FencePostgresqlSourceAsync(_request, phase, token)
                    )
                    : JsonSerializer.SerializeToElement(
                        await fixture.FenceSqlServerSourceAsync(_request, phase, token)
                    )
            );
        }
    }

    [Test]
    [Property("ScenarioSuffix", "INTERRUPTION")]
    public void It_exercises_broker_interruption_and_restarts_the_same_worker_before_catch_up()
    {
        _interruption
            .Should()
            .Be(new MessageContractInterruptionEvidence(true, true, true, true, true, true));
        HasUpdateAndDelete(_pendingSource)
            .Should()
            .BeTrue("both source events reached Connect while the broker was paused");
        HasUpdateAndDelete(_replayedSource)
            .Should()
            .BeTrue("the killed worker must re-read uncommitted source history");
        _offsetAdvanced.Should().BeTrue();
        _partitionIdentityRetained.Should().BeTrue();
        _replayedSource
            .Where(IsDocumentRecord)
            .Should()
            .OnlyContain(
                r => r.GetProperty("operation").GetString() != "r",
                "recovery must stream retained history rather than resnapshot"
            );
    }

    [Test]
    [Property("ScenarioSuffix", "PARTITION")]
    public void It_keeps_all_replayed_upserts_and_tombstones_on_the_original_key_partition()
    {
        var records = _baseline.Records.Concat(_recovered.Records).ToArray();
        records.Select(r => r.Partition).Distinct().Should().Equal(_baseline.Records[0].Partition);
        records.Select(r => r.Offset).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        byte[] key = Encoding.UTF8.GetBytes(_shared.CacheRow.GetProperty("documentUuid").GetString()!);
        foreach (MessageContractKafkaRecord record in records)
        {
            record.Topic.Should().Be(_request.PublicTopicName);
            record.Key.IsNull.Should().BeFalse();
            record.Key.Bytes.Should().Equal(key);
            record.Headers.Should().BeEmpty();
            if (record.Value.IsNull)
            {
                record.Value.Bytes.Should().BeEmpty();
                continue;
            }
            JsonElement envelope = JsonSerializer.Deserialize<JsonElement>(record.Value.Bytes);
            long version = envelope.GetProperty("contentVersion").GetInt64();
            bool baseline = version == _shared.CacheRow.GetProperty("contentVersion").GetInt64();
            MessageContractJson.ShouldEqual(envelope, baseline ? _shared.ExpectedEnvelope : _updatedEnvelope);
        }
    }

    [Test]
    [Property("ScenarioSuffix", "CONVERGENCE")]
    public void It_eventually_converges_to_deleted_state_after_the_provider_recovery_fence()
    {
        _recovered.Records.Should().NotBeEmpty();
        _recovered.Records.Any(r => !r.Value.IsNull).Should().BeTrue("the pending update must replay");
        _recovered.Records.Any(r => r.Value.IsNull).Should().BeTrue("the authoritative delete must replay");
        // Deliberately no uniqueness or monotonic-version assertion: duplicates and temporary
        // resurrection from replay are permitted. Applying the complete fenced sequence must delete.
        bool present = false;
        foreach (MessageContractKafkaRecord record in _baseline.Records.Concat(_recovered.Records))
        {
            present = !record.Value.IsNull;
        }
        present.Should().BeFalse();
        _recovered.Records[^1].Value.IsNull.Should().BeTrue();
    }

    private bool HasUpdateAndDelete(IEnumerable<JsonElement> records) =>
        records.Any(r => Matches(r, "DocumentCache", "u")) && records.Any(r => Matches(r, "Document", "d"));

    private bool Matches(JsonElement record, string kind, string operation) =>
        record.GetProperty("kind").GetString() == kind
        && record.GetProperty("operation").GetString() == operation
        && record.GetProperty("uuid").GetString() == _shared.CacheRow.GetProperty("documentUuid").GetString();

    private static bool IsDocumentRecord(JsonElement record) =>
        record.GetProperty("kind").GetString() is "DocumentCache" or "Document";

    private async Task RetainEvidenceAsync()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "TestResults",
            "MessageContractRetry"
        );
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            $"cdc-message-contract-{scenarioPrefix}-{Guid.NewGuid():N}.json"
        );
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                new
                {
                    ScenarioIds = new[]
                    {
                        $"{scenarioPrefix}-INTERRUPTION",
                        $"{scenarioPrefix}-PARTITION",
                        $"{scenarioPrefix}-CONVERGENCE",
                    },
                    ConnectImage = Environment.GetEnvironmentVariable(MessageContractRunner.ImageVariable)
                        ?? string.Empty,
                    Interruption = _interruption,
                    Fences = _fences,
                    OffsetAdvanced = _offsetAdvanced,
                    PartitionIdentityRetained = _partitionIdentityRetained,
                    PendingSource = _pendingSource.Where(IsDocumentRecord).Select(SafeSource),
                    ReplayedSource = _replayedSource.Where(IsDocumentRecord).Select(SafeSource),
                    Scans = new[]
                    {
                        (Phase: "baseline", Scan: _baseline),
                        (Phase: "recovered", Scan: _recovered),
                    }.Select(s => new
                    {
                        s.Phase,
                        Bounds = s.Scan.CompletedBoundaries.Select(b => new
                        {
                            b.Partition,
                            b.StartOffset,
                            b.EndOffset,
                        }),
                        Records = s.Scan.Records.Select(r => new
                        {
                            r.Partition,
                            r.Offset,
                            KeyBytes = r.Key.Bytes.Length,
                            ValueBytes = r.Value.Bytes.Length,
                            KafkaNull = r.Value.IsNull,
                        }),
                    }),
                },
                new JsonSerializerOptions { WriteIndented = true }
            )
        );
        TestContext.AddTestAttachment(
            path,
            "Delivery interruption, retained source replay and fenced broker sequence; payloads omitted"
        );
        static object SafeSource(JsonElement record) =>
            new
            {
                Kind = record.GetProperty("kind").GetString(),
                Operation = record.GetProperty("operation").GetString(),
                ServerMatches = record.GetProperty("serverMatches").GetBoolean(),
                DatabaseMatches = record.GetProperty("databaseMatches").GetBoolean(),
            };
    }
}
