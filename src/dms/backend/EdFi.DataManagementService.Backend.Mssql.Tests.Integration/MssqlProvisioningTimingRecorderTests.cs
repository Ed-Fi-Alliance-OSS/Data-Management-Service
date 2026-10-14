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
        MssqlProvisioningTimingRecorder.RecordToPath(
            " \t",
            TextWriter.Null,
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
        warnings.ToString().Should().Contain("MSSQL fixture timing output unavailable (IOException).");
        Action throwingWarning = () =>
            MssqlProvisioningTimingRecorder.RecordToPath(
                Path.Combine(parentFile, "child.csv"),
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

    [Test]
    public void It_continues_disposal_after_timing_output_fails()
    {
        var parentFile = Path.Combine(_directory, "parent-file");
        File.WriteAllText(parentFile, "file");
        var disposed = false;
        var primary = new InvalidOperationException("primary");
        Action act = () =>
        {
            try
            {
                throw primary;
            }
            finally
            {
                MssqlProvisioningTimingRecorder.RecordToPath(
                    Path.Combine(parentFile, "child.csv"),
                    new ThrowingWriter(),
                    "failure",
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
                disposed = true;
            }
        };
        act.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(primary);
        disposed.Should().BeTrue();
    }

    [Test]
    public void It_preserves_primary_and_cleanup_failures_after_timing_output_fails()
    {
        var parentFile = Path.Combine(_directory, "parent-file");
        File.WriteAllText(parentFile, "file");
        var cleanupInvoked = false;
        var primary = new InvalidOperationException("primary");
        var cleanup = new ApplicationException("cleanup");
        Action dispose = () =>
        {
            cleanupInvoked = true;
            throw cleanup;
        };
        Action act = () =>
        {
            try
            {
                throw primary;
            }
            catch (Exception primaryException)
            {
                List<Exception> cleanupExceptions = [];
                try
                {
                    MssqlProvisioningTimingRecorder.RecordToPath(
                        Path.Combine(parentFile, "child.csv"),
                        new ThrowingWriter(),
                        "failure",
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
                    dispose();
                }
                catch (Exception cleanupException)
                {
                    cleanupExceptions.Add(cleanupException);
                }

                MssqlLifecycleExceptionAggregator.Throw(primaryException, cleanupExceptions);
            }
        };

        var aggregate = act.Should().Throw<AggregateException>().Which;
        cleanupInvoked.Should().BeTrue();
        aggregate.InnerExceptions.Should().HaveCount(2);
        aggregate.InnerExceptions[0].Should().BeSameAs(primary);
        aggregate.InnerExceptions[1].Should().BeSameAs(cleanup);
    }

    private sealed class ThrowingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("warning failure");
    }
}
