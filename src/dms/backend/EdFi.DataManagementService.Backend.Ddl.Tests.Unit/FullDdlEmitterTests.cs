// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_JoinSegments_With_All_Segments_Having_Trailing_Newlines
{
    private string _result = default!;

    [SetUp]
    public void Setup()
    {
        _result = FullDdlEmitter.JoinSegments("CREATE TABLE a;\n", "CREATE TABLE b;\n", "INSERT 1;\n");
    }

    [Test]
    public void It_should_concatenate_without_extra_newlines()
    {
        _result.Should().Be("CREATE TABLE a;\nCREATE TABLE b;\nINSERT 1;\n");
    }
}

[TestFixture]
public class Given_JoinSegments_With_Missing_Trailing_Newlines
{
    private string _result = default!;

    [SetUp]
    public void Setup()
    {
        _result = FullDdlEmitter.JoinSegments("CREATE TABLE a;", "CREATE TABLE b;", "INSERT 1;");
    }

    [Test]
    public void It_should_insert_newline_boundaries_between_segments()
    {
        _result.Should().Be("CREATE TABLE a;\nCREATE TABLE b;\nINSERT 1;");
    }

    [Test]
    public void It_should_not_have_consecutive_statements_on_same_line()
    {
        _result.Should().NotContain(";C");
    }
}

[TestFixture]
public class Given_JoinSegments_With_Empty_Segments
{
    private string _result = default!;

    [SetUp]
    public void Setup()
    {
        _result = FullDdlEmitter.JoinSegments("CREATE TABLE a;\n", "", "INSERT 1;\n");
    }

    [Test]
    public void It_should_skip_empty_segments()
    {
        _result.Should().Be("CREATE TABLE a;\nINSERT 1;\n");
    }
}

[TestFixture]
public class Given_JoinSegments_With_All_Empty_Segments
{
    private string _result = default!;

    [SetUp]
    public void Setup()
    {
        _result = FullDdlEmitter.JoinSegments("", "", "");
    }

    [Test]
    public void It_should_return_empty_string()
    {
        _result.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_JoinSegments_With_Single_Segment
{
    private string _result = default!;

    [SetUp]
    public void Setup()
    {
        _result = FullDdlEmitter.JoinSegments("SELECT 1;");
    }

    [Test]
    public void It_should_return_segment_unchanged()
    {
        _result.Should().Be("SELECT 1;");
    }
}
