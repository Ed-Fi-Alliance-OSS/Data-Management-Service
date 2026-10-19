// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category(MssqlCiShards.Shard4)]
public class Given_MssqlProvisioningTimingRecorder
{
    private string _directory = null!;

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"mssql-timing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public void It_preserves_the_csv_contract()
    {
        var path = Path.Combine(_directory, "timings.csv");
        MssqlProvisioningTimingRecorder.RecordToPath(
            path,
            TextWriter.Null,
            "success",
            TimeSpan.FromSeconds(2.5),
            "db",
            300,
            "family,variant",
            "hash",
            "provision",
            "direct",
            "caller",
            "file.cs",
            7,
            batchOrdinal: 2,
            batchCount: 3,
            batchHash: "batch"
        );
        MssqlProvisioningTimingRecorder.RecordToPath(
            path,
            TextWriter.Null,
            "failure",
            TimeSpan.FromSeconds(1),
            "db2",
            300,
            "family",
            "hash2",
            "apply",
            "direct",
            "caller",
            "file.cs",
            8
        );

        var rows = File.ReadAllLines(path);
        rows.Should().HaveCount(3);
        rows[0]
            .Should()
            .Be(
                "TimestampUtc,Outcome,DurationSeconds,DatabaseName,CommandTimeoutSeconds,FixtureSignature,GeneratedDdlHash,Phase,LeaseStrategy,Shard,TestWorkerId,CallerMemberName,CallerFilePath,CallerLineNumber,IsDiagnostic,Detail,BatchOrdinal,BatchCount,BatchHash"
            );
        rows[1]
            .Should()
            .Contain(",success,2.500,db,300,\"family,variant\",hash,provision,direct,")
            .And.Contain(",7,false,,2,3,batch");
        rows[2].Should().Contain(",failure,1.000,db2,300,family,hash2,apply,direct,");
    }

    [Test]
    public void It_skips_a_blank_output_path()
    {
        var warnings = new StringWriter();
        MssqlProvisioningTimingRecorder.RecordToPath(
            "",
            warnings,
            "success",
            TimeSpan.Zero,
            "db",
            300,
            "fixture",
            "hash",
            "phase",
            "direct",
            "caller",
            "file.cs",
            1
        );
        warnings.ToString().Should().BeEmpty();
    }

    [Test]
    public void It_makes_path_write_and_warning_failures_nonfatal()
    {
        var parentFile = Path.Combine(_directory, "regular-file");
        File.WriteAllText(parentFile, "file");
        var warnings = new StringWriter();
        Action act = () =>
            MssqlProvisioningTimingRecorder.RecordToPath(
                Path.Combine(parentFile, "child.csv"),
                warnings,
                "success",
                TimeSpan.Zero,
                "db",
                300,
                "fixture",
                "hash",
                "phase",
                "direct",
                "caller",
                "file.cs",
                1
            );
        act.Should().NotThrow();
        act.Should().NotThrow();
        warnings
            .ToString()
            .Should()
            .Be(
                $"MSSQL fixture timing output unavailable (IOException). Check MSSQL_FIXTURE_TIMINGS_PATH; further warnings for this path are suppressed.{Environment.NewLine}"
            );
        Action throwingWarning = () =>
            MssqlProvisioningTimingRecorder.RecordToPath(
                Path.Combine(parentFile, "other-child.csv"),
                new ThrowingWriter(),
                "success",
                TimeSpan.Zero,
                "db",
                300,
                "fixture",
                "hash",
                "phase",
                "direct",
                "caller",
                "file.cs",
                1
            );
        throwingWarning.Should().NotThrow();
    }

    private sealed class ThrowingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("warning failure");
    }
}
