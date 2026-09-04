// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_OwnershipAuthorizationSqlCompiler
{
    private const string DocumentIdParameterName = "documentId";
    private const string OwnershipTokenIdsParameterName = "ownershipTokenIds";

    private static OwnershipTokenParameterization Tokens(
        SqlDialect dialect,
        params int[] ownershipTokenIds
    ) =>
        OwnershipTokenParameterizationFactory.Create(
            dialect,
            [.. ownershipTokenIds.Select(static ownershipTokenId => (short)ownershipTokenId)],
            OwnershipTokenIdsParameterName
        );

    private static OwnershipAuthorizationSqlSpec Spec(
        OwnershipTokenParameterization ownershipTokenParameterization,
        int rawConfiguredIndex = 0,
        string? rowGuardPredicateSql = null
    ) =>
        new(
            new OwnershipAuthorizationCheckSpec(rawConfiguredIndex),
            ownershipTokenParameterization,
            DocumentIdParameterName,
            rowGuardPredicateSql
        );

    private static string Sql(params string[] lines) => string.Join("\n", lines) + "\n";

    /// <summary>
    /// The whole emitted shape for PostgreSQL. The arm order is the contract: authorized, then stored-null,
    /// then no-row, then mismatch as the fallthrough. Classifying stored-null ahead of no-row keeps a row
    /// that exists with an unassigned token reported as §2.14 rather than as a stale target.
    /// </summary>
    [Test]
    public void It_compiles_a_pgsql_check_with_the_authorized_arm_then_the_three_auth1_arms()
    {
        var compiler = new OwnershipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(Spec(Tokens(SqlDialect.Pgsql, 12)));

        plan.AuthorizationSql.Should()
            .Be(
                Sql(
                    "SELECT CASE",
                    "    WHEN EXISTS (SELECT 1 FROM \"dms\".\"Document\" d WHERE d.\"DocumentId\" = @documentId AND d.\"CreatedByOwnershipTokenId\" IS NOT NULL AND d.\"CreatedByOwnershipTokenId\" = ANY(@ownershipTokenIds)) THEN 1",
                    "    WHEN EXISTS (SELECT 1 FROM \"dms\".\"Document\" d WHERE d.\"DocumentId\" = @documentId AND d.\"CreatedByOwnershipTokenId\" IS NULL) THEN \"dms\".\"throw_error\"('AUTH1', 'own1|0|u')",
                    "    WHEN NOT EXISTS (SELECT 1 FROM \"dms\".\"Document\" d WHERE d.\"DocumentId\" = @documentId) THEN \"dms\".\"throw_error\"('AUTH1', 'own1|0|s')",
                    "    ELSE \"dms\".\"throw_error\"('AUTH1', 'own1|0|m')",
                    "END;"
                )
            );
        plan.ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal(DocumentIdParameterName, OwnershipTokenIdsParameterName);
    }

    /// <summary>
    /// The whole emitted shape for SQL Server: a parameterized scalar <c>IN</c> list and the
    /// <c>CAST('AUTH1 - ...' AS INT)</c> abort device. No table-valued parameter is involved, by design.
    /// </summary>
    [Test]
    public void It_compiles_a_mssql_check_with_a_scalar_in_list_and_cast_auth1_dash_throws()
    {
        var compiler = new OwnershipAuthorizationSqlCompiler(SqlDialect.Mssql);

        var plan = compiler.Compile(Spec(Tokens(SqlDialect.Mssql, 12, 34)));

        plan.AuthorizationSql.Should()
            .Be(
                Sql(
                    "SELECT CASE",
                    "    WHEN EXISTS (SELECT 1 FROM [dms].[Document] d WHERE d.[DocumentId] = @documentId AND d.[CreatedByOwnershipTokenId] IS NOT NULL AND d.[CreatedByOwnershipTokenId] IN (@ownershipTokenIds_0, @ownershipTokenIds_1)) THEN 1",
                    "    WHEN EXISTS (SELECT 1 FROM [dms].[Document] d WHERE d.[DocumentId] = @documentId AND d.[CreatedByOwnershipTokenId] IS NULL) THEN CAST('AUTH1 - own1|0|u' AS INT)",
                    "    WHEN NOT EXISTS (SELECT 1 FROM [dms].[Document] d WHERE d.[DocumentId] = @documentId) THEN CAST('AUTH1 - own1|0|s' AS INT)",
                    "    ELSE CAST('AUTH1 - own1|0|m' AS INT)",
                    "END;"
                )
            );
        plan.ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal(DocumentIdParameterName, "ownershipTokenIds_0", "ownershipTokenIds_1");
    }

    /// <summary>
    /// PostgreSQL binds one array parameter whatever the token count, so the emitted SQL text does not grow
    /// with the number of tokens and the command's parameter budget is unaffected by it.
    /// </summary>
    [Test]
    public void It_binds_one_pgsql_array_parameter_however_many_tokens_there_are()
    {
        var compiler = new OwnershipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var onePlan = compiler.Compile(Spec(Tokens(SqlDialect.Pgsql, 12)));
        var manyPlan = compiler.Compile(Spec(Tokens(SqlDialect.Pgsql, 12, 34, 56)));

        manyPlan.AuthorizationSql.Should().Be(onePlan.AuthorizationSql);
        manyPlan
            .ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal(DocumentIdParameterName, OwnershipTokenIdsParameterName);
    }

    /// <summary>
    /// SQL Server emits one scalar per deduplicated token, in the parameterization's ascending order, so the
    /// nth emitted placeholder always binds the nth token. Duplicates are collapsed before naming, which is
    /// why four supplied tokens emit three placeholders.
    /// </summary>
    [Test]
    public void It_binds_one_mssql_scalar_parameter_per_token_in_ascending_order()
    {
        var compiler = new OwnershipAuthorizationSqlCompiler(SqlDialect.Mssql);
        var parameterization = Tokens(SqlDialect.Mssql, 7, 3, 5, 3);

        var plan = compiler.Compile(Spec(parameterization));

        parameterization.TokensInOrder.Should().Equal((short)3, (short)5, (short)7);
        plan.AuthorizationSql.Should()
            .Contain(
                "d.[CreatedByOwnershipTokenId] IN (@ownershipTokenIds_0, @ownershipTokenIds_1, @ownershipTokenIds_2)"
            );
        plan.ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal(
                DocumentIdParameterName,
                "ownershipTokenIds_0",
                "ownershipTokenIds_1",
                "ownershipTokenIds_2"
            );
    }

    /// <summary>
    /// A client configured for OwnershipBased that holds no tokens still runs the check, so §2.14 stays
    /// distinguishable from §2.13, but its membership predicate is a constant false and it binds no token
    /// parameter. Binding one would leave a parameter the SQL never references, which co-batching rejects as
    /// a dangling parameter. The <c>IS NOT NULL</c> guard is deliberately retained so the emitted arm shape
    /// does not vary with the token count.
    /// </summary>
    [Test]
    public void It_renders_a_constant_false_pgsql_membership_predicate_and_binds_no_token_parameter()
    {
        var compiler = new OwnershipAuthorizationSqlCompiler(SqlDialect.Pgsql);

        var plan = compiler.Compile(Spec(Tokens(SqlDialect.Pgsql)));

        plan.AuthorizationSql.Should()
            .Be(
                Sql(
                    "SELECT CASE",
                    "    WHEN EXISTS (SELECT 1 FROM \"dms\".\"Document\" d WHERE d.\"DocumentId\" = @documentId AND d.\"CreatedByOwnershipTokenId\" IS NOT NULL AND 1 = 0) THEN 1",
                    "    WHEN EXISTS (SELECT 1 FROM \"dms\".\"Document\" d WHERE d.\"DocumentId\" = @documentId AND d.\"CreatedByOwnershipTokenId\" IS NULL) THEN \"dms\".\"throw_error\"('AUTH1', 'own1|0|u')",
                    "    WHEN NOT EXISTS (SELECT 1 FROM \"dms\".\"Document\" d WHERE d.\"DocumentId\" = @documentId) THEN \"dms\".\"throw_error\"('AUTH1', 'own1|0|s')",
                    "    ELSE \"dms\".\"throw_error\"('AUTH1', 'own1|0|m')",
                    "END;"
                )
            );
        plan.ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal(DocumentIdParameterName);
    }

    /// <summary>
    /// The same empty-list rendering for SQL Server, where a constant is not merely tidier than an empty
    /// list but required: <c>IN ()</c> is a syntax error.
    /// </summary>
    [Test]
    public void It_renders_a_constant_false_mssql_membership_predicate_and_binds_no_token_parameter()
    {
        var compiler = new OwnershipAuthorizationSqlCompiler(SqlDialect.Mssql);

        var plan = compiler.Compile(Spec(Tokens(SqlDialect.Mssql)));

        plan.AuthorizationSql.Should()
            .Be(
                Sql(
                    "SELECT CASE",
                    "    WHEN EXISTS (SELECT 1 FROM [dms].[Document] d WHERE d.[DocumentId] = @documentId AND d.[CreatedByOwnershipTokenId] IS NOT NULL AND 1 = 0) THEN 1",
                    "    WHEN EXISTS (SELECT 1 FROM [dms].[Document] d WHERE d.[DocumentId] = @documentId AND d.[CreatedByOwnershipTokenId] IS NULL) THEN CAST('AUTH1 - own1|0|u' AS INT)",
                    "    WHEN NOT EXISTS (SELECT 1 FROM [dms].[Document] d WHERE d.[DocumentId] = @documentId) THEN CAST('AUTH1 - own1|0|s' AS INT)",
                    "    ELSE CAST('AUTH1 - own1|0|m' AS INT)",
                    "END;"
                )
            );
        plan.ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal(DocumentIdParameterName);
        plan.AuthorizationSql.Should().NotContain("IN ()");
    }

    /// <summary>
    /// The payload carries the raw configured strategy index, not a normalized zero and not an emitted
    /// statement ordinal, on every one of the three failure arms and on both dialects. The response mapper
    /// attributes a denial by comparing this index with the planned check's, so an index that did not
    /// survive compilation would either misattribute the denial or force a 500.
    /// </summary>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void It_carries_the_configured_strategy_index_into_every_payload_on_both_dialects(
        int rawConfiguredIndex
    )
    {
        var pgsqlSql = new OwnershipAuthorizationSqlCompiler(SqlDialect.Pgsql)
            .Compile(Spec(Tokens(SqlDialect.Pgsql, 12), rawConfiguredIndex))
            .AuthorizationSql;
        var mssqlSql = new OwnershipAuthorizationSqlCompiler(SqlDialect.Mssql)
            .Compile(Spec(Tokens(SqlDialect.Mssql, 12), rawConfiguredIndex))
            .AuthorizationSql;

        foreach (var failureKindCode in new[] { "u", "s", "m" })
        {
            pgsqlSql
                .Should()
                .Contain($"\"dms\".\"throw_error\"('AUTH1', 'own1|{rawConfiguredIndex}|{failureKindCode}')");
            mssqlSql.Should().Contain($"CAST('AUTH1 - own1|{rawConfiguredIndex}|{failureKindCode}' AS INT)");
        }
    }

    /// <summary>
    /// The row guard is the device that makes a co-batched check vacuous when nothing was captured — a POST
    /// that resolved to a create. It must land as a trailing <c>WHERE</c> on the outer select, after
    /// <c>END</c>, so a false guard yields an empty result set and no arm — the abort device included —
    /// evaluates.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_appends_the_row_guard_as_a_trailing_where_clause(SqlDialect dialect)
    {
        var compiler = new OwnershipAuthorizationSqlCompiler(dialect);

        var plan = compiler.Compile(
            Spec(Tokens(dialect, 12), rowGuardPredicateSql: "EXISTS (SELECT 1 FROM captured)")
        );

        plan.AuthorizationSql.Should().EndWith("END WHERE EXISTS (SELECT 1 FROM captured);\n");
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_emits_no_where_clause_when_no_row_guard_is_supplied(SqlDialect dialect)
    {
        var compiler = new OwnershipAuthorizationSqlCompiler(dialect);

        var plan = compiler.Compile(Spec(Tokens(dialect, 12)));

        plan.AuthorizationSql.Should().EndWith("END;\n");
    }

    [Test]
    public void It_throws_when_constructed_for_an_unsupported_dialect()
    {
        Action act = () => new OwnershipAuthorizationSqlCompiler((SqlDialect)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// A parameterization built for the other provider would emit that provider's membership syntax, so the
    /// compiler refuses it rather than producing SQL the target server cannot parse.
    /// </summary>
    [Test]
    public void It_rejects_a_parameterization_built_for_the_other_dialect()
    {
        Action pgsqlCompilerWithMssqlTokens = () =>
            new OwnershipAuthorizationSqlCompiler(SqlDialect.Pgsql).Compile(
                Spec(Tokens(SqlDialect.Mssql, 12))
            );
        Action mssqlCompilerWithPgsqlTokens = () =>
            new OwnershipAuthorizationSqlCompiler(SqlDialect.Mssql).Compile(
                Spec(Tokens(SqlDialect.Pgsql, 12))
            );

        pgsqlCompilerWithMssqlTokens.Should().Throw<ArgumentException>();
        mssqlCompilerWithPgsqlTokens.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_a_null_spec()
    {
        Action act = () => new OwnershipAuthorizationSqlCompiler(SqlDialect.Pgsql).Compile(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void It_rejects_a_spec_with_no_check()
    {
        Action act = () =>
            new OwnershipAuthorizationSqlCompiler(SqlDialect.Pgsql).Compile(
                new OwnershipAuthorizationSqlSpec(
                    null!,
                    Tokens(SqlDialect.Pgsql, 12),
                    DocumentIdParameterName
                )
            );

        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void It_rejects_a_spec_with_no_token_parameterization()
    {
        Action act = () =>
            new OwnershipAuthorizationSqlCompiler(SqlDialect.Pgsql).Compile(
                new OwnershipAuthorizationSqlSpec(
                    new OwnershipAuthorizationCheckSpec(0),
                    null!,
                    DocumentIdParameterName
                )
            );

        act.Should().Throw<ArgumentNullException>();
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("@documentId")]
    [TestCase("document id")]
    public void It_rejects_an_invalid_document_id_parameter_name(string documentIdParameterName)
    {
        Action act = () =>
            new OwnershipAuthorizationSqlCompiler(SqlDialect.Pgsql).Compile(
                new OwnershipAuthorizationSqlSpec(
                    new OwnershipAuthorizationCheckSpec(0),
                    Tokens(SqlDialect.Pgsql, 12),
                    documentIdParameterName
                )
            );

        act.Should().Throw<ArgumentException>();
    }
}
