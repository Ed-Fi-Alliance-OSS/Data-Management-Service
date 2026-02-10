// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for override collision detection with duplicate origins.
/// </summary>
[TestFixture]
public class Given_Override_Collision_Detector_With_Duplicate_Origins
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var detector = new OverrideCollisionDetector();
        var tableName = new DbTableName(new DbSchemaName("edfi"), "SameTable");

        detector.RegisterTable(
            tableName,
            "SameTable",
            new IdentifierCollisionOrigin("table edfi.SameTable", "ResourceAlpha", "$")
        );
        detector.RegisterTable(
            tableName,
            "SameTable",
            new IdentifierCollisionOrigin("table edfi.SameTable", "ResourceBeta", "$")
        );

        try
        {
            detector.ThrowIfCollisions();
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should report collisions for distinct origins.
    /// </summary>
    [Test]
    public void It_should_report_collision_for_distinct_origins()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Identifier override collisions detected");
        _exception.Message.Should().Contain("ResourceAlpha");
        _exception.Message.Should().Contain("ResourceBeta");
    }
}

/// <summary>
/// Test fixture for identifier shortening collision detection with duplicate origins.
/// </summary>
[TestFixture]
public class Given_Identifier_Collision_Detector_With_Duplicate_Origins
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var dialectRules = new PassthroughDialectRules();
        var detector = new IdentifierCollisionDetector(
            dialectRules,
            IdentifierCollisionStage.AfterDialectShortening(dialectRules)
        );
        var tableName = new DbTableName(new DbSchemaName("edfi"), "SameTable");

        detector.RegisterTable(
            tableName,
            new IdentifierCollisionOrigin("table edfi.SameTable", "ResourceAlpha", "$")
        );
        detector.RegisterTable(
            tableName,
            new IdentifierCollisionOrigin("table edfi.SameTable", "ResourceBeta", "$")
        );

        try
        {
            detector.ThrowIfCollisions();
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should report collisions for distinct origins.
    /// </summary>
    [Test]
    public void It_should_report_collision_for_distinct_origins()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Identifier shortening collisions detected");
        _exception.Message.Should().Contain("AfterDialectShortening");
        _exception.Message.Should().Contain("ResourceAlpha");
        _exception.Message.Should().Contain("ResourceBeta");
    }

    /// <summary>
    /// Test type dialect rules that perform no shortening.
    /// </summary>
    private sealed class PassthroughDialectRules : ISqlDialectRules
    {
        private static readonly SqlScalarTypeDefaults Defaults = new PgsqlDialectRules().ScalarTypeDefaults;

        /// <summary>
        /// Gets dialect.
        /// </summary>
        public SqlDialect Dialect => SqlDialect.Pgsql;

        /// <summary>
        /// Gets max identifier length.
        /// </summary>
        public int MaxIdentifierLength => 63;

        /// <summary>
        /// Gets scalar type defaults.
        /// </summary>
        public SqlScalarTypeDefaults ScalarTypeDefaults => Defaults;

        /// <summary>
        /// Shorten identifier.
        /// </summary>
        public string ShortenIdentifier(string identifier)
        {
            return identifier;
        }
    }
}
