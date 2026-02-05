// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for root table name override collisions.
/// </summary>
[TestFixture]
public class Given_A_RootTableNameOverride_Collision_Between_Root_Tables
{
    private Exception? _exception;
    private Exception? _reverseOrderException;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _exception = BuildCollisionException(
            "hand-authored-root-override-collision-api-schema.json",
            reverseResourceOrder: false
        );
        _reverseOrderException = BuildCollisionException(
            "hand-authored-root-override-collision-api-schema.json",
            reverseResourceOrder: true
        );
    }

    /// <summary>
    /// It should report root table override collisions.
    /// </summary>
    [Test]
    public void It_should_report_root_table_override_collisions()
    {
        _exception.Should().BeOfType<InvalidOperationException>();

        var message = _exception!.Message;

        message.Should().Contain("Identifier shortening collisions detected");
        message.Should().Contain("AfterDialectShortening(Pgsql:63-bytes)");
        message.Should().Contain("table name collision");
        message.Should().Contain("schema 'edfi'");
        message.Should().Contain("table edfi.Person");
        message.Should().Contain("resource 'Ed-Fi:Student'");
        message.Should().Contain("resource 'Ed-Fi:Staff'");
        message.Should().Contain("path '$'");
    }

    /// <summary>
    /// It should be deterministic across resource ordering.
    /// </summary>
    [Test]
    public void It_should_be_deterministic_across_resource_ordering()
    {
        _reverseOrderException.Should().BeOfType<InvalidOperationException>();
        _reverseOrderException!.Message.Should().Be(_exception!.Message);
    }

    private static Exception? BuildCollisionException(string fixtureName, bool reverseResourceOrder)
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSetFromFixture(
            fixtureName,
            reverseResourceOrder: reverseResourceOrder
        );
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception exception)
        {
            return exception;
        }

        return null;
    }
}

/// <summary>
/// Test fixture for root table override collisions with other tables.
/// </summary>
[TestFixture]
public class Given_A_RootTableNameOverride_Collision_With_A_Child_Table
{
    private Exception? _exception;
    private Exception? _reverseOrderException;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _exception = BuildCollisionException(
            "hand-authored-root-override-child-collision-api-schema.json",
            reverseResourceOrder: false
        );
        _reverseOrderException = BuildCollisionException(
            "hand-authored-root-override-child-collision-api-schema.json",
            reverseResourceOrder: true
        );
    }

    /// <summary>
    /// It should report root table override collisions with child tables.
    /// </summary>
    [Test]
    public void It_should_report_root_table_override_collisions_with_child_tables()
    {
        _exception.Should().BeOfType<InvalidOperationException>();

        var message = _exception!.Message;

        message.Should().Contain("Identifier shortening collisions detected");
        message.Should().Contain("AfterDialectShortening(Pgsql:63-bytes)");
        message.Should().Contain("table name collision");
        message.Should().Contain("schema 'edfi'");
        message.Should().Contain("table edfi.SchoolProgram");
        message.Should().Contain("resource 'Ed-Fi:Program'");
        message.Should().Contain("resource 'Ed-Fi:School'");
        message.Should().Contain("path '$'");
        message.Should().Contain("$.programs[*]");
    }

    /// <summary>
    /// It should be deterministic across resource ordering.
    /// </summary>
    [Test]
    public void It_should_be_deterministic_across_resource_ordering()
    {
        _reverseOrderException.Should().BeOfType<InvalidOperationException>();
        _reverseOrderException!.Message.Should().Be(_exception!.Message);
    }

    private static Exception? BuildCollisionException(string fixtureName, bool reverseResourceOrder)
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSetFromFixture(
            fixtureName,
            reverseResourceOrder: reverseResourceOrder
        );
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception exception)
        {
            return exception;
        }

        return null;
    }
}
