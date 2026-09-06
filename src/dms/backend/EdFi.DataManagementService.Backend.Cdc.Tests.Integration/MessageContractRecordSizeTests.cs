// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc.Control;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;
using CdcProvider = EdFi.DataManagementService.Backend.Ddl.CdcProvider;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql, "MC-RECORD-SIZE-PG", Category = "PostgresqlIntegration")]
[TestFixture(CdcProvider.SqlServer, "MC-RECORD-SIZE-SQL", Category = "MssqlIntegration")]
[Category("DatabaseIntegration")]
[Category("CdcMessageContract")]
[Category("CdcMessageContractKafka")]
public sealed class Given_MessageContractRecordSize(CdcProvider provider, string scenarioPrefix)
{
    private const int Budget = 16_384;
    private const int RecoveryBudget = 32_768;
    private readonly List<object> _evidence = [];
    private SizedRow _below = null!;
    private SizedRow _above = null!;
    private MessageContractKafkaScan _published = null!;
    private MessageContractKafkaScan _failedScan = null!;
    private MessageContractKafkaScan _recoveredScan = null!;
    private CdcAdmission _healthy = null!;
    private CdcAdmission _failed = null!;
    private CdcAdmission _recovered = null!;
    private string _failureCategory = "";
    private string _phase = "startup";

