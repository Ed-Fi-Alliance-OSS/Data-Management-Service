// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model;

[TestFixture]
public class Given_a_PagingQuery
{
    [Test]
    public void It_should_return_an_empty_paging_clause_when_no_values_are_set()
    {
        var query = new PagingQuery();

        query.BuildPagingClause().Should().BeEmpty();
    }

    [Test]
    public void It_should_return_limit_when_only_limit_is_set()
    {
        var query = new PagingQuery { Limit = 25 };

        query.BuildPagingClause().Should().Be("LIMIT @Limit");
    }

    [Test]
    public void It_should_return_offset_when_only_offset_is_set()
    {
        var query = new PagingQuery { Offset = 10 };

        query.BuildPagingClause().Should().Be("OFFSET @Offset");
    }

    [Test]
    public void It_should_return_limit_and_offset_when_both_values_are_set()
    {
        var query = new PagingQuery { Limit = 25, Offset = 10 };

        query.BuildPagingClause().Should().Be("LIMIT @Limit OFFSET @Offset");
    }

    [TestCase(null, false)]
    [TestCase("asc", false)]
    [TestCase("descending", true)]
    [TestCase("desc", true)]
    [TestCase("other", false)]
    public void It_should_identify_descending_sort_order(string? direction, bool expectedIsDescending)
    {
        var query = new PagingQuery { Direction = direction };

        query.IsDescending.Should().Be(expectedIsDescending);
    }
}
