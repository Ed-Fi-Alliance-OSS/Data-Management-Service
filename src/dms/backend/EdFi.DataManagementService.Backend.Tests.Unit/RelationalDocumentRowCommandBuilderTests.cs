// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The <c>dms.Document</c> insert statement, which is the single place
/// <c>CreatedByOwnershipTokenId</c> is stamped for regular resources — both the co-batched write path and
/// the ordered-segment one build their insert here.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_RelationalDocumentRowCommandBuilder
{
    private const string OwnershipTokenParameterName = "@createdByOwnershipTokenId";

    private static readonly DocumentUuid _documentUuid = new(
        Guid.Parse("11111111-2222-3333-4444-555555555555")
    );

    private static RelationalCommand Insert(SqlDialect dialect, short? createdByOwnershipTokenId) =>
        RelationalDocumentRowCommandBuilder.BuildInsertCommand(
            dialect,
            _documentUuid,
            resourceKeyId: 7,
            createdByOwnershipTokenId
        );

    [TestCase(SqlDialect.Pgsql, "\"CreatedByOwnershipTokenId\"")]
    [TestCase(SqlDialect.Mssql, "[CreatedByOwnershipTokenId]")]
    public void It_emits_the_ownership_column_in_the_insert(SqlDialect dialect, string quotedColumn)
    {
        Insert(dialect, 42).CommandText.Should().Contain(quotedColumn);
    }

    /// <summary>
    /// The column is emitted for a null token too, so each dialect has exactly one statement text. A
    /// conditional column list would double statement-text cardinality and cost plan reuse on both engines.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_emits_one_statement_text_whether_or_not_the_client_has_a_creator_token(SqlDialect dialect)
    {
        Insert(dialect, 42).CommandText.Should().Be(Insert(dialect, null).CommandText);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_binds_the_creator_ownership_token(SqlDialect dialect)
    {
        Parameter(Insert(dialect, 42)).Value.Should().Be((short)42);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_binds_null_when_the_client_has_no_creator_token(SqlDialect dialect)
    {
        Parameter(Insert(dialect, null)).Value.Should().BeNull();
    }

    /// <summary>
    /// The parameter declares <c>smallint</c> rather than relying on provider inference. A null value reaches
    /// the driver as <c>DBNull</c>, which carries no type: PostgreSQL would have to infer one from the insert
    /// target and SQL Server would default to a string type and rely on an implicit conversion. Declaring the
    /// type makes the null and non-null cases bind identically on both engines.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_declares_the_ownership_parameter_as_smallint(SqlDialect dialect)
    {
        foreach (short? token in new short?[] { 42, null })
        {
            var parameter = Parameter(Insert(dialect, token));
            parameter.ConfigureParameter.Should().NotBeNull();

            using var probe = new ProbeDbParameter();
            parameter.ConfigureParameter!(probe);

            probe.DbType.Should().Be(DbType.Int16);
        }
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_binds_exactly_three_parameters(SqlDialect dialect)
    {
        Insert(dialect, 42)
            .Parameters.Select(static parameter => parameter.Name)
            .Should()
            .BeEquivalentTo("@documentUuid", "@resourceKeyId", OwnershipTokenParameterName);
    }

    [Test]
    public void It_rejects_an_unsupported_dialect()
    {
        Action act = () => Insert((SqlDialect)999, 42);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// The subquery a later statement uses to re-derive the created DocumentId is unaffected by the added
    /// column, so a create's downstream statements still resolve their root id.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_leaves_the_document_id_subquery_free_of_the_ownership_column(SqlDialect dialect)
    {
        RelationalDocumentRowCommandBuilder
            .BuildDocumentIdSubquery(dialect, "@documentUuid")
            .Should()
            .NotContain("CreatedByOwnershipTokenId");
    }

    private static RelationalParameter Parameter(RelationalCommand command) =>
        command.Parameters.Single(parameter => parameter.Name == OwnershipTokenParameterName);

    /// <summary>
    /// A minimal <see cref="DbParameter"/> so the provider-agnostic configuration callback can be observed
    /// without a real provider command.
    /// </summary>
    private sealed class ProbeDbParameter : DbParameter, IDisposable
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;

        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;

        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }
        public override int Size { get; set; }

        public override void ResetDbType() => DbType = default;

        public void Dispose() { }
    }
}
