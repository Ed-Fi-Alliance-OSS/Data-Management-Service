// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for a root table name override.
/// </summary>
[TestFixture]
public class Given_A_RootTableNameOverride
{
    private DerivedRelationalModelSet _derivedModelSet = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSetFromFixture(
            "hand-authored-root-override-api-schema.json"
        );
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        _derivedModelSet = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should rename child tables based on the override.
    /// </summary>
    [Test]
    public void It_should_rename_child_tables_based_on_the_override()
    {
        var model = _derivedModelSet.ConcreteResourcesInNameOrder.Single().RelationalModel;

        var tableNames = model.TablesInDependencyOrder.Select(table => table.Table.Name).ToArray();

        tableNames.Should().Contain(new[] { "Person", "PersonAddress", "PersonAddressPeriod" });
    }

    /// <summary>
    /// It should update root key-part column names based on the override.
    /// </summary>
    [Test]
    public void It_should_update_root_key_part_column_names()
    {
        var model = _derivedModelSet.ConcreteResourcesInNameOrder.Single().RelationalModel;

        var addressTable = model.TablesInDependencyOrder.Single(table => table.Table.Name == "PersonAddress");
        var keyColumnNames = addressTable.Key.Columns.Select(column => column.ColumnName.Value).ToArray();

        keyColumnNames.Should().Contain("Person_DocumentId");
    }
}
