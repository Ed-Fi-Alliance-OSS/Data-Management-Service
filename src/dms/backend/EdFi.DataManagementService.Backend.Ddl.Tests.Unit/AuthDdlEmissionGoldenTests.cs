// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

// ===================================================================
// Auth DDL Golden File Tests - EdOrg Hierarchy
// ===================================================================

[TestFixture]
public class Given_AuthDdlEmitter_With_EdOrgHierarchy_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var hierarchy = AuthEdOrgHierarchyFixture.Build();
        _paths = EmitAuthDdl("auth-edorg-hierarchy", SqlDialect.Pgsql, hierarchy);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_AuthDdlEmitter_With_EdOrgHierarchy_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var hierarchy = AuthEdOrgHierarchyFixture.Build();
        _paths = EmitAuthDdl("auth-edorg-hierarchy", SqlDialect.Mssql, hierarchy);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

// ===================================================================
// Auth DDL Golden File Tests - Empty Hierarchy (edge case)
// ===================================================================

[TestFixture]
public class Given_AuthDdlEmitter_With_EmptyHierarchy_For_Pgsql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var hierarchy = new AuthEdOrgHierarchy([]);
        _paths = EmitAuthDdl("auth-edorg-empty", SqlDialect.Pgsql, hierarchy);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

[TestFixture]
public class Given_AuthDdlEmitter_With_EmptyHierarchy_For_Mssql : DdlEmissionGoldenTestBase
{
    private GoldenTestPaths _paths = default!;

    [SetUp]
    public void Setup()
    {
        var hierarchy = new AuthEdOrgHierarchy([]);
        _paths = EmitAuthDdl("auth-edorg-empty", SqlDialect.Mssql, hierarchy);
    }

    [Test]
    public void It_should_emit_ddl_matching_golden_file()
    {
        AssertGoldenMatch(_paths);
    }
}

// ===================================================================
// Auth DDL Determinism Tests
// ===================================================================

[TestFixture]
public class Given_AuthDdlEmitter_Determinism_For_Pgsql
{
    [Test]
    public void It_should_produce_identical_output_on_repeated_calls()
    {
        var hierarchy = AuthEdOrgHierarchyFixture.Build();
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);

        var first = new AuthDdlEmitter(dialect, hierarchy).Emit();
        var second = new AuthDdlEmitter(dialect, hierarchy).Emit();

        second.Should().Be(first, "AuthDdlEmitter must produce deterministic output");
    }
}

[TestFixture]
public class Given_AuthDdlEmitter_Determinism_For_Mssql
{
    [Test]
    public void It_should_produce_identical_output_on_repeated_calls()
    {
        var hierarchy = AuthEdOrgHierarchyFixture.Build();
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);

        var first = new AuthDdlEmitter(dialect, hierarchy).Emit();
        var second = new AuthDdlEmitter(dialect, hierarchy).Emit();

        second.Should().Be(first, "AuthDdlEmitter must produce deterministic output");
    }
}

// ===================================================================
// Fixture Builder
// ===================================================================

/// <summary>
/// Fixture for auth EdOrg hierarchy scenario:
/// StateEducationAgency (leaf, 0 parents), School (single-parent, 1 FK),
/// LocalEducationAgency (multi-parent, 3 FKs).
/// </summary>
internal static class AuthEdOrgHierarchyFixture
{
    internal static AuthEdOrgHierarchy Build()
    {
        var schema = new DbSchemaName("edfi");

        // Concrete EdOrg subtypes use entity-specific identity column names.
        // The abstract identity table uses EducationOrganizationId; the union view
        // aliases entity-specific columns (e.g., s.SchoolId AS EducationOrganizationId).
        var leaIdentity = new DbColumnName("LocalEducationAgencyId");
        var schoolIdentity = new DbColumnName("SchoolId");
        var seaIdentity = new DbColumnName("StateEducationAgencyId");
        var escIdentity = new DbColumnName("EducationServiceCenterId");

        var stateEducationAgency = new AuthEdOrgEntity(
            EntityName: "StateEducationAgency",
            Table: new DbTableName(schema, "StateEducationAgency"),
            IdentityColumn: seaIdentity,
            ParentEdOrgFks: []
        );

        var school = new AuthEdOrgEntity(
            EntityName: "School",
            Table: new DbTableName(schema, "School"),
            IdentityColumn: schoolIdentity,
            ParentEdOrgFks:
            [
                new AuthParentEdOrgFk(
                    FkColumn: new DbColumnName("LocalEducationAgency_DocumentId"),
                    ParentTable: new DbTableName(schema, "LocalEducationAgency"),
                    ParentIdentityColumn: leaIdentity
                ),
            ]
        );

        var localEducationAgency = new AuthEdOrgEntity(
            EntityName: "LocalEducationAgency",
            Table: new DbTableName(schema, "LocalEducationAgency"),
            IdentityColumn: leaIdentity,
            ParentEdOrgFks:
            [
                new AuthParentEdOrgFk(
                    FkColumn: new DbColumnName("EducationServiceCenter_DocumentId"),
                    ParentTable: new DbTableName(schema, "EducationServiceCenter"),
                    ParentIdentityColumn: escIdentity
                ),
                new AuthParentEdOrgFk(
                    FkColumn: new DbColumnName("ParentLocalEducationAgency_DocumentId"),
                    ParentTable: new DbTableName(schema, "LocalEducationAgency"),
                    ParentIdentityColumn: leaIdentity
                ),
                new AuthParentEdOrgFk(
                    FkColumn: new DbColumnName("StateEducationAgency_DocumentId"),
                    ParentTable: new DbTableName(schema, "StateEducationAgency"),
                    ParentIdentityColumn: seaIdentity
                ),
            ]
        );

        // EntitiesInNameOrder: alphabetically sorted
        return new AuthEdOrgHierarchy([localEducationAgency, school, stateEducationAgency]);
    }
}
