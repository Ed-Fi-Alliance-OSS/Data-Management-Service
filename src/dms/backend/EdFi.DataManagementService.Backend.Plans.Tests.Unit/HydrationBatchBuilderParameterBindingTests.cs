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
    public void It_should_bind_single_keyset_document_id_as_a_scalar_parameter()
    {
        var command = new RecordingDbCommand(new DataTable().CreateDataReader());
        var keyset = new PageKeysetSpec.Single(42L);

        HydrationBatchBuilder.AddParameters(command, keyset);

        command.Parameters.Count.Should().Be(1);
        command.Parameters.Contains("@DocumentId").Should().BeTrue();
        command.Parameters["@DocumentId"].Value.Should().Be(42L);
    }

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
        command.Parameters["@offset"].Value.Should().Be(0L);
        command.Parameters["@limit"].Value.Should().Be(25L);
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
    public void It_should_throw_when_page_and_total_count_parameters_have_conflicting_metadata()
    {
        var command = new RecordingDbCommand(new DataTable().CreateDataReader());
        var keyset = new PageKeysetSpec.Query(
            CreateQueryPlan(
                pageParametersInOrder:
                [
                    new QuerySqlParameter(QuerySqlParameterRole.Filter, "authorizationIds"),
                    new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                    new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
                ],
                totalCountParametersInOrder:
                [
                    new QuerySqlParameter(
                        QuerySqlParameterRole.Filter,
                        "authorizationIds",
                        QuerySqlParameterBinding.PgsqlArray
                    ),
                ]
            ),
            new Dictionary<string, object?>
            {
                ["authorizationIds"] = new long[] { 10L, 20L },
                ["offset"] = 0L,
                ["limit"] = 25L,
            }
        );

        var act = () => HydrationBatchBuilder.AddParameters(command, keyset);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Hydration query keyset cannot bind parameter 'authorizationIds' with conflicting binding metadata."
            );
        command.Parameters.Count.Should().Be(0);
    }

    [Test]
    public void It_should_report_all_missing_required_parameter_values()
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
                    new QuerySqlParameter(QuerySqlParameterRole.Filter, "localEducationAgencyId"),
                ]
            ),
            new Dictionary<string, object?> { ["offset"] = 0L }
        );

        var act = () => HydrationBatchBuilder.AddParameters(command, keyset);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Hydration query keyset is missing required parameter values for ['schoolYear', 'limit', 'localEducationAgencyId']."
            );
        command.Parameters.Count.Should().Be(0);
    }

    [Test]
    public void It_should_throw_when_the_binding_kind_is_not_supported()
    {
        var command = new RecordingDbCommand(new DataTable().CreateDataReader());
        var keyset = new PageKeysetSpec.Query(
            CreateQueryPlan(
                pageParametersInOrder:
                [
                    new QuerySqlParameter(
                        QuerySqlParameterRole.Filter,
                        "unsupported",
                        new QuerySqlParameterBinding((QuerySqlParameterBindingKind)999)
                    ),
                    new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                    new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
                ],
                totalCountParametersInOrder: null
            ),
            new Dictionary<string, object?>
            {
                ["unsupported"] = 10L,
                ["offset"] = 0L,
                ["limit"] = 25L,
            }
        );

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            HydrationBatchBuilder.AddParameters(command, keyset)
        );

        exception.Should().NotBeNull();
        exception!.ParamName.Should().Be("parameter");
        exception.Message.Should().Contain("Unsupported query-parameter binding kind.");
        command.Parameters.Count.Should().Be(0);
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

    [Test]
    public void It_should_throw_when_a_non_list_value_is_supplied_for_a_mssql_structured_parameter()
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
                "Hydration query keyset parameter 'ClaimEducationOrganizationIds' requires an IReadOnlyList<long> runtime value."
            );
        command.Parameters.Count.Should().Be(0);
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
