// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Performance.Harness.Results;
using EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

[TestFixture]
public class Given_A_Written_Run_Directory
{
    private string _directory = null!;

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dms-perf-writer-tests", Guid.NewGuid().ToString("N"));
        PerfAssembledRun assembled = AssemblerSamples.Assemble();
        PerfRunArtifactWriter.Write(
            _directory,
            assembled.Manifest,
            assembled.Results,
            assembled.FixtureManifest,
            assembled.AuxiliaryFiles
        );
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
    public void It_writes_the_core_artifacts()
    {
        File.Exists(Path.Combine(_directory, "run-manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(_directory, "results.json")).Should().BeTrue();
        File.Exists(Path.Combine(_directory, "results.csv")).Should().BeTrue();
        File.Exists(Path.Combine(_directory, "fixture-manifest.json")).Should().BeTrue();
    }

    [Test]
    public void It_round_trips_through_the_validator()
    {
        PerfRunManifest manifest = PerfArtifactJson.Deserialize<PerfRunManifest>(
            File.ReadAllText(Path.Combine(_directory, "run-manifest.json"))
        );
        PerfResultsDocument results = PerfArtifactJson.Deserialize<PerfResultsDocument>(
            File.ReadAllText(Path.Combine(_directory, "results.json"))
        );
        PerfArtifactValidator.Validate(manifest, results).Should().BeEmpty();
    }

    [Test]
    public void It_writes_without_a_byte_order_mark()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(_directory, "results.json"));
        bytes.Length.Should().BeGreaterThan(3);
        bool hasByteOrderMark = bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        hasByteOrderMark.Should().BeFalse();
    }

    [Test]
    public void It_writes_the_plan_and_sql_subdirectories()
    {
        Directory.GetFiles(Path.Combine(_directory, "plans")).Should().HaveCount(6);
        Directory.GetFiles(Path.Combine(_directory, "sql")).Should().HaveCount(3);
    }
}

[TestFixture]
public class Given_A_Missing_Plan_File
{
    [Test]
    public void It_refuses_to_write_anything()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dms-perf-writer-tests",
            Guid.NewGuid().ToString("N")
        );
        PerfAssembledRun assembled = AssemblerSamples.Assemble();
        List<PerfArtifactFile> withoutPlans =
        [
            .. assembled.AuxiliaryFiles.Where(file => !file.RelativePath.StartsWith("plans/")),
        ];

        FluentActions
            .Invoking(() =>
                PerfRunArtifactWriter.Write(
                    directory,
                    assembled.Manifest,
                    assembled.Results,
                    assembled.FixtureManifest,
                    withoutPlans
                )
            )
            .Should()
            .Throw<PerfArtifactValidationException>()
            .WithMessage("*is not being written*");

        Directory.Exists(directory).Should().BeFalse("nothing may be written for invalid artifacts");
    }
}

[TestFixture]
public class Given_Invalid_Artifacts_To_Write
{
    [Test]
    public void It_validates_before_writing()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dms-perf-writer-tests",
            Guid.NewGuid().ToString("N")
        );
        PerfAssembledRun assembled = AssemblerSamples.Assemble();
        PerfResultsDocument truncated = assembled.Results with
        {
            Results = [.. assembled.Results.Results.Take(3)],
        };

        FluentActions
            .Invoking(() =>
                PerfRunArtifactWriter.Write(
                    directory,
                    assembled.Manifest,
                    truncated,
                    assembled.FixtureManifest,
                    assembled.AuxiliaryFiles
                )
            )
            .Should()
            .Throw<PerfArtifactValidationException>();

        Directory.Exists(directory).Should().BeFalse();
    }
}
