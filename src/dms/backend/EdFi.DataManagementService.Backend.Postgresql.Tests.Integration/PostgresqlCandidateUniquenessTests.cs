// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Real-PostgreSQL execution evidence for the shared candidate relation: that the compiled traditional,
/// cursor, and unpaged candidate SQL runs, returns the expected ordered identifiers, and yields exactly
/// one row per <c>DocumentId</c> for every authorization shape the candidate compiler emits.
/// </summary>
/// <remarks>
/// The underlying authorization rows are deliberately seeded to duplicate: several hierarchy edges reach
/// one subject value, several auth-view rows match one person, and several intermediate join rows match
/// one root. A join-based authorization plan would multiply the candidate row under each of those seeds,
/// which would corrupt the row numbering and candidate count that partition boundaries are derived from.
/// The candidate relation must stay unique by construction, and that is asserted here against real
/// results rather than enforced by a runtime guard or concealed by an unconditional <c>DISTINCT</c>.
/// <para>
/// These probes execute the compiled SQL directly rather than through <c>QueryDocuments</c>, which still
/// rejects cursor paging: cursor hydration and execution arrive with the cursor execution story.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Compiled_Candidate_Relation
{
    private const long ClaimEducationOrganizationId = 900L;
    private const string AuthorizedNamespacePrefix = "uri://ed-fi.org/";

    private static readonly DbTableName _rootTable = new(new DbSchemaName("edfi"), "CandidateProbeRoot");
    private static readonly DbTableName _childTable = new(new DbSchemaName("edfi"), "CandidateProbeChild");
    private static readonly DbTableName _customViewTable = new(
        new DbSchemaName("auth"),
        "CandidateProbeView"
    );
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");
    private static readonly DbColumnName _namespaceColumn = new("Namespace");
    private static readonly DbColumnName _schoolIdColumn = new("SchoolId");
    private static readonly DbColumnName _studentDocumentIdColumn = new("Student_DocumentId");

    /// <summary>Every seeded root <c>DocumentId</c>, ascending.</summary>
    private static readonly long[] _allDocumentIds = [10L, 20L, 30L, 40L, 50L];

    private NpgsqlDataSource _dataSource = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dataSource = NpgsqlDataSource.Create(Configuration.DatabaseConnectionString);

        await using var connection = await _dataSource.OpenConnectionAsync();

        foreach (var statement in BuildSchemaStatements())
        {
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await using (var connection = await _dataSource.OpenConnectionAsync())
        {
            foreach (var statement in BuildDropStatements())
            {
                await using var command = new NpgsqlCommand(statement, connection);
                await command.ExecuteNonQueryAsync();
            }
        }

        await _dataSource.DisposeAsync();
    }

    [Test]
    public async Task It_should_execute_traditional_cursor_and_unpaged_candidate_sql_for_every_authorization_shape()
    {
        foreach (var (description, authorization) in BuildAuthorizationMatrix())
        {
            foreach (var mode in BuildEveryCandidateMode())
            {
                var ids = await SelectCandidateIdsAsync(mode, authorization);

                ids.Should()
                    .OnlyHaveUniqueItems(
                        $"the candidate relation must produce one row per DocumentId for {description} in {mode.GetType().Name} mode"
                    );
                ids.Should()
                    .BeEquivalentTo(
                        _allDocumentIds,
                        $"every seeded row must be reachable under {description}"
                    );

                // Only the paged modes order their output. The unpaged candidate relation is
                // deliberately unordered because its consumer supplies the ordering it needs.
                if (mode is not PageCandidateMode.UnpagedCandidates)
                {
                    ids.Should()
                        .BeInAscendingOrder(
                            $"paged candidate selection must be ordered by DocumentId for {description}"
                        );
                }
            }
        }
    }

    [Test]
    public void It_should_never_compile_distinct_into_any_authorized_candidate_relation()
    {
        foreach (var (description, authorization) in BuildAuthorizationMatrix())
        {
            foreach (var mode in BuildEveryCandidateMode())
            {
                Compile(mode, authorization)
                    .PageDocumentIdSql.Should()
                    .NotContain(
                        "DISTINCT",
                        $"uniqueness must hold by construction for {description}, not by a sort"
                    );
            }
        }
    }

    [Test]
    public async Task It_should_select_the_inclusive_cursor_window_only()
    {
        var ids = await SelectCandidateIdsAsync(
            new PageCandidateMode.Cursor(),
            authorization: null,
            cursorMinimum: 20L,
            cursorMaximum: 40L
        );

        ids.Should().Equal(20L, 30L, 40L);
    }

    [Test]
    public async Task It_should_return_no_rows_for_an_inverted_cursor_range()
    {
        var ids = await SelectCandidateIdsAsync(
            new PageCandidateMode.Cursor(),
            authorization: null,
            cursorMinimum: 40L,
            cursorMaximum: 20L
        );

        ids.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_return_no_rows_for_a_zero_page_size()
    {
        // Direct proof that a zero-size cursor page is correct at the SQL level, independent of the
        // hydration batch's zero-limit short-circuit, which keys off the traditional Limit role only.
        var ids = await SelectCandidateIdsAsync(
            new PageCandidateMode.Cursor(),
            authorization: null,
            pageSize: 0L
        );

        ids.Should().BeEmpty();
    }

    [Test]
    public async Task It_should_return_only_the_lowest_in_range_id_for_a_page_size_of_one()
    {
        var ids = await SelectCandidateIdsAsync(
            new PageCandidateMode.Cursor(),
            authorization: null,
            cursorMinimum: 25L,
            pageSize: 1L
        );

        ids.Should().Equal(30L);
    }

    [Test]
    public async Task It_should_select_every_candidate_for_the_maximum_page_size()
    {
        var ids = await SelectCandidateIdsAsync(
            new PageCandidateMode.Cursor(),
            authorization: null,
            pageSize: 500L
        );

        ids.Should().Equal(_allDocumentIds);
    }

    [Test]
    public async Task It_should_select_every_candidate_for_extreme_int64_bounds()
    {
        var ids = await SelectCandidateIdsAsync(
            new PageCandidateMode.Cursor(),
            authorization: null,
            cursorMinimum: long.MinValue,
            cursorMaximum: long.MaxValue
        );

        ids.Should().Equal(_allDocumentIds);
    }

    [Test]
    public async Task It_should_execute_the_unpaged_candidate_relation_inside_a_common_table_expression()
    {
        // The partition consumer wraps this relation in a CTE and supplies its own ordering. A candidate
        // relation that emitted its own ORDER BY would still run here on PostgreSQL but not on SQL Server,
        // so the wrapped form is executed on both providers.
        var plan = Compile(new PageCandidateMode.UnpagedCandidates(), authorization: null);
        var wrappedSql =
            $"WITH candidates AS (\n{plan.PageDocumentIdSql.TrimEnd().TrimEnd(';')}\n)\n"
            + "SELECT \"DocumentId\", ROW_NUMBER() OVER (ORDER BY \"DocumentId\") AS row_number, "
            + "COUNT(*) OVER () AS candidate_count FROM candidates ORDER BY \"DocumentId\";";

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(wrappedSql, connection);

        BindParameters(command, plan, BuildParameterValues(new PageCandidateMode.UnpagedCandidates(), null));

        List<long> ids = [];
        long candidateCount = 0;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
            candidateCount = reader.GetInt64(2);
        }

        ids.Should().Equal(_allDocumentIds);
        candidateCount.Should().Be(_allDocumentIds.Length);
    }

    private async Task<IReadOnlyList<long>> SelectCandidateIdsAsync(
        PageCandidateMode mode,
        PageDocumentIdAuthorizationSpec? authorization,
        long cursorMinimum = long.MinValue,
        long cursorMaximum = long.MaxValue,
        long pageSize = 500L
    )
    {
        var plan = Compile(mode, authorization);

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(plan.PageDocumentIdSql, connection);

        BindParameters(
            command,
            plan,
            BuildParameterValues(mode, authorization, cursorMinimum, cursorMaximum, pageSize)
        );

        List<long> ids = [];

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private static void BindParameters(
        NpgsqlCommand command,
        PageDocumentIdSqlPlan plan,
        IReadOnlyDictionary<string, object?> parameterValues
    )
    {
        // Npgsql infers bigint[] and text[] from the CLR array the parameterization supplies, so array
        // and scalar roles bind through the same call.
        foreach (var parameter in plan.PageParametersInOrder)
        {
            command.Parameters.AddWithValue(
                parameter.ParameterName,
                parameterValues[parameter.ParameterName]!
            );
        }
    }

    private static IReadOnlyDictionary<string, object?> BuildParameterValues(
        PageCandidateMode mode,
        PageDocumentIdAuthorizationSpec? authorization,
        long cursorMinimum = long.MinValue,
        long cursorMaximum = long.MaxValue,
        long pageSize = 500L
    )
    {
        Dictionary<string, object?> parameterValues = new(StringComparer.Ordinal);

        switch (mode)
        {
            case PageCandidateMode.Traditional traditional:
                parameterValues[traditional.OffsetParameterName] = 0L;
                parameterValues[traditional.LimitParameterName] = pageSize;
                break;
            case PageCandidateMode.Cursor cursor:
                parameterValues[cursor.InclusiveMinimumParameterName] = cursorMinimum;
                parameterValues[cursor.InclusiveMaximumParameterName] = cursorMaximum;
                parameterValues[cursor.PageSizeParameterName] = pageSize;
                break;
        }

        if (authorization?.ClaimEducationOrganizationIdParameterization is { } claimParameterization)
        {
            AuthorizationClaimEducationOrganizationIdParameterValues.AddTo(
                parameterValues,
                claimParameterization
            );
        }

        NamespacePrefixParameterValueBinder.Bind(
            parameterValues,
            authorization?.NamespacePrefixParameterization
        );

        return parameterValues;
    }

    private static PageDocumentIdSqlPlan Compile(
        PageCandidateMode mode,
        PageDocumentIdAuthorizationSpec? authorization
    )
    {
        return new PageDocumentIdSqlCompiler(SqlDialect.Pgsql).Compile(
            new PageDocumentIdQuerySpec(
                RootTable: _rootTable,
                Predicates: [],
                UnifiedAliasMappingsByColumn: new Dictionary<DbColumnName, ColumnStorage.UnifiedAlias>(),
                Mode: mode,
                Authorization: authorization
            )
        );
    }

    private static IReadOnlyList<PageCandidateMode> BuildEveryCandidateMode() =>
        [
            new PageCandidateMode.Traditional(),
            new PageCandidateMode.Cursor(),
            new PageCandidateMode.UnpagedCandidates(),
        ];

    /// <summary>
    /// Every authorization shape the candidate compiler emits. <c>OwnershipBased</c> is deliberately
    /// absent: it is known but not enabled for GET-many and fails closed in the authorization planner, so
    /// no candidate SQL is ever compiled for it.
    /// </summary>
    private static IReadOnlyList<(
        string Description,
        PageDocumentIdAuthorizationSpec? Authorization
    )> BuildAuthorizationMatrix() =>
        [
            ("no further restrictions", null),
            (
                "relationship authorization on a root EducationOrganization subject",
                CandidateProbeAuthorizationSpecs.RelationshipEdOrg(
                    SqlDialect.Pgsql,
                    _rootTable,
                    _schoolIdColumn,
                    ClaimEducationOrganizationId
                )
            ),
            (
                "two OR-combined relationship strategies matching the same root",
                CandidateProbeAuthorizationSpecs.TwoRelationshipStrategies(
                    SqlDialect.Pgsql,
                    _rootTable,
                    _schoolIdColumn,
                    ClaimEducationOrganizationId
                )
            ),
            (
                "relationship authorization on a self person subject",
                CandidateProbeAuthorizationSpecs.RelationshipSelfPerson(
                    SqlDialect.Pgsql,
                    _rootTable,
                    _documentIdColumn,
                    ClaimEducationOrganizationId
                )
            ),
            (
                "namespace authorization",
                CandidateProbeAuthorizationSpecs.Namespace(
                    SqlDialect.Pgsql,
                    _rootTable,
                    _namespaceColumn,
                    AuthorizedNamespacePrefix
                )
            ),
            (
                "single-step custom view authorization",
                CandidateProbeAuthorizationSpecs.SingleStepCustomView(
                    _rootTable,
                    _documentIdColumn,
                    _customViewTable
                )
            ),
            (
                "multi-step custom view authorization through a duplicating child table",
                CandidateProbeAuthorizationSpecs.MultiStepCustomView(
                    _rootTable,
                    _documentIdColumn,
                    _childTable,
                    _studentDocumentIdColumn,
                    _customViewTable
                )
            ),
            (
                "namespace and custom view authorization combined",
                CandidateProbeAuthorizationSpecs.NamespaceAndCustomView(
                    SqlDialect.Pgsql,
                    _rootTable,
                    _namespaceColumn,
                    _documentIdColumn,
                    _customViewTable,
                    AuthorizedNamespacePrefix
                )
            ),
        ];

    private static IReadOnlyList<string> BuildSchemaStatements()
    {
        var personAuthObject = CandidateProbeAuthorizationSpecs.SelfPersonAuthObject;
        var edOrgAuthObject = CandidateProbeAuthorizationSpecs.EdOrgAuthObject;

        return
        [
            "CREATE SCHEMA IF NOT EXISTS edfi;",
            "CREATE SCHEMA IF NOT EXISTS auth;",
            $"DROP TABLE IF EXISTS {Quote(_childTable)};",
            $"DROP TABLE IF EXISTS {Quote(_rootTable)};",
            $"DROP TABLE IF EXISTS {Quote(_customViewTable)};",
            $"""
                CREATE TABLE {Quote(_rootTable)} (
                    "DocumentId" bigint PRIMARY KEY,
                    "SchoolId" bigint NOT NULL,
                    "Namespace" varchar(255) NOT NULL
                );
                """,
            $"""
                CREATE TABLE {Quote(_childTable)} (
                    "DocumentId" bigint NOT NULL,
                    "Student_DocumentId" bigint NOT NULL
                );
                """,
            $"CREATE TABLE {Quote(_customViewTable)} (\"DocumentId\" bigint NOT NULL);",
            $"""
                CREATE TABLE IF NOT EXISTS {Quote(edOrgAuthObject.Name)} (
                    "{edOrgAuthObject.ClaimEducationOrganizationIdColumn.Value}" bigint NOT NULL,
                    "{edOrgAuthObject.SubjectValueColumn.Value}" bigint NOT NULL
                );
                """,
            $"""
                CREATE TABLE IF NOT EXISTS {Quote(personAuthObject.Name)} (
                    "{personAuthObject.ClaimEducationOrganizationIdColumn.Value}" bigint NOT NULL,
                    "{personAuthObject.SubjectValueColumn.Value}" bigint NOT NULL
                );
                """,
                // Every root row is reachable, and every authorization source is seeded to duplicate: two
                // hierarchy edges per subject, two auth-view rows per person, two child rows per root, and two
                // custom-view rows per document.
                $"""
                INSERT INTO {Quote(_rootTable)} ("DocumentId", "SchoolId", "Namespace")
                SELECT id, {ClaimEducationOrganizationId}, '{AuthorizedNamespacePrefix}Probe'
                FROM (VALUES (10),(20),(30),(40),(50)) AS seed(id);
                """,
            $"""
                INSERT INTO {Quote(_childTable)} ("DocumentId", "Student_DocumentId")
                SELECT r."DocumentId", r."DocumentId"
                FROM {Quote(_rootTable)} r
                CROSS JOIN (VALUES (1),(2)) AS duplicate(n);
                """,
            $"""
                INSERT INTO {Quote(_customViewTable)} ("DocumentId")
                SELECT r."DocumentId"
                FROM {Quote(_rootTable)} r
                CROSS JOIN (VALUES (1),(2)) AS duplicate(n);
                """,
            $"""
                DELETE FROM {Quote(edOrgAuthObject.Name)}
                WHERE "{edOrgAuthObject.ClaimEducationOrganizationIdColumn.Value}" = {ClaimEducationOrganizationId};
                """,
            $"""
                INSERT INTO {Quote(edOrgAuthObject.Name)} (
                    "{edOrgAuthObject.ClaimEducationOrganizationIdColumn.Value}",
                    "{edOrgAuthObject.SubjectValueColumn.Value}"
                )
                SELECT {ClaimEducationOrganizationId}, {ClaimEducationOrganizationId}
                FROM (VALUES (1),(2)) AS duplicate(n);
                """,
            $"""
                DELETE FROM {Quote(personAuthObject.Name)}
                WHERE "{personAuthObject.ClaimEducationOrganizationIdColumn.Value}" = {ClaimEducationOrganizationId};
                """,
            $"""
                INSERT INTO {Quote(personAuthObject.Name)} (
                    "{personAuthObject.ClaimEducationOrganizationIdColumn.Value}",
                    "{personAuthObject.SubjectValueColumn.Value}"
                )
                SELECT {ClaimEducationOrganizationId}, r."DocumentId"
                FROM {Quote(_rootTable)} r
                CROSS JOIN (VALUES (1),(2)) AS duplicate(n);
                """,
        ];
    }

    private static IReadOnlyList<string> BuildDropStatements() =>
        [
            $"DROP TABLE IF EXISTS {Quote(_childTable)};",
            $"DROP TABLE IF EXISTS {Quote(_rootTable)};",
            $"DROP TABLE IF EXISTS {Quote(_customViewTable)};",
        ];

    private static string Quote(DbTableName table) => $"\"{table.Schema.Value}\".\"{table.Name}\"";
}
