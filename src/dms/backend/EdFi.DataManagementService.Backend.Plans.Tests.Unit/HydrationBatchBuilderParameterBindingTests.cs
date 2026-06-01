// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_HydrationBatchBuilder_Query_Parameter_Binding
{
    [Test]
    public void It_should_bind_scalar_parameters_once_when_shared_by_page_and_total_count_sql()
    {
        var command = new RecordingDbCommand(new DataTable().CreateDataReader());
        var keyset = new PageKeysetSpec.Query(
            CreateQueryPlan(
                pageParametersInOrder:
                [
                    new QuerySqlParameter(QuerySqlParameterRole.Filter, "schoolYear"),
                    new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                    new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
                ],
                totalCountParametersInOrder:
                [
                    new QuerySqlParameter(QuerySqlParameterRole.Filter, "schoolYear"),
                ]
            ),
            new Dictionary<string, object?>
            {
                ["schoolYear"] = null,
                ["offset"] = 0L,
                ["limit"] = 25L,
            }
        );

        HydrationBatchBuilder.AddParameters(command, keyset);

        command.Parameters.Count.Should().Be(3);
        command.Parameters.Contains("@schoolYear").Should().BeTrue();
        command.Parameters.Contains("@offset").Should().BeTrue();
        command.Parameters.Contains("@limit").Should().BeTrue();
        command.Parameters["@schoolYear"].Value.Should().Be(DBNull.Value);
    }

    [Test]
    public void It_should_bind_postgresql_array_parameter_as_a_single_value()
    {
        using var command = new NpgsqlCommand();
        var keyset = new PageKeysetSpec.Query(
            CreateQueryPlan(
                pageParametersInOrder:
                [
                    new QuerySqlParameter(
                        QuerySqlParameterRole.Filter,
                        "ClaimEducationOrganizationIds",
                        QuerySqlParameterBinding.PgsqlArray
                    ),
                    new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                    new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
                ],
                totalCountParametersInOrder:
                [
                    new QuerySqlParameter(
                        QuerySqlParameterRole.Filter,
                        "ClaimEducationOrganizationIds",
                        QuerySqlParameterBinding.PgsqlArray
                    ),
                ]
            ),
            new Dictionary<string, object?>
            {
                ["ClaimEducationOrganizationIds"] = new long[] { 10L, 20L, 30L },
                ["offset"] = 0L,
                ["limit"] = 25L,
            }
        );

        HydrationBatchBuilder.AddParameters(command, keyset);

        command.Parameters.Count.Should().Be(3);
        command.Parameters.Contains("@ClaimEducationOrganizationIds").Should().BeTrue();
        command
            .Parameters["@ClaimEducationOrganizationIds"]
            .Value.Should()
            .BeEquivalentTo(new long[] { 10L, 20L, 30L });
    }

    [Test]
    public void It_should_bind_postgresql_array_parameter_for_string_namespace_prefix_patterns()
    {
        using var command = new NpgsqlCommand();
        var keyset = new PageKeysetSpec.Query(
            CreateQueryPlan(
                pageParametersInOrder:
                [
                    new QuerySqlParameter(
                        QuerySqlParameterRole.Filter,
                        "namespacePrefixes",
                        QuerySqlParameterBinding.PgsqlArray
                    ),
                    new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                    new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
                ],
                totalCountParametersInOrder: null
            ),
            new Dictionary<string, object?>
            {
                ["namespacePrefixes"] = new[] { "uri://ed-fi.org/%", "uri://gbisd.edu/%" },
                ["offset"] = 0L,
                ["limit"] = 25L,
            }
        );

        HydrationBatchBuilder.AddParameters(command, keyset);

        command.Parameters.Count.Should().Be(3);
        command.Parameters.Contains("@namespacePrefixes").Should().BeTrue();
        command
            .Parameters["@namespacePrefixes"]
            .Value.Should()
            .BeEquivalentTo(new[] { "uri://ed-fi.org/%", "uri://gbisd.edu/%" });
    }

    [Test]
    public void It_should_bind_mssql_structured_parameter_with_expected_type_and_table_value()
    {
        using var command = new SqlCommand();
        var keyset = new PageKeysetSpec.Query(
            CreateQueryPlan(
                pageParametersInOrder:
                [
                    new QuerySqlParameter(
                        QuerySqlParameterRole.Filter,
                        "ClaimEducationOrganizationIds",
                        QuerySqlParameterBinding.CreateMssqlStructured("dms.BigIntTable", "Id")
                    ),
                    new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                    new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
                ],
                totalCountParametersInOrder:
                [
                    new QuerySqlParameter(
                        QuerySqlParameterRole.Filter,
                        "ClaimEducationOrganizationIds",
                        QuerySqlParameterBinding.CreateMssqlStructured("dms.BigIntTable", "Id")
                    ),
                ]
            ),
            new Dictionary<string, object?>
            {
                ["ClaimEducationOrganizationIds"] = new long[] { 10L, 20L, 30L },
                ["offset"] = 0L,
                ["limit"] = 25L,
            }
        );

        HydrationBatchBuilder.AddParameters(command, keyset);

        command.Parameters.Count.Should().Be(3);
        var parameter = command.Parameters["@ClaimEducationOrganizationIds"];
        parameter.ParameterName.Should().Be("@ClaimEducationOrganizationIds");
        parameter.SqlDbType.Should().Be(SqlDbType.Structured);
        parameter.TypeName.Should().Be("dms.BigIntTable");
        parameter.Value.Should().BeOfType<DataTable>();

        var table = (DataTable)parameter.Value;
        table.Columns.Should().ContainSingle();
        table.Columns[0].ColumnName.Should().Be("Id");
        table.Columns[0].DataType.Should().Be(typeof(long));
        table.Rows.Cast<DataRow>().Select(static row => row.Field<long>("Id")).Should().Equal(10L, 20L, 30L);
    }

    [Test]
    public void It_should_throw_when_a_non_list_value_is_supplied_for_a_postgresql_array_parameter()
    {
        using var command = new NpgsqlCommand();
        var keyset = new PageKeysetSpec.Query(
            CreateQueryPlan(
                pageParametersInOrder:
                [
                    new QuerySqlParameter(
                        QuerySqlParameterRole.Filter,
                        "ClaimEducationOrganizationIds",
                        QuerySqlParameterBinding.PgsqlArray
                    ),
                    new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                    new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
                ],
                totalCountParametersInOrder: null
            ),
            new Dictionary<string, object?>
            {
                ["ClaimEducationOrganizationIds"] = 10L,
                ["offset"] = 0L,
                ["limit"] = 25L,
            }
        );

        var act = () => HydrationBatchBuilder.AddParameters(command, keyset);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Hydration query keyset parameter 'ClaimEducationOrganizationIds' requires an IReadOnlyList<long> or IReadOnlyList<string> runtime value."
            );
    }

    private static PageDocumentIdSqlPlan CreateQueryPlan(
        IReadOnlyList<QuerySqlParameter> pageParametersInOrder,
        IReadOnlyList<QuerySqlParameter>? totalCountParametersInOrder
    )
    {
        return new PageDocumentIdSqlPlan(
            PageDocumentIdSql: "SELECT 1",
            TotalCountSql: totalCountParametersInOrder is null ? null : "SELECT COUNT(1)",
            PageParametersInOrder: pageParametersInOrder,
            TotalCountParametersInOrder: totalCountParametersInOrder
        );
    }
}
