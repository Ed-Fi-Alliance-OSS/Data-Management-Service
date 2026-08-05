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
public class PageSizeTests
{
    [Test]
    public void It_accepts_a_zero_page_size()
    {
        PageSize pageSize = new(0);

        pageSize.Value.Should().Be(0);
    }

    [Test]
    public void It_accepts_a_positive_page_size()
    {
        PageSize pageSize = new(500);

        pageSize.Value.Should().Be(500);
    }

    [Test]
    public void It_rejects_a_negative_page_size()
    {
        Action act = () => _ = new PageSize(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void It_compares_equal_to_a_page_size_with_the_same_value()
    {
        new PageSize(25).Should().Be(new PageSize(25));
    }

    [Test]
    public void It_compares_unequal_to_a_page_size_with_a_different_value()
    {
        new PageSize(25).Should().NotBe(new PageSize(26));
    }

    [Test]
    public void It_treats_the_default_value_as_a_zero_page_size()
    {
        default(PageSize).Value.Should().Be(0);
    }
}
