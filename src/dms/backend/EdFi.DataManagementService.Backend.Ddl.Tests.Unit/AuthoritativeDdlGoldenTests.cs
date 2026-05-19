// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
[Category("Authoritative")]
public class Given_AuthoritativeDdl_With_Ds52Core : DdlGoldenFixtureTestBase
{
    protected override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(projectRoot, "..", "Fixtures", "authoritative", "ds-5.2");
}

[TestFixture]
[Category("Authoritative")]
public class Given_AuthoritativeDdl_With_Ds52Core_And_SampleExtension : DdlGoldenFixtureTestBase
{
    // Anchored on the CREATE statement (not the table/index name) so existence-guard fragments
    // (PG `IF NOT EXISTS`, MSSQL `WHERE i.name = N'...'`, etc.) don't inflate the count.
    private static readonly Regex _authTableCreateStatement = new(
        @"\bCREATE\s+TABLE\b(?:\s+IF\s+NOT\s+EXISTS)?\s+[""\[]auth[""\]]\.[""\[]EducationOrganizationIdToEducationOrganizationId[""\]]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex _authIndexCreateStatement = new(
        @"\bCREATE\s+(?:UNIQUE\s+)?INDEX\b(?:\s+IF\s+NOT\s+EXISTS)?\s+[""\[]IX_EducationOrganizationIdToEducationOrganizationId_Target[""\]]\s+ON\s+[""\[]auth[""\]]\.[""\[]EducationOrganizationIdToEducationOrganizationId[""\]]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex _pgsqlAuthSchemaCreateStatement = new(
        @"\bCREATE\s+SCHEMA\b(?:\s+IF\s+NOT\s+EXISTS)?\s+""auth""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex _mssqlAuthSchemaCreateStatement = new(
        @"EXEC\s*\(\s*N?'CREATE\s+SCHEMA\s+\[auth\]'\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    protected override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(projectRoot, "..", "Fixtures", "authoritative", "sample");

    [Test]
    public void It_should_emit_the_auth_edorg_to_edorg_table_exactly_once_for_pgsql()
    {
        var generatedSql = ReadActual("pgsql.sql");
        _authTableCreateStatement
            .Matches(generatedSql)
            .Count.Should()
            .Be(
                1,
                "auth.EducationOrganizationIdToEducationOrganizationId must be emitted exactly once regardless of loaded extensions"
            );
    }

    [Test]
    public void It_should_emit_the_auth_edorg_to_edorg_table_exactly_once_for_mssql()
    {
        var generatedSql = ReadActual("mssql.sql");
        _authTableCreateStatement
            .Matches(generatedSql)
            .Count.Should()
            .Be(
                1,
                "auth.EducationOrganizationIdToEducationOrganizationId must be emitted exactly once regardless of loaded extensions"
            );
    }

    [Test]
    public void It_should_emit_the_auth_edorg_to_edorg_target_index_exactly_once_for_pgsql()
    {
        var generatedSql = ReadActual("pgsql.sql");
        _authIndexCreateStatement
            .Matches(generatedSql)
            .Count.Should()
            .Be(
                1,
                "IX_EducationOrganizationIdToEducationOrganizationId_Target must be emitted exactly once regardless of loaded extensions"
            );
    }

    [Test]
    public void It_should_emit_the_auth_edorg_to_edorg_target_index_exactly_once_for_mssql()
    {
        var generatedSql = ReadActual("mssql.sql");
        _authIndexCreateStatement
            .Matches(generatedSql)
            .Count.Should()
            .Be(
                1,
                "IX_EducationOrganizationIdToEducationOrganizationId_Target must be emitted exactly once regardless of loaded extensions (MSSQL: name appears in the existence guard too, so this counts CREATE INDEX statements only)"
            );
    }

    [Test]
    public void It_should_emit_the_auth_schema_exactly_once_for_pgsql()
    {
        var generatedSql = ReadActual("pgsql.sql");
        _pgsqlAuthSchemaCreateStatement
            .Matches(generatedSql)
            .Count.Should()
            .Be(1, "auth schema must be created exactly once regardless of loaded extensions");
    }

    [Test]
    public void It_should_emit_the_auth_schema_exactly_once_for_mssql()
    {
        var generatedSql = ReadActual("mssql.sql");
        _mssqlAuthSchemaCreateStatement
            .Matches(generatedSql)
            .Count.Should()
            .Be(
                1,
                "auth schema must be created exactly once regardless of loaded extensions (MSSQL uses the guarded EXEC('CREATE SCHEMA [auth]') form)"
            );
    }
}
