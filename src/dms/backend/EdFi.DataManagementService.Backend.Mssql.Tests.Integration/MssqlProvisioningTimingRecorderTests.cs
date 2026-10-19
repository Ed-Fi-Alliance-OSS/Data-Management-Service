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
    private string _directory = "";
    private string[] _rows = [];
    private string _warnings = "";
    private string _blankPathWarnings = "";
    private string[] _unexpectedBlankPathFiles = [];
    private string[] _createdBlankPathFiles = [];
    private int _warningAttempts;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"mssql-timing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
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
        _rows = File.ReadAllLines(path);

        string[] blankPaths = ["", " \t "];
        var existingBlankPathFiles = blankPaths.Where(File.Exists).ToArray();
        using var blankWarnings = new StringWriter();
        foreach (var blankPath in blankPaths)
        {
            Record(blankPath, blankWarnings);
        }
        _blankPathWarnings = blankWarnings.ToString();
        _unexpectedBlankPathFiles = blankPaths.Where(File.Exists).ToArray();
        _createdBlankPathFiles = _unexpectedBlankPathFiles.Except(existingBlankPathFiles).ToArray();

        var parentFile = Path.Combine(_directory, "regular-file");
        File.WriteAllText(parentFile, "file");
        using var warnings = new StringWriter();
        // Both attempts must remain nonfatal; the second must not warn again.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Record(Path.Combine(parentFile, "child.csv"), warnings);
        }
        _warnings = warnings.ToString();

        using var throwingWriter = new ThrowingWriter();
        Record(Path.Combine(parentFile, "other-child.csv"), throwingWriter);
        _warningAttempts = throwingWriter.Attempts;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        foreach (var path in _createdBlankPathFiles)
        {
            File.Delete(path);
        }
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public void It_preserves_the_csv_contract()
    {
        _rows.Should().HaveCount(3);
        _rows[0]
            .Should()
            .Be(
                "TimestampUtc,Outcome,DurationSeconds,DatabaseName,CommandTimeoutSeconds,FixtureSignature,GeneratedDdlHash,Phase,LeaseStrategy,Shard,TestWorkerId,CallerMemberName,CallerFilePath,CallerLineNumber,IsDiagnostic,Detail,BatchOrdinal,BatchCount,BatchHash"
            );
        _rows[1]
            .Should()
            .Contain(",success,2.500,db,300,\"family,variant\",hash,provision,direct,")
            .And.Contain(",7,false,,2,3,batch");
        _rows[2].Should().Contain(",failure,1.000,db2,300,family,hash2,apply,direct,");
    }

    [Test]
    public void It_skips_empty_and_whitespace_only_output_paths()
    {
        _blankPathWarnings.Should().BeEmpty();
        _unexpectedBlankPathFiles.Should().BeEmpty();
    }

    [Test]
    public void It_warns_only_once_per_unavailable_path()
    {
        _warnings
            .Should()
            .Be(
                $"MSSQL fixture timing output unavailable (IOException). Check MSSQL_FIXTURE_TIMINGS_PATH; further warnings for this path are suppressed.{Environment.NewLine}"
            );
    }

    [Test]
    public void It_makes_warning_failures_nonfatal()
    {
        _warningAttempts.Should().Be(1);
    }

    private static void Record(string path, TextWriter writer) =>
        MssqlProvisioningTimingRecorder.RecordToPath(
            path,
            writer,
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

    private sealed class ThrowingWriter : StringWriter
    {
        public int Attempts { get; private set; }

        public override void WriteLine(string? value)
        {
            Attempts++;
            throw new IOException("warning failure");
        }
    }
}
