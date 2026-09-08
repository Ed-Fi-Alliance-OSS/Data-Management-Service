// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
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
public class Given_AuthoritativeDdl_With_Ds52Core_And_TpdmExtension : DdlGoldenFixtureTestBase
{
    protected override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(projectRoot, "..", "Fixtures", "authoritative", "ds-5.2-tpdm");
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

    // Captures the view name from any `CREATE [OR REPLACE|ALTER] VIEW "auth"."Name"` /
    // `CREATE [OR REPLACE|ALTER] VIEW [auth].[Name]` form. The view-name capture group lets
    // tests assert that every emitted auth view appears exactly once, regardless of which
    // views currently exist or how many are added in the future.
    private static readonly Regex _authViewCreateStatement = new(
        @"\bCREATE\b(?:\s+OR\s+(?:REPLACE|ALTER))?\s+VIEW\s+[""\[]auth[""\]]\.[""\[](?<viewName>[^""\]]+)[""\]]",
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

    [Test]
    public void It_should_emit_each_auth_view_exactly_once_for_pgsql()
    {
        AssertAuthViewsAreEmittedExactlyOnce("pgsql.sql");
    }

    [Test]
    public void It_should_emit_each_auth_view_exactly_once_for_mssql()
    {
        AssertAuthViewsAreEmittedExactlyOnce("mssql.sql");
    }

    private void AssertAuthViewsAreEmittedExactlyOnce(string dialectSqlFileName)
    {
        var generatedSql = ReadActual(dialectSqlFileName);
        var viewNames = _authViewCreateStatement
            .Matches(generatedSql)
            .Select(match => match.Groups["viewName"].Value)
            .ToArray();

        viewNames
            .Should()
            .NotBeEmpty(
                "at least one auth view (e.g. EducationOrganizationIdToStudentDocumentId) is expected in the emitted DDL"
            );
        viewNames
            .Should()
            .OnlyHaveUniqueItems(
                "each auth view must be emitted exactly once regardless of loaded extensions. "
                    + "PG `CREATE OR REPLACE VIEW` and MSSQL `CREATE OR ALTER VIEW` silently overwrite duplicates, "
                    + "so a re-emission from an extension would not error and would only surface here."
            );
    }

    [Test]
    public void It_should_serialize_auth_table_definition_in_relational_model_manifest_for_pgsql()
    {
        AssertManifestSerializesAuthTable("relational-model.pgsql.manifest.json");
    }

    [Test]
    public void It_should_serialize_auth_table_definition_in_relational_model_manifest_for_mssql()
    {
        AssertManifestSerializesAuthTable("relational-model.mssql.manifest.json");
    }

    [Test]
    public void It_should_serialize_each_auth_view_definition_in_relational_model_manifest_for_pgsql()
    {
        AssertManifestSerializesPeopleAuthViews("relational-model.pgsql.manifest.json");
    }

    [Test]
    public void It_should_serialize_each_auth_view_definition_in_relational_model_manifest_for_mssql()
    {
        AssertManifestSerializesPeopleAuthViews("relational-model.mssql.manifest.json");
    }

    private void AssertManifestSerializesAuthTable(string manifestFileName)
    {
        var authObjects = ReadAuthObjects(manifestFileName);

        // Pins that the manifest snapshots the auth EdOrg-to-EdOrg table's columns and PK so a
        // future change to either is detected by this snapshot (not only by the SQL goldens).
        // Per DMS-1096 AC `19-auth-verification-harness.md`, both snapshots must cover auth.
        authObjects.TryGetProperty("table", out var authTable).Should().BeTrue();
        authTable.GetProperty("table").GetProperty("schema").GetString().Should().Be("auth");
        authTable
            .GetProperty("table")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("EducationOrganizationIdToEducationOrganizationId");

        var columns = authTable.GetProperty("columns");
        columns
            .GetArrayLength()
            .Should()
            .Be(2, "auth.EducationOrganizationIdToEducationOrganizationId has exactly two columns");
        columns[0].GetProperty("name").GetString().Should().Be("SourceEducationOrganizationId");
        columns[0].GetProperty("type").GetString().Should().Be("bigint");
        columns[0].GetProperty("is_nullable").GetBoolean().Should().BeFalse();
        columns[1].GetProperty("name").GetString().Should().Be("TargetEducationOrganizationId");
        columns[1].GetProperty("type").GetString().Should().Be("bigint");
        columns[1].GetProperty("is_nullable").GetBoolean().Should().BeFalse();

        var primaryKey = authTable.GetProperty("primary_key");
        primaryKey
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("PK_EducationOrganizationIdToEducationOrganizationId");
        primaryKey
            .GetProperty("columns")
            .EnumerateArray()
            .Select(c => c.GetString())
            .Should()
            .Equal("SourceEducationOrganizationId", "TargetEducationOrganizationId");
    }

    private void AssertManifestSerializesPeopleAuthViews(string manifestFileName)
    {
        var authObjects = ReadAuthObjects(manifestFileName);

        // Pins that the manifest snapshots each of the four hand-emitted people auth views, so a
        // structural change (joins, output columns, source table, DISTINCT flag) flips the manifest
        // golden — not only the SQL goldens. Drift between this section and
        // `RelationalModelDdlEmitter.EmitPeopleAuthViews` is intentional schema review surface.
        authObjects.TryGetProperty("views", out var views).Should().BeTrue();
        var viewNames = views
            .EnumerateArray()
            .Select(v => v.GetProperty("view").GetProperty("name").GetString())
            .ToArray();
        viewNames
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    "EducationOrganizationIdToContactDocumentId",
                    "EducationOrganizationIdToStaffDocumentId",
                    "EducationOrganizationIdToStudentDocumentId",
                    "EducationOrganizationIdToStudentDocumentIdThroughResponsibility",
                },
                "the four people auth views must each appear exactly once in alphabetical order"
            );

