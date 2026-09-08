// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.DataModel;

[TestFixture]
public class Given_A_Query_With_Limit_And_Offset_Building_A_Sql_Server_Paging_Clause
{
    private string _clause = string.Empty;

    [SetUp]
    public void Setup()
    {
        _clause = new PagingQuery { Limit = 25, Offset = 50 }.BuildSqlServerPagingClause();
    }

    [Test]
    public void It_pages_with_offset_and_fetch()
    {
        _clause.Should().Be("OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY");
    }
}

[TestFixture]
public class Given_A_Query_With_Only_A_Limit_Building_A_Sql_Server_Paging_Clause
{
    private string _clause = string.Empty;

    [SetUp]
    public void Setup()
    {
        _clause = new PagingQuery { Limit = 25 }.BuildSqlServerPagingClause();
    }

    [Test]
    public void It_fetches_from_offset_zero()
    {
        _clause.Should().Be("OFFSET 0 ROWS FETCH NEXT @Limit ROWS ONLY");
    }
}

[TestFixture]
public class Given_A_Query_With_Only_An_Offset_Building_A_Sql_Server_Paging_Clause
{
    private string _clause = string.Empty;

    [SetUp]
    public void Setup()
    {
        _clause = new PagingQuery { Offset = 50 }.BuildSqlServerPagingClause();
    }

    [Test]
    public void It_offsets_without_a_fetch_cap()
    {
        _clause.Should().Be("OFFSET @Offset ROWS");
    }
}

[TestFixture]
public class Given_A_Query_Without_Paging_Building_A_Sql_Server_Paging_Clause
{
    private string _clause = string.Empty;

    [SetUp]
    public void Setup()
    {
        _clause = new PagingQuery().BuildSqlServerPagingClause();
    }

    [Test]
    public void It_emits_a_zero_offset_so_order_by_stays_valid()
    {
        _clause.Should().Be("OFFSET 0 ROWS");
    }
}
