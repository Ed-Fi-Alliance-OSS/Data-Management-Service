// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_A_Root_JsonPath
{
    private JsonPathExpression _expression = default!;

    [SetUp]
    public void Setup()
    {
        _expression = JsonPathExpressionCompiler.Compile("$");
    }

    [Test]
    public void It_should_keep_the_canonical_root_path()
    {
        _expression.Canonical.Should().Be("$");
    }

    [Test]
    public void It_should_have_no_segments()
    {
        _expression.Segments.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_A_JsonPath_With_One_Property
{
    private JsonPathExpression _expression = default!;

    [SetUp]
    public void Setup()
    {
        _expression = JsonPathExpressionCompiler.Compile("$.a");
    }

    [Test]
    public void It_should_keep_the_canonical_path()
    {
        _expression.Canonical.Should().Be("$.a");
    }

    [Test]
    public void It_should_capture_the_property_segment()
    {
        _expression.Segments.Should().Equal(new JsonPathSegment.Property("a"));
    }
}

[TestFixture]
public class Given_A_JsonPath_With_Two_Properties
{
    private JsonPathExpression _expression = default!;

    [SetUp]
    public void Setup()
    {
        _expression = JsonPathExpressionCompiler.Compile("$.a.b");
    }

    [Test]
    public void It_should_keep_the_canonical_path()
    {
        _expression.Canonical.Should().Be("$.a.b");
    }

    [Test]
    public void It_should_capture_the_property_segments()
    {
        _expression
            .Segments.Should()
            .Equal(new JsonPathSegment.Property("a"), new JsonPathSegment.Property("b"));
    }
}

[TestFixture]
public class Given_A_JsonPath_With_An_Array_Segment
{
    private JsonPathExpression _expression = default!;

    [SetUp]
    public void Setup()
    {
        _expression = JsonPathExpressionCompiler.Compile("$.a[*]");
    }

    [Test]
    public void It_should_keep_the_canonical_path()
    {
        _expression.Canonical.Should().Be("$.a[*]");
    }

    [Test]
    public void It_should_capture_the_array_segment()
    {
        _expression
            .Segments.Should()
            .Equal(new JsonPathSegment.Property("a"), new JsonPathSegment.AnyArrayElement());
    }
}

[TestFixture]
public class Given_A_JsonPath_With_Array_And_Property_Segments
{
    private JsonPathExpression _expression = default!;

    [SetUp]
    public void Setup()
    {
        _expression = JsonPathExpressionCompiler.Compile("$.a[*].b");
    }

    [Test]
    public void It_should_keep_the_canonical_path()
    {
        _expression.Canonical.Should().Be("$.a[*].b");
    }

    [Test]
    public void It_should_capture_the_mixed_segments()
    {
        _expression
            .Segments.Should()
            .Equal(
                new JsonPathSegment.Property("a"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("b")
            );
    }
}

[TestFixture]
public class Given_A_JsonPath_With_Nested_Arrays
{
    private JsonPathExpression _expression = default!;

    [SetUp]
    public void Setup()
    {
        _expression = JsonPathExpressionCompiler.Compile("$.a[*].b[*].c");
    }

    [Test]
    public void It_should_keep_the_canonical_path()
    {
        _expression.Canonical.Should().Be("$.a[*].b[*].c");
    }

    [Test]
    public void It_should_capture_the_nested_segments()
    {
        _expression
            .Segments.Should()
            .Equal(
                new JsonPathSegment.Property("a"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("b"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("c")
            );
    }
}

[TestFixture]
public class Given_A_JsonPath_With_A_Numeric_Index
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        try
        {
            _ = JsonPathExpressionCompiler.Compile("$.a[0]");
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    [Test]
    public void It_should_throw_an_argument_exception()
    {
        _exception.Should().BeOfType<ArgumentException>();
    }
}