        // Staff has a UNION of two arms (assignment + employment); all others have one.
        // The `arms_set_operator` is also asserted so a swap from UNION → UNION ALL (which changes
        // dedup semantics) flips this manifest snapshot, not only the SQL goldens.
        foreach (var view in views.EnumerateArray())
        {
            var name = view.GetProperty("view").GetProperty("name").GetString();
            var armCount = view.GetProperty("arms").GetArrayLength();
            var setOperator = view.GetProperty("arms_set_operator").GetString();
            if (name == "EducationOrganizationIdToStaffDocumentId")
            {
                armCount
                    .Should()
                    .Be(2, "Staff view unions over the assignment and employment association tables");
                setOperator
                    .Should()
                    .Be(
                        "UNION",
                        "Staff view arms are joined with deduplicating UNION; swapping to UNION ALL is a semantic regression"
                    );
            }
            else
            {
                armCount.Should().Be(1, $"view '{name}' is single-arm");
                setOperator.Should().Be("NONE", $"single-arm view '{name}' has no set-operator");
            }
        }
    }

    private JsonElement ReadAuthObjects(string manifestFileName)
    {
        var manifestJson = ReadActual(manifestFileName);
        using var document = JsonDocument.Parse(manifestJson);
        document
            .RootElement.TryGetProperty("auth_objects", out var authObjects)
            .Should()
            .BeTrue(
                $"the relational-model manifest must include an `auth_objects` section when the auth EdOrg hierarchy is present ({manifestFileName})"
            );
        // JsonElement is a value type referencing the parent document; clone so it stays valid
        // after the using block disposes the JsonDocument.
        return authObjects.Clone();
    }

    // ── DMS-1177: tracked_changes_* schema and table rendering ──────────────────────────
    //
    // The DS-5.2 + Sample set exercises all three tracked-change table kinds: regular resources,
    // concrete subclasses of an abstract resource (School / LocalEducationAgency), and the single
    // shared descriptor table. These tests assert the rendered DDL (read fresh via ReadActual) covers
    // each kind in both dialects, independent of the byte-for-byte goldens.

    [Test]
    public void It_should_emit_the_tracked_changes_schema_for_pgsql()
    {
        ReadActual("pgsql.sql").Should().Contain("CREATE SCHEMA IF NOT EXISTS \"tracked_changes_edfi\";");
    }

    [Test]
    public void It_should_emit_the_tracked_changes_schema_for_mssql()
    {
        ReadActual("mssql.sql").Should().Contain("CREATE SCHEMA [tracked_changes_edfi]");
    }

    [Test]
    public void It_should_render_a_regular_resource_tracked_change_table_for_pgsql()
    {
        AssertTrackedChangeTableStructure(
            "pgsql.sql",
            mssql: false,
            "StudentSchoolAssociation",
            expectDiscriminator: false
        );
    }

    [Test]
    public void It_should_render_a_regular_resource_tracked_change_table_for_mssql()
    {
        AssertTrackedChangeTableStructure(
            "mssql.sql",
            mssql: true,
            "StudentSchoolAssociation",
            expectDiscriminator: false
        );
    }

    [Test]
    public void It_should_render_a_concrete_abstract_tracked_change_table_for_pgsql()
    {
        AssertTrackedChangeTableStructure("pgsql.sql", mssql: false, "School", expectDiscriminator: false);
    }

    [Test]
    public void It_should_render_a_concrete_abstract_tracked_change_table_for_mssql()
    {
        AssertTrackedChangeTableStructure("mssql.sql", mssql: true, "School", expectDiscriminator: false);
    }

    [Test]
    public void It_should_render_the_shared_descriptor_tracked_change_table_for_pgsql()
    {
        AssertTrackedChangeTableStructure("pgsql.sql", mssql: false, "Descriptor", expectDiscriminator: true);

        // Old image follows the source nullability (NOT NULL); New image is nullable for tombstones.
        var block = ExtractTrackedChangeTableBlock(ReadActual("pgsql.sql"), mssql: false, "Descriptor");
        block.Should().Contain("\"OldNamespace\" varchar(255) NOT NULL");
        block.Should().Contain("\"NewNamespace\" varchar(255) NULL");
        block.Should().Contain("\"OldCodeValue\" varchar(50) NOT NULL");
        block.Should().Contain("\"NewCodeValue\" varchar(50) NULL");
        block.Should().Contain("\"Discriminator\" varchar(128) NOT NULL");
    }

    [Test]
    public void It_should_render_the_shared_descriptor_tracked_change_table_for_mssql()
    {
        AssertTrackedChangeTableStructure("mssql.sql", mssql: true, "Descriptor", expectDiscriminator: true);

        var block = ExtractTrackedChangeTableBlock(ReadActual("mssql.sql"), mssql: true, "Descriptor");
        block.Should().Contain("[OldNamespace] nvarchar(255) NOT NULL");
        block.Should().Contain("[NewNamespace] nvarchar(255) NULL");
        block.Should().Contain("[OldCodeValue] nvarchar(50) NOT NULL");
        block.Should().Contain("[NewCodeValue] nvarchar(50) NULL");
        block.Should().Contain("[Discriminator] nvarchar(128) NOT NULL");
    }

    /// <summary>
    /// Asserts a tracked-change <c>CREATE TABLE</c> block renders value columns before system columns,
    /// the fixed-by-role system column types/defaults, and a <c>ChangeVersion</c> primary key (clustered
    /// on SQL Server).
    /// </summary>
    private void AssertTrackedChangeTableStructure(
        string fileName,
        bool mssql,
        string tableName,
        bool expectDiscriminator
    )
    {
        var (open, close) = mssql ? ("[", "]") : ("\"", "\"");
        var block = ExtractTrackedChangeTableBlock(ReadActual(fileName), mssql, tableName);

        // Value columns (Old*) precede the system columns (Id / ChangeVersion / CreatedAt).
        var firstValueColumnIndex = block.IndexOf($"{open}Old", StringComparison.Ordinal);
        firstValueColumnIndex
            .Should()
            .BeGreaterThan(0, $"{tableName} tracked-change table must have value columns");
        var idColumnIndex = block.IndexOf($"{open}Id{close} ", StringComparison.Ordinal);
        idColumnIndex
            .Should()
            .BeGreaterThan(firstValueColumnIndex, "value columns must precede the system columns");

        var idType = mssql ? "uniqueidentifier" : "uuid";
        block.Should().Contain($"{open}Id{close} {idType} NOT NULL");
        block.Should().Contain($"{open}ChangeVersion{close} bigint NOT NULL");
        if (mssql)
        {
            // CreatedAt carries a named DF_* default constraint (consistent with the core DDL convention),
            // so the type prefix and the DEFAULT expression are separated by the CONSTRAINT clause.
            block.Should().Contain($"{open}CreatedAt{close} datetime2(7) NOT NULL CONSTRAINT {open}DF_");
            block.Should().Contain("DEFAULT (sysutcdatetime())");
        }
        else
        {
            // PostgreSQL ignores the default-constraint name and renders a plain DEFAULT.
            block
                .Should()
                .Contain($"{open}CreatedAt{close} timestamp with time zone NOT NULL DEFAULT now()");
        }

        if (expectDiscriminator)
        {
            var discriminatorType = mssql ? "nvarchar(128)" : "varchar(128)";
            block.Should().Contain($"{open}Discriminator{close} {discriminatorType} NOT NULL");
        }
        else
        {
            block.Should().NotContain($"{open}Discriminator{close} ");
        }

        var primaryKey = mssql
            ? $"PRIMARY KEY CLUSTERED ({open}ChangeVersion{close})"
            : $"PRIMARY KEY ({open}ChangeVersion{close})";
        block.Should().Contain(primaryKey);
    }

    /// <summary>
    /// Extracts the <c>CREATE TABLE</c> body for the supplied tracked-change table, from the create
    /// header through the closing parenthesis.
    /// </summary>
    private static string ExtractTrackedChangeTableBlock(string sql, bool mssql, string tableName)
    {
        var (open, close) = mssql ? ("[", "]") : ("\"", "\"");
        var qualified = $"{open}tracked_changes_edfi{close}.{open}{tableName}{close}";
        var header = mssql ? $"CREATE TABLE {qualified}" : $"CREATE TABLE IF NOT EXISTS {qualified}";

        var start = sql.IndexOf(header, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"expected to find '{header}'");
        var end = sql.IndexOf(");", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"expected a closing ');' after '{header}'");

        return sql[start..end];
    }
}
