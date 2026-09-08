// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_SqlDialectFactory_Create_From_Pgsql_Enum
{
    private ISqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
    }

    [Test]
    public void It_should_return_pgsql_dialect()
    {
        _dialect.Should().BeOfType<PgsqlDialect>();
    }

    [Test]
    public void It_should_have_correct_rules()
    {
        _dialect.Rules.Dialect.Should().Be(SqlDialect.Pgsql);
    }
}

[TestFixture]
public class Given_SqlDialectFactory_Create_From_Mssql_Enum
{
    private ISqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
    }

    [Test]
    public void It_should_return_mssql_dialect()
    {
        _dialect.Should().BeOfType<MssqlDialect>();
    }

    [Test]
    public void It_should_have_correct_rules()
    {
        _dialect.Rules.Dialect.Should().Be(SqlDialect.Mssql);
    }
}

[TestFixture]
public class Given_SqlDialectFactory_Create_From_Pgsql_Rules
{
    private ISqlDialect _dialect = default!;
    private ISqlDialectRules _rules = default!;

    [SetUp]
    public void Setup()
    {
        _rules = new PgsqlDialectRules();
        _dialect = SqlDialectFactory.Create(_rules);
    }

    [Test]
    public void It_should_return_pgsql_dialect()
    {
        _dialect.Should().BeOfType<PgsqlDialect>();
    }

    [Test]
    public void It_should_use_provided_rules()
    {
        _dialect.Rules.Should().BeSameAs(_rules);
    }
}

[TestFixture]
public class Given_SqlDialectFactory_Create_From_Mssql_Rules
{
    private ISqlDialect _dialect = default!;
    private ISqlDialectRules _rules = default!;

    [SetUp]
    public void Setup()
    {
        _rules = new MssqlDialectRules();
        _dialect = SqlDialectFactory.Create(_rules);
    }

    [Test]
    public void It_should_return_mssql_dialect()
    {
        _dialect.Should().BeOfType<MssqlDialect>();
    }

    [Test]
    public void It_should_use_provided_rules()
    {
        _dialect.Rules.Should().BeSameAs(_rules);
    }
}

[TestFixture]
public class Given_SqlDialectFactory_Create_From_Null_Rules
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        try
        {
            _ = SqlDialectFactory.Create((ISqlDialectRules)null!);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_throw_argument_null_exception()
    {
        _exception.Should().BeOfType<ArgumentNullException>();
    }
}
