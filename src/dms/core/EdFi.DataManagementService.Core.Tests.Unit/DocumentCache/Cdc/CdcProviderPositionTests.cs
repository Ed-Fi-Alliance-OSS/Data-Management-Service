// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcProviderPosition")]
public class Given_CdcProviderPosition
{
    [Test]
    public void It_parses_postgresql_wal_lsn_and_compares_lsn_proc_as_unsigned_bit_patterns()
    {
        CdcPostgresqlWalPositionResult barrierResult = CdcPostgresqlProviderPosition.ParseWalLsn(
            "FFFFFFFF/FFFFFFFE"
        );

        CdcProviderPositionComparisonResult result =
            CdcPostgresqlProviderPosition.CompareCommittedOffsetToBarrier(
                barrierResult.Position!.Value,
                new(CdcConnectorOffsetMatchResult.Exact, false, false, -1)
            );

        barrierResult.Succeeded.Should().BeTrue();
        barrierResult.Position!.Value.ToString().Should().Be("FFFFFFFF/FFFFFFFE");
        result.Succeeded.Should().BeTrue();
        result.AtOrBeyondBarrier.Should().BeTrue();
        result.CommittedPosition.Should().Be("FFFFFFFF/FFFFFFFF");
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_rejects_postgresql_malformed_barriers_and_offsets_that_are_not_committed_exact_matches()
    {
        CdcPostgresqlWalPositionResult barrierResult = CdcPostgresqlProviderPosition.ParseWalLsn(
            "1/100000000"
        );
        CdcProviderPositionComparisonResult offsetResult =
            CdcPostgresqlProviderPosition.CompareCommittedOffsetToBarrier(
                new(0x100),
                new(CdcConnectorOffsetMatchResult.Multiple, true, true, null)
            );
        CdcProviderPositionComparisonResult behindResult =
            CdcPostgresqlProviderPosition.CompareCommittedOffsetToBarrier(
                new(0x100),
                new(CdcConnectorOffsetMatchResult.Exact, false, false, 0xFF)
            );

        barrierResult.Succeeded.Should().BeFalse();
        barrierResult
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.MalformedPayload);
        offsetResult.Succeeded.Should().BeFalse();
        offsetResult
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.InvalidObservation)
            .And.Contain(CdcDiagnosticCategory.MalformedPayload)
            .And.Contain(CdcDiagnosticCategory.MissingRequiredField);
        behindResult.Succeeded.Should().BeFalse();
        behindResult
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.InvalidOrdering);
    }

    [Test]
    public void It_normalizes_sql_server_ten_byte_lsn_values_and_compares_commit_change_then_event()
    {
        CdcSqlServerLsnResult normalizedResult = CdcSqlServerProviderPositionParser.NormalizeTenByteLsn(
            [0x00, 0x00, 0x00, 0x23, 0x00, 0x00, 0x01, 0x38, 0x00, 0x02],
            "$.startLsn"
        );
        CdcSqlServerLsnResult parsedResult = CdcSqlServerProviderPositionParser.ParseLsn(
            "0x00000023000001380002",
            "$.startLsn"
        );
        CdcSqlServerProviderPosition barrier = CdcSqlServerProviderPosition.HeartbeatAfterImage(
            parsedResult.Lsn!.Value,
            new(0x23, 0x139, 0x0001)
        );

        CdcProviderPositionComparisonResult reachedResult =
            CdcSqlServerProviderPositionParser.CompareCommittedOffsetToBarrier(
                barrier,
                new(
                    CdcConnectorOffsetMatchResult.Exact,
                    false,
                    false,
                    "00000023:00000138:0002",
                    "00000023:00000139:0001",
                    2
                )
            );
        CdcProviderPositionComparisonResult behindResult =
            CdcSqlServerProviderPositionParser.CompareCommittedOffsetToBarrier(
                barrier,
                new(
                    CdcConnectorOffsetMatchResult.Exact,
                    false,
                    false,
                    "00000023:00000138:0002",
                    "00000023:00000139:0001",
                    1
                )
            );
        CdcProviderPositionComparisonResult laterCommitResult =
            CdcSqlServerProviderPositionParser.CompareCommittedOffsetToBarrier(
                barrier,
                new(
                    CdcConnectorOffsetMatchResult.Exact,
                    false,
                    false,
                    "00000024:00000000:0000",
                    "00000000:00000000:0000",
                    0
                )
            );

        normalizedResult.Succeeded.Should().BeTrue();
        normalizedResult.Lsn!.Value.ToString().Should().Be("00000023:00000138:0002");
        parsedResult.Succeeded.Should().BeTrue();
        parsedResult.Lsn.Should().Be(normalizedResult.Lsn);
        reachedResult.Succeeded.Should().BeTrue();
        reachedResult.CommittedPosition.Should().Be("00000023:00000138:0002/00000023:00000139:0001/2");
        behindResult.Succeeded.Should().BeFalse();
        behindResult
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.InvalidOrdering);
        laterCommitResult.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_rejects_negative_sql_server_event_serial_without_unsigned_wraparound()
    {
        CdcSqlServerProviderPosition zeroBarrier = new(new(0, 0, 0), new(0, 0, 0), 0);
        CdcProviderPositionComparisonResult negativeResult =
            CdcSqlServerProviderPositionParser.CompareCommittedOffsetToBarrier(
                zeroBarrier,
                new(
                    CdcConnectorOffsetMatchResult.Exact,
                    false,
                    false,
                    "00000000:00000000:0000",
                    "00000000:00000000:0000",
                    -1
                )
            );
        CdcSqlServerEventSerialNoResult zeroResult = CdcSqlServerProviderPositionParser.ParseEventSerialNo(
            0,
            "$.eventSerialNo"
        );
        CdcSqlServerEventSerialNoResult heartbeatResult =
            CdcSqlServerProviderPositionParser.ParseEventSerialNo(2, "$.eventSerialNo");
        CdcSqlServerEventSerialNoResult largeResult = CdcSqlServerProviderPositionParser.ParseEventSerialNo(
            long.MaxValue,
            "$.eventSerialNo"
        );
        CdcProviderPositionComparisonResult largeComparisonResult =
            CdcSqlServerProviderPositionParser.CompareCommittedOffsetToBarrier(
                new(new(0x23, 0x138, 0x0002), new(0x23, 0x139, 0x0001), (ulong)long.MaxValue - 1),
                new(
                    CdcConnectorOffsetMatchResult.Exact,
                    false,
                    false,
                    "00000023:00000138:0002",
                    "00000023:00000139:0001",
                    long.MaxValue
                )
            );

        negativeResult.Succeeded.Should().BeFalse();
        negativeResult.AtOrBeyondBarrier.Should().BeFalse();
        negativeResult.CommittedPosition.Should().BeNull();
        negativeResult
            .Diagnostics.Should()
            .Contain(diagnostic => diagnostic.Category == CdcDiagnosticCategory.MalformedPayload);
        negativeResult
            .Diagnostics.Select(diagnostic => diagnostic.Message)
            .Should()
            .NotContain(message => message.Contains("-1"));
        zeroResult.Succeeded.Should().BeTrue();
        zeroResult.EventSerialNo.Should().Be(0);
        heartbeatResult.Succeeded.Should().BeTrue();
        heartbeatResult.EventSerialNo.Should().Be(2);
        largeResult.Succeeded.Should().BeTrue();
        largeResult.EventSerialNo.Should().Be((ulong)long.MaxValue);
        largeComparisonResult.Succeeded.Should().BeTrue();
        largeComparisonResult.CommittedPosition.Should().EndWith($"/{long.MaxValue}");
    }

    [Test]
    public void It_rejects_sql_server_malformed_offsets_and_non_exact_source_partitions()
    {
        CdcSqlServerProviderPosition barrier = CdcSqlServerProviderPosition.HeartbeatAfterImage(
            new(0x23, 0x138, 0x0002),
            new(0x23, 0x139, 0x0001)
        );

        CdcProviderPositionComparisonResult result =
            CdcSqlServerProviderPositionParser.CompareCommittedOffsetToBarrier(
                barrier,
                new(
                    CdcConnectorOffsetMatchResult.SourcePartitionMismatch,
                    true,
                    true,
                    "00000023:00000139:zzzz",
                    null,
                    null
                )
            );

        result.Succeeded.Should().BeFalse();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .Contain(CdcDiagnosticCategory.SourceMismatch)
            .And.Contain(CdcDiagnosticCategory.InvalidObservation)
            .And.Contain(CdcDiagnosticCategory.MalformedPayload)
            .And.Contain(CdcDiagnosticCategory.MissingRequiredField);
        result
            .Diagnostics.Select(diagnostic => diagnostic.Message)
            .Should()
            .NotContain(message => message.Contains("zzzz"));
    }
}
