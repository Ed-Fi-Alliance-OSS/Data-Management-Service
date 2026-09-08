// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixtureSource(nameof(Scenarios))]
[Category("CdcMessageContract")]
[Parallelizable(ParallelScope.Fixtures)]
public class Given_MessageContractAdmissionOffset(
    CdcProvider provider,
    string scenario,
    string offsetJson,
    string wal,
    CdcProviderBarrierState expectedState,
    string expectedPosition
)
{
    private CdcAdmission _baseline = null!;
    private CdcAdmission _actual = null!;
    private CdcConnectorOffsetObservation _offset = null!;
    private CdcProviderBarrierObservation _barrier = null!;

    public static IEnumerable<TestFixtureData> Scenarios()
    {
        foreach (CdcProvider provider in new[] { CdcProvider.Postgresql, CdcProvider.SqlServer })
        {
            string valid = provider == CdcProvider.Postgresql ? """{"lsn_proc":42}""" : SqlOffset();
            foreach (
                string scenario in new[]
                {
                    "missing",
                    "multiple",
                    "wrong-server",
                    "wrong-source",
                    "wrong-provider",
                    "old-operation",
                    "future-offset",
                    "extra-partition-field",
                    "missing-server",
                    "nonstring-server",
                }
            )
            {
                yield return Case(provider, scenario, valid);
            }
            yield return Case(provider, "null", "null");
            yield return Case(provider, "empty", "{}");
            yield return Case(provider, "array", "[]");
            yield return Case(provider, "scalar", "42");
            yield return Case(provider, "string", "\"offset\"");
            yield return Case(
                provider,
                "progress-value",
                """{"HeartbeatSequence":999999,"server":"cdc-progress"}"""
            );
            yield return Case(
                provider,
                "topic-ends",
                """{"publicTopicEndOffset":999999,"progressTopicEndOffset":999999}"""
            );
            foreach (string snapshot in new[] { "true", "\"last\"", "\"incremental\"" })
            {
                yield return Case(
                    provider,
                    $"snapshot-{snapshot.Trim('"')}",
                    valid[..^1] + ",\"snapshot\":" + snapshot + "}"
                );
            }
        }
        yield return Case(
            CdcProvider.SqlServer,
            "null-commit",
            SqlOffset()
                .Replace(
                    "\"" + MessageContractAdmissionFixture.CommitLsn + "\"",
                    "null",
                    StringComparison.Ordinal
                )
        );
        yield return Case(
            CdcProvider.SqlServer,
            "null-change",
            SqlOffset()
                .Replace(
                    "\"" + MessageContractAdmissionFixture.ChangeLsn + "\"",
                    "null",
                    StringComparison.Ordinal
                )
        );
        yield return Case(
            CdcProvider.Postgresql,
            "equal",
            """{"lsn_proc":42}""",
            CdcProviderBarrierState.Reached,
            "0/2A"
        );
        yield return Case(
            CdcProvider.Postgresql,
            "beyond",
            """{"lsn_proc":"43"}""",
            CdcProviderBarrierState.Reached,
            "0/2B"
        );
        yield return Case(
            CdcProvider.Postgresql,
            "behind-other-positions-ahead",
            """{"lsn_proc":41,"lsn":999999,"lsn_commit":999999,"confirmed_flush_lsn":"0/FFFF"}""",
            CdcProviderBarrierState.NotReached
        );
        yield return Case(
            CdcProvider.Postgresql,
            "other-positions-only",
            """{"lsn":999999,"lsn_commit":999999,"confirmed_flush_lsn":"0/FFFF"}"""
        );
        yield return Case(CdcProvider.Postgresql, "null-lsn", """{"lsn_proc":null}""");
        yield return Case(CdcProvider.Postgresql, "malformed-lsn", """{"lsn_proc":"not-an-lsn"}""");
        yield return Case(CdcProvider.Postgresql, "fractional-lsn", """{"lsn_proc":42.5}""");
        yield return Case(CdcProvider.Postgresql, "overflow-lsn", """{"lsn_proc":9223372036854775808}""");
        yield return Case(
            CdcProvider.Postgresql,
            "unsigned-equal",
            """{"lsn_proc":-9223372036854775808}""",
            CdcProviderBarrierState.Reached,
            "80000000/0",
            "80000000/0"
        );
        yield return Case(
            CdcProvider.Postgresql,
            "unsigned-max",
            """{"lsn_proc":-1}""",
            CdcProviderBarrierState.Reached,
            "FFFFFFFF/FFFFFFFF",
            "80000000/0"
        );
        yield return Case(
            CdcProvider.Postgresql,
            "unsigned-behind",
            """{"lsn_proc":9223372036854775807}""",
            CdcProviderBarrierState.NotReached,
            wal: "80000000/0"
        );
        yield return Case(CdcProvider.SqlServer, "wrong-database", SqlOffset());
        yield return Case(
            CdcProvider.SqlServer,
            "before-image",
            SqlOffset(serial: "1"),
            CdcProviderBarrierState.NotReached
        );
        yield return Case(
            CdcProvider.SqlServer,
            "after-image",
            SqlOffset(),
            CdcProviderBarrierState.Reached,
            "00000027:00000c78:0003/00000027:00000c78:0002/2"
        );
        yield return Case(
            CdcProvider.SqlServer,
            "serial-beyond",
            SqlOffset(serial: "3"),
            CdcProviderBarrierState.Reached,
            "00000027:00000c78:0003/00000027:00000c78:0002/3"
        );
        yield return Case(
            CdcProvider.SqlServer,
            "commit-dominates",
            SqlOffset(commit: "00000028:00000000:0000", change: "00000001:00000000:0000", serial: "0"),
            CdcProviderBarrierState.Reached,
            "00000028:00000000:0000/00000001:00000000:0000/0"
        );
        yield return Case(
            CdcProvider.SqlServer,
            "change-dominates",
            SqlOffset(change: "00000027:00000c78:0003", serial: "0"),
            CdcProviderBarrierState.Reached,
            "00000027:00000c78:0003/00000027:00000c78:0003/0"
        );
        yield return Case(
            CdcProvider.SqlServer,
            "commit-behind",
            SqlOffset(commit: "00000026:ffffffff:ffff", change: "ffffffff:ffffffff:ffff", serial: "999"),
            CdcProviderBarrierState.NotReached
        );
        yield return Case(
            CdcProvider.SqlServer,
            "change-behind",
            SqlOffset(change: "00000027:00000c78:0001", serial: "999"),
            CdcProviderBarrierState.NotReached
        );
        yield return Case(
            CdcProvider.SqlServer,
            "uppercase-hex",
            SqlOffset(commit: "00000027:00000C78:0003", change: "00000027:00000C78:0002"),
            CdcProviderBarrierState.Reached,
            "00000027:00000c78:0003/00000027:00000c78:0002/2"
        );
        yield return Case(CdcProvider.SqlServer, "malformed-commit", SqlOffset(commit: "not-an-lsn"));
        yield return Case(CdcProvider.SqlServer, "malformed-change", SqlOffset(change: "not-an-lsn"));
        yield return Case(CdcProvider.SqlServer, "null-serial", SqlOffset(serial: "null"));
        yield return Case(CdcProvider.SqlServer, "negative-serial", SqlOffset(serial: "-1"));
        yield return Case(CdcProvider.SqlServer, "fractional-serial", SqlOffset(serial: "2.5"));
    }

    private static TestFixtureData Case(
        CdcProvider provider,
        string scenario,
        string json,
        CdcProviderBarrierState state = CdcProviderBarrierState.Unknown,
        string position = "",
        string wal = "0/2A"
    )
    {
        TestFixtureData data = new(provider, scenario, json, wal, state, position);
        data.Properties.Set("ScenarioId", $"MC-ADMISSION-OFFSET-{provider}-{scenario}");
        return data;
    }

    private static string SqlOffset(
        string commit = MessageContractAdmissionFixture.CommitLsn,
        string change = MessageContractAdmissionFixture.ChangeLsn,
        string serial = "2"
    ) => $$"""{"commit_lsn":"{{commit}}","change_lsn":"{{change}}","event_serial_no":{{serial}}}""";

    [SetUp]
    public void Setup()
    {
        using MessageContractAdmissionFixture fixture = new(provider);
        CdcInitialAdmissionEvaluationInput input = fixture.ValidInput();
        _baseline = CdcInitialAdmissionEvaluator.Evaluate(input);
        _offset = scenario switch
        {
            "missing" => fixture.MapOffset(),
            "extra-partition-field" => fixture.MapOffset(
                fixture.Entry(offsetJson) with
                {
                    Partition = JsonSerializer.SerializeToElement(
                        new
                        {
                            server = fixture.Inventory.ConnectorName,
                            database = MessageContractAdmissionFixture.Catalog,
                            extra = 1,
                        }
                    ),
                }
            ),
            "missing-server" => fixture.MapOffset(
                fixture.Entry(offsetJson) with
                {
                    Partition = JsonSerializer.SerializeToElement(
                        new { database = MessageContractAdmissionFixture.Catalog }
                    ),
                }
            ),
            "nonstring-server" => fixture.MapOffset(
                fixture.Entry(offsetJson) with
                {
                    Partition = JsonSerializer.SerializeToElement(
                        new { server = 42, database = MessageContractAdmissionFixture.Catalog }
                    ),
                }
            ),
            "multiple" => fixture.MapOffset(fixture.Entry(offsetJson), fixture.Entry(offsetJson)),
            "wrong-server" => fixture.MapOffset(fixture.Entry(offsetJson, server: "other-connector")),
            "wrong-database" => fixture.MapOffset(fixture.Entry(offsetJson, database: "other-database")),
            _ => fixture.MapOffset(fixture.Entry(offsetJson)),
        };
        _offset = scenario switch
        {
            "wrong-source" => _offset with
            {
                PhysicalSourceFingerprint = MessageContractAdmissionFixture.OtherFingerprint,
            },
            "wrong-provider" => _offset with
            {
                Provider =
                    provider == CdcProvider.Postgresql ? CdcProvider.SqlServer : CdcProvider.Postgresql,
            },
            "old-operation" => _offset with { OperationId = "previous-operation" },
            "future-offset" => _offset with
            {
                ObservedAt = MessageContractAdmissionFixture.Now.AddSeconds(1),
            },
            _ => _offset,
        };
        _barrier = fixture.Barrier(_offset, wal);
        _actual = CdcInitialAdmissionEvaluator.Evaluate(input with { ProviderBarrier = _barrier });
    }

    [Test]
    public void It_starts_with_an_admitted_baseline() =>
        _baseline.AdmissionState.Should().Be(CdcAdmissionState.Admitted);

    [Test]
    public void It_uses_the_production_provider_comparison() =>
        _barrier.BarrierState.Should().Be(expectedState);

    [Test]
    public void It_reports_the_normalized_committed_position()
    {
        if (expectedState == CdcProviderBarrierState.Reached)
        {
            _barrier.CommittedPosition.Should().Be(expectedPosition);
        }
        else
        {
            _barrier.CommittedPosition.Should().BeNull();
        }
    }

    [Test]
    public void It_admits_only_when_the_committed_source_position_reaches_the_barrier()
    {
        if (expectedState == CdcProviderBarrierState.Reached)
        {
            _actual.AdmissionState.Should().Be(CdcAdmissionState.Admitted);
        }
        else
        {
            _actual.AdmissionState.Should().NotBe(CdcAdmissionState.Admitted);
        }
    }

    [Test]
    public void It_keeps_all_other_admission_steps_satisfied()
    {
        _actual.Steps.Binding.State.Should().Be(CdcComponentState.Satisfied);
        _actual.Steps.GuardedTrackingActivation.State.Should().Be(CdcComponentState.Satisfied);
        _actual.Steps.ProviderSetup.State.Should().Be(CdcComponentState.Satisfied);
        _actual.Steps.ConnectorAndTopicValidation.State.Should().Be(CdcComponentState.Satisfied);
        _actual.Steps.FirstProjectionCaughtUp.State.Should().Be(CdcComponentState.Satisfied);
        _actual.Steps.SourceHistory.State.Should().Be(CdcComponentState.Satisfied);
        _actual.Steps.SecondProjectionCaughtUp.State.Should().Be(CdcComponentState.Satisfied);
        _actual.Steps.Lag.State.Should().Be(CdcComponentState.Satisfied);
    }

    [Test]
    public void It_preserves_the_offset_rejection_evidence()
    {
        if (expectedState == CdcProviderBarrierState.Reached)
        {
            _barrier.Diagnostics.Should().BeEmpty();
        }
        else
        {
            _barrier.Diagnostics.Should().NotBeEmpty();
        }
        if (scenario == "multiple")
        {
            _offset.SourcePartitionMatchResult.Should().Be(CdcConnectorOffsetMatchResult.Multiple);
        }
        if (scenario is "wrong-server" or "wrong-database")
        {
            _offset
                .SourcePartitionMatchResult.Should()
                .Be(CdcConnectorOffsetMatchResult.SourcePartitionMismatch);
        }
    }
}
