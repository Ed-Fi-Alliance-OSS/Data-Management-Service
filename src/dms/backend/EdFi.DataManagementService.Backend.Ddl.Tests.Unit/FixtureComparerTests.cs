// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_FixtureComparer_When_Actual_Directory_Missing
{
    private string _tempDir = default!;
    private FixtureCompareResult _result = default!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _result = FixtureComparer.Compare(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void It_should_fail()
    {
        _result.Passed.Should().BeFalse();
    }

    [Test]
    public void It_should_mention_actual_directory()
    {
        _result.Message.Should().Contain("actual/ directory does not exist");
    }
}

[TestFixture]
public class Given_FixtureComparer_When_Expected_Directory_Missing
{
    private string _tempDir = default!;
    private FixtureCompareResult _result = default!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "actual"));

        File.WriteAllText(Path.Combine(_tempDir, "actual", "test.txt"), "content");

        _result = FixtureComparer.Compare(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void It_should_fail()
    {
        _result.Passed.Should().BeFalse();
    }

    [Test]
    public void It_should_mention_expected_directory()
    {
        _result.Message.Should().Contain("expected/ directory does not exist");
    }
}

[TestFixture]
public class Given_FixtureComparer_When_Expected_Directory_Empty
{
    private string _tempDir = default!;
    private FixtureCompareResult _result = default!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "actual"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "expected"));

        File.WriteAllText(Path.Combine(_tempDir, "actual", "test.txt"), "content");

        _result = FixtureComparer.Compare(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void It_should_fail()
    {
        _result.Passed.Should().BeFalse();
    }

    [Test]
    public void It_should_mention_empty_expected()
    {
        _result.Message.Should().Contain("expected/ directory is empty");
    }
}

[TestFixture]
public class Given_FixtureComparer_When_Files_Match
{
    private string _tempDir = default!;
    private FixtureCompareResult _result = default!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "actual"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "expected"));

        File.WriteAllText(Path.Combine(_tempDir, "expected", "test.txt"), "same content\n");
        File.WriteAllText(Path.Combine(_tempDir, "actual", "test.txt"), "same content\n");

        _result = FixtureComparer.Compare(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void It_should_pass()
    {
        _result.Passed.Should().BeTrue();
    }

    [Test]
    public void It_should_have_empty_message()
    {
        _result.Message.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_FixtureComparer_When_Files_Differ
{
    private string _tempDir = default!;
    private FixtureCompareResult _result = default!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "actual"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "expected"));

        File.WriteAllText(Path.Combine(_tempDir, "expected", "test.txt"), "expected content\n");
        File.WriteAllText(Path.Combine(_tempDir, "actual", "test.txt"), "actual content\n");

        _result = FixtureComparer.Compare(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void It_should_fail()
    {
        _result.Passed.Should().BeFalse();
    }

    [Test]
    public void It_should_include_diff_output()
    {
        _result.Message.Should().Contain("test.txt");
    }
}

[TestFixture]
public class Given_FixtureComparer_When_File_Missing_In_Actual
{
    private string _tempDir = default!;
    private FixtureCompareResult _result = default!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "actual"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "expected"));

        File.WriteAllText(
            Path.Combine(_tempDir, "expected", "expected-only.txt"),
            "this file is only in expected\n"
        );

        _result = FixtureComparer.Compare(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void It_should_fail()
    {
        _result.Passed.Should().BeFalse();
    }

    [Test]
    public void It_should_report_missing_file()
    {
        _result.Message.Should().Contain("Missing in actual/").And.Contain("expected-only.txt");
    }
}

[TestFixture]
public class Given_FixtureComparer_When_Extra_Files_In_Actual
{
    private string _tempDir = default!;
    private FixtureCompareResult _result = default!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "actual"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "expected"));

        File.WriteAllText(Path.Combine(_tempDir, "expected", "shared.txt"), "content\n");
        File.WriteAllText(Path.Combine(_tempDir, "actual", "shared.txt"), "content\n");
        File.WriteAllText(Path.Combine(_tempDir, "actual", "extra.txt"), "extra content\n");

        _result = FixtureComparer.Compare(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void It_should_fail()
    {
        _result.Passed.Should().BeFalse();
    }

    [Test]
    public void It_should_report_extra_files()
    {
        _result.Message.Should().Contain("Extra files in actual/").And.Contain("extra.txt");
    }
}
