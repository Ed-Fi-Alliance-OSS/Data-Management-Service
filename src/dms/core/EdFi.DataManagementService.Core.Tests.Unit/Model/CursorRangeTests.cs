// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Model;

[TestFixture]
[Parallelizable]
public class CursorRangeTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_A_Range_With_Both_Bounds_Supplied : CursorRangeTests
    {
        private CursorRange _range = null!;

        [SetUp]
        public void Setup()
        {
            _range = new CursorRange(InclusiveMinimum: 10, InclusiveMaximum: 2509);
        }

        [Test]
        public void It_carries_the_inclusive_minimum()
        {
            _range.InclusiveMinimum.Should().Be(10);
        }

        [Test]
        public void It_carries_the_inclusive_maximum()
        {
            _range.InclusiveMaximum.Should().Be(2509);
        }

        [Test]
        public void It_compares_equal_to_a_range_with_the_same_bounds()
        {
            _range.Should().Be(new CursorRange(10, 2509));
        }

        [Test]
        public void It_compares_unequal_to_a_range_with_different_bounds()
        {
            _range.Should().NotBe(new CursorRange(10, 2510));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Range_Created_From_A_Minimum_Only : CursorRangeTests
    {
        private CursorRange _range = null!;

        [SetUp]
        public void Setup()
        {
            _range = CursorRange.From(inclusiveMinimum: 501);
        }

        [Test]
        public void It_retains_the_supplied_minimum()
        {
            _range.InclusiveMinimum.Should().Be(501);
        }

        [Test]
        public void It_is_unbounded_above()
        {
            _range.InclusiveMaximum.Should().Be(long.MaxValue);
        }
    }

    [Test]
    public void It_accepts_negative_bounds()
    {
        CursorRange range = new(-20, -5);

        range.InclusiveMinimum.Should().Be(-20);
        range.InclusiveMaximum.Should().Be(-5);
    }

    [Test]
    public void It_accepts_an_inverted_match_nothing_range()
    {
        CursorRange range = new(2510, 2509);

        range.InclusiveMinimum.Should().Be(2510);
        range.InclusiveMaximum.Should().Be(2509);
    }

    [Test]
    public void It_accepts_the_full_signed_range()
    {
        CursorRange range = new(long.MinValue, long.MaxValue);

        range.InclusiveMinimum.Should().Be(long.MinValue);
        range.InclusiveMaximum.Should().Be(long.MaxValue);
    }
}