    [OneTimeSetUp]
    public async Task Setup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(9));
        CancellationToken token = timeout.Token;
        await using var fixture = await CdcConnectorTemplatePinnedImageFixture.StartAsync(provider, token);
        try
        {
            var request = CdcConnectorTemplatePinnedImageFixture.WithRecordBudget(
                await fixture.CreateRequestAsync(token),
                Budget
            );
            _phase = "measure-pinned-producer-boundary";
            await BuildRowsAsync(request, token);
            _evidence.Add(await fixture.AlignRecordSizeLimitsAsync(request, token));
            await fixture.InstallSourceObserverAsync(token);
            var rendered = fixture.Render(request);
            AssertProducerConfig(rendered, Budget);
            await fixture.RegisterRenderedConnectorConfigDirectlyAsync(
                rendered,
                token,
                observeSourceRecords: true
            );
            await fixture.AssertRegisteredConnectorReachesRunningStateAsync(request, token);
            await fixture.AssertObservedConnectorConfigAsync(rendered, token);
            string catalog = request.ProviderConnectionProperties.Properties[
                provider == CdcProvider.Postgresql ? "database.dbname" : "database.names"
            ];
            using MessageContractAdmissionFixture admission = new(
                request.Binding.Provider,
                request.Binding,
                request.ArtifactInventory,
                catalog
            );
            DateTimeOffset firstAt = admission.ObserveFocusedProjection().ObservedAt;
            CdcProviderBarrierCaptureResult capture;
            if (provider == CdcProvider.Postgresql)
            {
                string wal = await fixture.CapturePostgresqlWalAsync(token);
                capture = CdcProviderBarrierCaptureResult.PostgresqlSuccess(wal, DateTimeOffset.UtcNow);
            }
            else
            {
                var barrier = await fixture.CaptureSqlServerHeartbeatBarrierAsync(token);
                capture = CdcProviderBarrierCaptureResult.SqlServerSuccess(
                    barrier.CommitLsn,
                    barrier.ChangeLsn,
                    DateTimeOffset.UtcNow
                );
            }

            _phase = "below-budget";
            await fixture.WriteMaterializedRowAsync(_below.CacheRow, false, token);
            await FenceAsync("SIZE-BELOW");
            _published = await ScanAsync();
            AssertCompleteRecords(_published, _below);
            _healthy = await ObserveAdmissionAsync();
            _healthy.AdmissionState.Should().Be(CdcAdmissionState.Admitted);

            _phase = "above-budget";
            await fixture.WriteMaterializedRowAsync(_above.CacheRow, false, token);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            bool failed = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var status = await fixture.ReadConnectorStatusAsync(request, token);
                if (status.TaskStates.Contains("FAILED"))
                {
                    failed = true;
                    break;
                }
                await Task.Delay(250, token);
            }
            failed.Should().BeTrue("the oversized live record must fail the real producer task");
            _failureCategory = await fixture.ReadRecordSizeFailureCategoryAsync(request, token);
            _failureCategory.Should().Be("RecordTooLargeException");
            var source = await fixture.ReadSourceObservationsAsync(token);
            source
                .Any(r =>
                    r.GetProperty("kind").GetString() == "DocumentCache"
                    && r.GetProperty("operation").GetString() == "c"
                    && r.GetProperty("uuid").GetString() == _above.Uuid
                )
                .Should()
                .BeTrue();
            _failedScan = await ScanAsync();
            _failedScan
                .CompletedBoundaries.Select(b => b.EndOffset)
                .Should()
                .Equal(
                    _published.CompletedBoundaries.Select(b => b.EndOffset),
                    "observed terminal producer failure and frozen bounds prove no partial or full oversized publication"
                );
            _failed = await ObserveAdmissionAsync();
            _failed.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
            _failed
                .Steps.ProviderBarrier.State.Should()
                .Be(
                    CdcComponentState.Satisfied,
                    "previously acknowledged progress cannot override a failed task"
                );

            _phase = "raise-isolated-budget-and-resume";
            var recoveredRequest = CdcConnectorTemplatePinnedImageFixture.WithRecordBudget(
                request,
                RecoveryBudget
            );
            _evidence.Add(await fixture.AlignRecordSizeLimitsAsync(recoveredRequest, token));
            var recoveredConfig = fixture.Render(recoveredRequest);
            AssertProducerConfig(recoveredConfig, RecoveryBudget);
            var originalOffset = await fixture.TryReadCommittedSourceOffsetAsync(request, token);
            originalOffset.Should().NotBeNull();
            await fixture.UpdateRetainedConnectorSizeConfigAsync(recoveredConfig, token);
            await fixture.RestartRegisteredConnectorAsync(recoveredRequest, token);
            // Connect's status store can still report the previous FAILED task while reconfiguration starts.
            deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            bool running = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var status = await fixture.ReadConnectorStatusAsync(recoveredRequest, token);
                if (status.ConnectorState == "RUNNING" && status.TaskStates.SequenceEqual(["RUNNING"]))
                {
                    running = true;
                    break;
                }
                await Task.Delay(250, token);
            }
            running.Should().BeTrue("the retained task must recover after asynchronous reconfiguration");
            await fixture.AssertRegisteredConnectorReachesRunningStateAsync(recoveredRequest, token);
            await fixture.AssertObservedConnectorConfigAsync(recoveredConfig, token);
            await FenceAsync("SIZE-RECOVERED");
            _recoveredScan = await ScanAsync();
            AssertCompleteRecords(_recoveredScan, _below);
            AssertCompleteRecords(_recoveredScan, _above);
            _recoveredScan
                .Records.Should()
                .OnlyContain(r =>
                    Encoding.UTF8.GetString(r.Key.Bytes) == _below.Uuid
                    || Encoding.UTF8.GetString(r.Key.Bytes) == _above.Uuid
                );
            var finalOffset = await fixture.TryReadCommittedSourceOffsetAsync(request, token);
            finalOffset.Should().NotBeNull();
            CdcConnectorTemplatePinnedImageFixture
                .CommittedSourceOffsetRetainsOrAdvances(
                    provider,
                    originalOffset!.CanonicalOffsetJson,
                    finalOffset!.CanonicalOffsetJson
                )
                .Should()
                .BeTrue();
            _recovered = await ObserveAdmissionAsync();
            _recovered.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
            _phase = "complete";

            async Task FenceAsync(string phase)
            {
                if (provider == CdcProvider.Postgresql)
                {
                    _evidence.Add(await fixture.FencePostgresqlSourceAsync(request, phase, token));
                }
                else
                {
                    _evidence.Add(await fixture.FenceSqlServerSourceAsync(request, phase, token));
                }
            }

            async Task<MessageContractKafkaScan> ScanAsync()
            {
                var bounds = await fixture.CaptureKafkaBoundariesAsync(request.PublicTopicName, token);
                var scan = await fixture.ConsumeThroughAsync(bounds, token);
                _evidence.Add(
                    new
                    {
                        Phase = _phase,
                        Bounds = bounds
                            .Select(b => new
                            {
                                b.Partition,
                                b.StartOffset,
                                b.EndOffset,
                            })
                            .ToArray(),
                        Records = scan
                            .Records.Select(r => new
                            {
                                r.Partition,
                                r.Offset,
                                KeyBytes = r.Key.Bytes.Length,
                                ValueBytes = r.Value.Bytes.Length,
                                KafkaNull = r.Value.IsNull,
                            })
                            .ToArray(),
                    }
                );
                return scan;
            }

            async Task<CdcAdmission> ObserveAdmissionAsync()
            {
                var snapshot = await fixture.TryReadCommittedSourceOffsetAsync(request, token);
                snapshot.Should().NotBeNull();
                var status = await fixture.ReadConnectorStatusAsync(request, token);
                var input = admission.ObserveLiveProgress(
                    new CdcConnectorOffsetEntry(
                        JsonSerializer.SerializeToElement(snapshot!.SourcePartitionEvidence.Properties),
                        JsonSerializer.Deserialize<JsonElement>(snapshot.CanonicalOffsetJson)
                    ),
                    status.ConnectorState,
                    status.TaskStates,
                    capture,
                    firstAt
                );
                input.Lag!.LagState.Should().Be(CdcConnectorLagState.WithinThreshold);
                var result = CdcInitialAdmissionEvaluator.Evaluate(input);
                _evidence.Add(
                    new
                    {
                        Phase = _phase,
                        Status = status,
                        FailureCategory = _failureCategory,
                        Admission = result.AdmissionState.ToString(),
                        Diagnostics = result.Diagnostics.Select(d => d.Code).ToArray(),
                        Barrier = input.ProviderBarrier!.BarrierState.ToString(),
                        Committed = snapshot.ProviderPosition.ToString(),
                    }
                );
                return result;
            }
        }
        catch (Exception failure) when (failure is not IgnoreException)
        {
            _evidence.Add(
                new
                {
                    FailureFrames = new System.Diagnostics.StackTrace(failure, true)
                        .GetFrames()
                        .Take(8)
                        .Select(f => new { Method = f.GetMethod()!.Name, Line = f.GetFileLineNumber() })
                        .ToArray(),
                }
            );
            await RetainEvidenceAsync();
            throw new AssertionException(
                $"{scenarioPrefix} failed in {_phase}; exception type={failure.GetType().Name}; details redacted. See bounded evidence."
            );
        }
        await RetainEvidenceAsync();
    }

    private async Task BuildRowsAsync(CdcConnectorTemplateRequest request, CancellationToken token)
    {
        string providerName = provider == CdcProvider.Postgresql ? "postgresql" : "sqlserver";
        string providerId = provider == CdcProvider.Postgresql ? "PG" : "SQL";
        var shared = MessageContractFixtureCatalog
            .LoadAll(AppContext.BaseDirectory)
            .Single(f =>
                f.ScenarioId == $"MC-FIX-{providerId}-ORDINARY-LINK-BEARING-STUDENT-SCHOOL-ASSOCIATION"
            );
        Dictionary<string, string> config = new()
        {
            ["provider"] = providerName,
            ["target.topic"] = request.PublicTopicName,
            ["progress.topic"] = request.ProgressTopicName,
        };
        var runner = MessageContractRunner.FromEnvironment();
        SizedRow seed = MakeRow(0, 701);
        var measured = await runner.RunAsync([Scenario(seed, "SEED")], token);
        var seedRecord = Record(measured.Observations.Single());
        int baseSize = seedRecord.GetProperty("producerSizeUpperBound").GetInt32();
        baseSize.Should().BeLessThan(Budget - 2);
        _below = MakeRow(Budget - 1 - baseSize, 701);
        _above = MakeRow(Budget + 1 - baseSize, 702);
        // Kafka's variable-length framing can grow when padding crosses a size tier.
        // Calibrate against the pinned API, then independently exercise both records through the live producer.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            measured = await runner.RunAsync([Scenario(_below, "BELOW"), Scenario(_above, "ABOVE")], token);
            int belowSize = Record(measured.Observations[0]).GetProperty("producerSizeUpperBound").GetInt32();
            int aboveSize = Record(measured.Observations[1]).GetProperty("producerSizeUpperBound").GetInt32();
            _evidence.Add(
                new
                {
                    CalibrationAttempt = attempt,
                    BelowSize = belowSize,
                    AboveSize = aboveSize,
                }
            );
            if (belowSize == Budget - 1 && aboveSize == Budget + 1)
            {
                break;
            }
            if (attempt < 3)
            {
                _below = MakeRow(_below.Padding + Budget - 1 - belowSize, 701);
                _above = MakeRow(_above.Padding + Budget + 1 - aboveSize, 702);
            }
        }
        _below = ObserveSize(_below, measured.Observations[0], Budget - 1);
        _above = ObserveSize(_above, measured.Observations[1], Budget + 1);

        SizedRow MakeRow(int padding, int suffix)
        {
            string uuid = $"aaaaaaaa-bbbb-cccc-dddd-{suffix:D12}";
            JsonNode cache = JsonNode.Parse(shared.CacheRow.GetRawText())!;
            JsonNode expected = JsonNode.Parse(shared.ExpectedEnvelope.GetRawText())!;
            cache["documentId"] = 970000 + suffix;
            cache["documentUuid"] = uuid;
            cache["documentJson"]!["id"] = uuid;
            cache["documentJson"]!["syntheticRecordSizePadding"] = new string('x', padding);
            expected["documentUuid"] = uuid;
            expected["document"]!["id"] = uuid;
            expected["document"]!["syntheticRecordSizePadding"] = new string('x', padding);
            JsonNode source = JsonNode.Parse(shared.SourceRecord.GetRawText())!;
            source["key"]!["DocumentUuid"] = uuid;
            source["value"]!["after"]!["DocumentId"] = 970000 + suffix;
            source["value"]!["after"]!["DocumentUuid"] = uuid;
            source["value"]!["after"]!["DocumentJson"] = cache["documentJson"]!.ToJsonString();
            return new(
                JsonSerializer.SerializeToElement(cache),
                JsonSerializer.SerializeToElement(expected),
                JsonSerializer.SerializeToElement(source),
                padding,
                0,
                0,
                0
            );
        }

        MessageContractRunnerScenario Scenario(SizedRow row, string suffix) =>
            new($"{scenarioPrefix}-{suffix}", row.Source, config, 1, MeasureProducerSize: true);

        SizedRow ObserveSize(SizedRow row, MessageContractRunnerObservation observation, int expectedSize)
        {
            var record = Record(observation);
            int size = record.GetProperty("producerSizeUpperBound").GetInt32();
            size.Should().Be(expectedSize);
            int key = record.GetProperty("keyBytes").GetProperty("length").GetInt32();
            int value = record.GetProperty("valueBytes").GetProperty("length").GetInt32();
            byte[] bytes = Convert.FromBase64String(
                record.GetProperty("valueBytes").GetProperty("base64").GetString()!
            );
            MessageContractJson.ShouldEqual(JsonSerializer.Deserialize<JsonElement>(bytes), row.Expected);
            size.Should().BeGreaterThan(key + value, "the producer boundary includes Kafka framing");
            _evidence.Add(
                new
                {
                    ScenarioId = observation.ScenarioId,
                    row.Padding,
                    KeyBytes = key,
                    ValueBytes = value,
                    ProducerSizeUpperBound = size,
                    FramingBytes = size - key - value,
                    KafkaClientVersion = record.GetProperty("kafkaClientVersion").GetString(),
                    Budget,
                    SizeApi = "AbstractRecords.estimateSizeInBytesUpperBound/CURRENT_MAGIC_VALUE/NONE",
                }
            );
            return row with { ProducerSize = size, KeyBytes = key, ValueBytes = value };
        }

        static JsonElement Record(MessageContractRunnerObservation observation)
        {
            observation.Status.Should().Be("retained");
            return observation.Data.GetProperty("record");
        }
    }

    private void AssertProducerConfig(CdcConnectorTemplateResult rendered, int budget)
    {
        rendered.Config["producer.override.compression.type"].Should().Be("none");
        rendered
            .Config["producer.override.max.request.size"]
            .Should()
            .Be(budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
        long.Parse(rendered.Config["producer.override.buffer.memory"])
            .Should()
            .BeGreaterThanOrEqualTo(budget);
        rendered.Config["errors.tolerance"].Should().Be("none");
        _evidence.Add(
            new
            {
                Phase = _phase,
                ProducerConfig = rendered
                    .Config.Where(p =>
                        p.Key
                            is "producer.override.compression.type"
                                or "producer.override.max.request.size"
                                or "producer.override.buffer.memory"
                                or "errors.tolerance"
                    )
                    .ToDictionary(),
            }
        );
    }

    private static void AssertCompleteRecords(MessageContractKafkaScan scan, SizedRow row)
    {
        var matching = scan.Records.Where(r => Encoding.UTF8.GetString(r.Key.Bytes) == row.Uuid).ToArray();
        matching.Length.Should().BeGreaterThan(0);
        foreach (var record in matching)
        {
            record.Key.IsNull.Should().BeFalse();
            record.Key.Bytes.Length.Should().Be(row.KeyBytes);
            record.Value.IsNull.Should().BeFalse();
            record.Value.Bytes.Length.Should().Be(row.ValueBytes);
            record.Headers.Should().BeEmpty();
            record.Partition.Should().Be(0);
            MessageContractJson.ShouldEqual(
                JsonSerializer.Deserialize<JsonElement>(record.Value.Bytes),
                row.Expected
            );
        }
    }

    [Test]
    [Property("ScenarioSuffix", "BOUNDARY")]
    public void It_publishes_the_complete_synthetic_record_one_byte_below_the_pinned_producer_boundary()
    {
        _below.ProducerSize.Should().Be(Budget - 1);
        _above.ProducerSize.Should().Be(Budget + 1);
        _above.Padding.Should().Be(_below.Padding + 2);
        AssertCompleteRecords(_published, _below);
        _healthy.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
    }

    [Test]
    [Property("ScenarioSuffix", "FAILURE")]
    public void It_fails_the_producer_without_partial_publication_and_blocks_readiness_despite_healthy_progress_and_lag()
    {
        _failureCategory.Should().Be("RecordTooLargeException");
        _failedScan.Records.Any(r => Encoding.UTF8.GetString(r.Key.Bytes) == _above.Uuid).Should().BeFalse();
        _failed.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        _failed.Steps.ConnectorAndTopicValidation.State.Should().NotBe(CdcComponentState.Satisfied);
        _failed.Steps.Lag.State.Should().Be(CdcComponentState.Satisfied);
        _failed.Steps.ProviderBarrier.State.Should().Be(CdcComponentState.Satisfied);
    }

    [Test]
    [Property("ScenarioSuffix", "RECOVERY")]
    public void It_replays_the_rejected_record_and_recovers_readiness_after_aligned_limits_change_in_place()
    {
        AssertCompleteRecords(_recoveredScan, _above);
        _recovered.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
    }

    private async Task RetainEvidenceAsync()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "TestResults",
            "MessageContractRecordSize"
        );
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{scenarioPrefix}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                new
                {
                    ScenarioIds = new[]
                    {
                        $"{scenarioPrefix}-BOUNDARY",
                        $"{scenarioPrefix}-FAILURE",
                        $"{scenarioPrefix}-RECOVERY",
                    },
                    ConnectImage = Environment.GetEnvironmentVariable(MessageContractRunner.ImageVariable)
                        ?? "",
                    Phase = _phase,
                    Observations = _evidence,
                },
                new JsonSerializerOptions { WriteIndented = true }
            )
        );
        TestContext.AddTestAttachment(
            path,
            "Synthetic size boundary, aligned limits, task category, broker bounds and focused readiness; bodies omitted."
        );
    }

    private sealed record SizedRow(
        JsonElement CacheRow,
        JsonElement Expected,
        JsonElement Source,
        int Padding,
        int ProducerSize,
        int KeyBytes,
        int ValueBytes
    )
    {
        public string Uuid => CacheRow.GetProperty("documentUuid").GetString()!;

        public override string ToString() =>
            $"Synthetic size row: padding={Padding}, producer bytes={ProducerSize}";
    }
}
