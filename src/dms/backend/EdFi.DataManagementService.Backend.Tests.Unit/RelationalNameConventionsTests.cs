// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_A_Project_Endpoint_Name_With_Dashes
{
    private DbSchemaName _schemaName = default!;

    [SetUp]
    public void Setup()
    {
        _schemaName = RelationalNameConventions.NormalizeSchemaName("ed-fi");
    }

    [Test]
    public void It_should_remove_non_alphanumerics_and_lowercase()
    {
        _schemaName.Value.Should().Be("edfi");
    }
}

[TestFixture]
public class Given_A_Collection_Name_Ending_In_Ies
{
    private string _baseName = default!;

    [SetUp]
    public void Setup()
    {
        _baseName = RelationalNameConventions.ToCollectionBaseName("categories");
    }

    [Test]
    public void It_should_singularize_to_a_pascal_base_name()
    {
        _baseName.Should().Be("Category");
    }
}

[TestFixture]
public class Given_A_Collection_Name_Ending_In_Ses
{
    private string _baseName = default!;

    [SetUp]
    public void Setup()
    {
        _baseName = RelationalNameConventions.ToCollectionBaseName("addresses");
    }

    [Test]
    public void It_should_singularize_to_a_pascal_base_name()
    {
        _baseName.Should().Be("Address");
    }
}

[TestFixture]
public class Given_A_Table_And_Columns_For_A_Foreign_Key
{
    private string _foreignKeyName = default!;

    [SetUp]
    public void Setup()
    {
        _foreignKeyName = RelationalNameConventions.ForeignKeyName(
            "SchoolAddress",
            new[] { new DbColumnName("SchoolId"), new DbColumnName("AddressId") }
        );
    }

    [Test]
    public void It_should_build_the_foreign_key_name()
    {
        _foreignKeyName.Should().Be("FK_SchoolAddress_SchoolId_AddressId");
    }
}

[TestFixture]
public class Given_An_Empty_Foreign_Key_Column_List
{
    private Action _action = default!;

    [SetUp]
    public void Setup()
    {
        _action = () =>
            RelationalNameConventions.ForeignKeyName("SchoolAddress", Array.Empty<DbColumnName>());
    }

    [Test]
    public void It_should_throw()
    {
        _action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Foreign key must have at least one column.");
    }
}
