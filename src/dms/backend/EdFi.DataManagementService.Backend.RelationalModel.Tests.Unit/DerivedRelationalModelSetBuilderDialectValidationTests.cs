// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for a dialect mismatch when building a derived model set.
/// </summary>
[TestFixture]
public class Given_A_Dialect_Mismatch_When_Building_A_Derived_Model_Set
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false));
        var resourceKeys = new[] { EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "School") };
        var effectiveSchemaSet = EffectiveSchemaFixture.CreateEffectiveSchemaSet(projectSchema, resourceKeys);

        var builder = new DerivedRelationalModelSetBuilder(Array.Empty<IRelationalModelSetPass>());

        try
        {
            builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new MssqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail fast with a dialect mismatch message.
    /// </summary>
    [Test]
    public void It_should_fail_fast_with_a_dialect_mismatch_message()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Pgsql");
        _exception.Message.Should().Contain("Mssql");
    }
}
