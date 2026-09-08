// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_MssqlPlanDialect
{
    private IPlanSqlDialect _dialect = null!;
    private SqlWriter _writer = null!;
    private KeysetTableContract _keyset = null!;

    [SetUp]
    public void Setup()
    {
        _dialect = PlanSqlDialectFactory.Create(SqlDialect.Mssql);
        _writer = new SqlWriter(SqlDialectFactory.Create(SqlDialect.Mssql));
        _keyset = KeysetTableConventions.GetKeysetTableContract(SqlDialect.Mssql);
    }

    [Test]
    public void It_should_report_single_document_hydration_support()
    {
        _dialect.Dialect.Should().Be(SqlDialect.Mssql);
        _dialect.DisplayName.Should().Be("SQL Server");
        _dialect.SupportsSingleDocumentHydration.Should().BeFalse();
    }

    [Test]
    public void It_should_emit_canonical_keyset_temp_table_ddl()
    {
        _dialect.AppendCreateKeysetTempTable(_writer, _keyset);

        _writer
            .ToString()
            .Should()
            .Be(
                """
                IF OBJECT_ID('tempdb..[#page]') IS NOT NULL
                    DROP TABLE [#page];
                CREATE TABLE [#page] ([DocumentId] bigint PRIMARY KEY);

                """
            );
    }

    [Test]
    public void It_should_emit_canonical_document_metadata_select()
    {
        _dialect.AppendDocumentMetadataSelect(_writer, _keyset);

        _writer
            .ToString()
            .Should()
            .Be(
                """
                SELECT
                    d.[DocumentId],
                    d.[DocumentUuid],
                    d.[ContentVersion],
                    d.[IdentityVersion],
                    d.[ContentLastModifiedAt],
                    d.[IdentityLastModifiedAt]
                FROM [dms].[Document] d
                INNER JOIN [#page] k ON d.[DocumentId] = k.[DocumentId]
                ORDER BY d.[DocumentId];

                """
            );
    }

    [Test]
    public void It_should_reject_single_document_metadata_select()
    {
        var act = () =>
            _dialect.AppendSingleDocumentMetadataSelect(
                _writer,
                HydrationSqlConventions.SingleDocumentIdParameterName
            );

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage("SQL Server plan dialect does not support single-document hydration.");
    }
}
