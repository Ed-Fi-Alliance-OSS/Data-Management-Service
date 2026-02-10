// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for a project endpoint name with dashes.
/// </summary>
[TestFixture]
public class Given_A_Project_Endpoint_Name_With_Dashes
{
    private DbSchemaName _schemaName = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _schemaName = RelationalNameConventions.NormalizeSchemaName("ed-fi");
    }

    /// <summary>
    /// It should remove non alphanumerics and lowercase.
    /// </summary>
    [Test]
    public void It_should_remove_non_alphanumerics_and_lowercase()
    {
        _schemaName.Value.Should().Be("edfi");
    }
}

/// <summary>
/// Test fixture for a collection name ending in ies.
/// </summary>
[TestFixture]
public class Given_A_Collection_Name_Ending_In_Ies
{
    private string _baseName = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _baseName = RelationalNameConventions.ToCollectionBaseName("categories");
    }

    /// <summary>
    /// It should singularize to a pascal base name.
    /// </summary>
    [Test]
    public void It_should_singularize_to_a_pascal_base_name()
    {
        _baseName.Should().Be("Category");
    }
}

/// <summary>
/// Test fixture for a collection name ending in ses.
/// </summary>
[TestFixture]
public class Given_A_Collection_Name_Ending_In_Ses
{
    private string _baseName = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _baseName = RelationalNameConventions.ToCollectionBaseName("addresses");
    }

    /// <summary>
    /// It should singularize to a pascal base name.
    /// </summary>
    [Test]
    public void It_should_singularize_to_a_pascal_base_name()
    {
        _baseName.Should().Be("Address");
    }
}
