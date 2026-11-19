// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_Independent_Outputs_For_The_Same_Ddl_Fixture
{
    [Test]
    public void It_preserves_existing_files_in_a_nonempty_output_directory()
    {
        string fixture = Path.Combine(FixtureTestHelper.FindProjectRoot(), "Fixtures", "small", "minimal");
        using var output = new GoldenFixtureOutputDirectory();
        string existingSql = Path.Combine(output.DirectoryPath, "pgsql.sql");
        File.WriteAllText(existingSql, "existing output");

        Action run = () => FixtureRunner.Run(fixture, strict: false, outputDirectory: output.DirectoryPath);

        run.Should().Throw<IOException>();
        File.ReadAllText(existingSql).Should().Be("existing output");
    }

    [Test]
    public void It_keeps_each_run_and_comparison_independent()
    {
        string fixture = Path.Combine(FixtureTestHelper.FindProjectRoot(), "Fixtures", "small", "minimal");
        using var first = new GoldenFixtureOutputDirectory();
        using var second = new GoldenFixtureOutputDirectory();
        first.DirectoryPath.Should().NotBe(second.DirectoryPath);
        FixtureRunner.Run(fixture, strict: false, outputDirectory: first.DirectoryPath);
        string firstSql = Path.Combine(first.DirectoryPath, "pgsql.sql");
        File.WriteAllText(firstSql, "different mutant output");

        FixtureRunner.Run(fixture, strict: false, outputDirectory: second.DirectoryPath);

        File.ReadAllText(firstSql).Should().Be("different mutant output");
        FixtureComparer
            .Compare(fixture, updateGoldens: false, actualDirectory: first.DirectoryPath)
            .Passed.Should()
            .BeFalse();
        FixtureComparer
            .Compare(fixture, updateGoldens: false, actualDirectory: second.DirectoryPath)
            .Passed.Should()
            .BeTrue();
    }
}
