// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for a root json path.
/// </summary>
[TestFixture]
public class Given_A_Root_JsonPath
{
    private JsonPathExpression _expression = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _expression = JsonPathExpressionCompiler.Compile("$");
    }

    /// <summary>
    /// It should keep the canonical root path.
    /// </summary>
    [Test]
    public void It_should_keep_the_canonical_root_path()
    {
        _expression.Canonical.Should().Be("$");
    }

    /// <summary>
    /// It should have no segments.
    /// </summary>
    [Test]
    public void It_should_have_no_segments()
    {
        _expression.Segments.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture for a json path with one property.
/// </summary>
[TestFixture]
public class Given_A_JsonPath_With_One_Property
{
    private JsonPathExpression _expression = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _expression = JsonPathExpressionCompiler.Compile("$.a");
    }

    /// <summary>
    /// It should keep the canonical path.
    /// </summary>
    [Test]
    public void It_should_keep_the_canonical_path()
    {
        _expression.Canonical.Should().Be("$.a");
    }

    /// <summary>
    /// It should capture the property segment.
    /// </summary>
    [Test]
    public void It_should_capture_the_property_segment()
    {
        _expression.Segments.Should().Equal(new JsonPathSegment.Property("a"));
    }
}

/// <summary>
/// Test fixture for a json path with two properties.
/// </summary>
[TestFixture]
public class Given_A_JsonPath_With_Two_Properties
{
    private JsonPathExpression _expression = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _expression = JsonPathExpressionCompiler.Compile("$.a.b");
    }

    /// <summary>
    /// It should keep the canonical path.
    /// </summary>
    [Test]
    public void It_should_keep_the_canonical_path()
    {
        _expression.Canonical.Should().Be("$.a.b");
    }

    /// <summary>
    /// It should capture the property segments.
    /// </summary>
    [Test]
    public void It_should_capture_the_property_segments()
    {
        _expression
            .Segments.Should()
            .Equal(new JsonPathSegment.Property("a"), new JsonPathSegment.Property("b"));
    }
}

/// <summary>
/// Test fixture for a json path with an array segment.
/// </summary>
[TestFixture]
public class Given_A_JsonPath_With_An_Array_Segment
{
    private JsonPathExpression _expression = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _expression = JsonPathExpressionCompiler.Compile("$.a[*]");
    }

    /// <summary>
    /// It should keep the canonical path.
    /// </summary>
    [Test]
    public void It_should_keep_the_canonical_path()
    {
        _expression.Canonical.Should().Be("$.a[*]");
    }

    /// <summary>
    /// It should capture the array segment.
    /// </summary>
    [Test]
    public void It_should_capture_the_array_segment()
    {
        _expression
            .Segments.Should()
            .Equal(new JsonPathSegment.Property("a"), new JsonPathSegment.AnyArrayElement());
    }
}

/// <summary>
/// Test fixture for a json path with array and property segments.
/// </summary>
[TestFixture]
public class Given_A_JsonPath_With_Array_And_Property_Segments
{
    private JsonPathExpression _expression = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _expression = JsonPathExpressionCompiler.Compile("$.a[*].b");
    }

    /// <summary>
    /// It should keep the canonical path.
    /// </summary>
    [Test]
    public void It_should_keep_the_canonical_path()
    {
        _expression.Canonical.Should().Be("$.a[*].b");
    }

    /// <summary>
    /// It should capture the mixed segments.
    /// </summary>
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

/// <summary>
/// Test fixture for a json path with nested arrays.
/// </summary>
[TestFixture]
public class Given_A_JsonPath_With_Nested_Arrays
{
    private JsonPathExpression _expression = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _expression = JsonPathExpressionCompiler.Compile("$.a[*].b[*].c");
    }

    /// <summary>
    /// It should keep the canonical path.
    /// </summary>
    [Test]
    public void It_should_keep_the_canonical_path()
    {
        _expression.Canonical.Should().Be("$.a[*].b[*].c");
    }

    /// <summary>
    /// It should capture the nested segments.
    /// </summary>
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

/// <summary>
/// Test fixture for a json path with a numeric index.
/// </summary>
[TestFixture]
public class Given_A_JsonPath_With_A_Numeric_Index
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should throw an argument exception.
    /// </summary>
    [Test]
    public void It_should_throw_an_argument_exception()
    {
        _exception.Should().BeOfType<ArgumentException>();
    }
}
