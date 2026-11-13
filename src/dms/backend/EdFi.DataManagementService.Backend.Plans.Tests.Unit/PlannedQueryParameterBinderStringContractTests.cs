// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// SQL Server declares a string query parameter from its plan contract rather than its value, so the
/// declaration does not vary with the filter values. Without a declared size SqlClient sizes each
/// parameter to its current value.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_PlannedQueryParameterBinder_String_Contracts
{
    private static readonly string _maxLengthUnicode = string.Concat(Enumerable.Repeat("\U0001F600", 16));

    // studentUniqueId nvarchar(32), firstName nvarchar(75), notes nvarchar(max), essay a bounded 5,000
    // character contract (wider than SQL Server's largest sized nvarchar), schoolId an integer.
    private static PageDocumentIdSqlPlan Plan() =>
        new(
            "SELECT 1",
            "SELECT 2",
            [
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    "studentUniqueId",
                    QuerySqlParameterBinding.CreateString(32)
                ),
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    "firstName",
                    QuerySqlParameterBinding.CreateString(75)
                ),
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    "notes",
                    QuerySqlParameterBinding.CreateString(null)
                ),
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    "essay",
                    QuerySqlParameterBinding.CreateString(5000)
                ),
                new QuerySqlParameter(QuerySqlParameterRole.Filter, "schoolId"),
                new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
            ],
            [
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    "studentUniqueId",
                    QuerySqlParameterBinding.CreateString(32)
                ),
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    "firstName",
                    QuerySqlParameterBinding.CreateString(75)
                ),
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    "notes",
                    QuerySqlParameterBinding.CreateString(null)
                ),
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    "essay",
                    QuerySqlParameterBinding.CreateString(5000)
                ),
                new QuerySqlParameter(QuerySqlParameterRole.Filter, "schoolId"),
            ]
        );

    private static Dictionary<string, object?> Values(
        string? studentUniqueId,
        string? firstName,
        string? notes,
        string? essay
    ) =>
        new()
        {
            ["studentUniqueId"] = studentUniqueId,
            ["firstName"] = firstName,
            ["notes"] = notes,
            ["essay"] = essay,
            ["schoolId"] = 255901,
            ["offset"] = 0L,
            ["limit"] = 25L,
        };

    private static SqlCommand BindThroughHydration(Dictionary<string, object?> values)
    {
        var command = new SqlCommand();
        HydrationBatchBuilder.AddParameters(
            command,
            new PageKeysetSpec.Query(Plan(), values, PageOrderingMode.DocumentId)
        );
        return command;
    }

    private static (SqlDbType, int) Declaration(SqlCommand command, string name) =>
        (command.Parameters[name].SqlDbType, command.Parameters[name].Size);

    private static IEnumerable<TestCaseData> IndependentlyVariedValues()
    {
        yield return new TestCaseData("", "", "", "").SetName("{m}(all empty)");
        yield return new TestCaseData("1", new string('a', 75), "n", new string('e', 5000)).SetName(
            "{m}(short, max, short, bounded max over 4000)"
        );
        yield return new TestCaseData(_maxLengthUnicode, "Zoë", new string('n', 9000), "é").SetName(
            "{m}(unicode at max, unicode, unbounded over 4000, unicode)"
        );
        yield return new TestCaseData(null, "Name42", null, null).SetName("{m}(typed nulls)");
    }

    [TestCaseSource(nameof(IndependentlyVariedValues))]
    public void It_declares_every_string_from_its_contract_whatever_the_values(
        string? studentUniqueId,
        string? firstName,
        string? notes,
        string? essay
    )
    {
        using var command = BindThroughHydration(Values(studentUniqueId, firstName, notes, essay));

        Declaration(command, "@studentUniqueId").Should().Be((SqlDbType.NVarChar, 32));
        Declaration(command, "@firstName").Should().Be((SqlDbType.NVarChar, 75));
        Declaration(command, "@notes").Should().Be((SqlDbType.NVarChar, -1));
        Declaration(command, "@essay").Should().Be((SqlDbType.NVarChar, -1));
    }

    [TestCaseSource(nameof(IndependentlyVariedValues))]
    public void It_binds_every_string_value_in_full(
        string? studentUniqueId,
        string? firstName,
        string? notes,
        string? essay
    )
    {
        using var command = BindThroughHydration(Values(studentUniqueId, firstName, notes, essay));

        command.Parameters["@studentUniqueId"].Value.Should().Be((object?)studentUniqueId ?? DBNull.Value);
        command.Parameters["@firstName"].Value.Should().Be((object?)firstName ?? DBNull.Value);
        command.Parameters["@notes"].Value.Should().Be((object?)notes ?? DBNull.Value);
        command.Parameters["@essay"].Value.Should().Be((object?)essay ?? DBNull.Value);
    }

    [Test]
    public void It_leaves_a_scalar_without_a_string_contract_to_sql_client_inference()
    {
        using var command = BindThroughHydration(Values("1", "a", "n", "e"));

        command.Parameters["@schoolId"].Size.Should().Be(0);
        command.Parameters["@schoolId"].SqlDbType.Should().Be(SqlDbType.Int);
    }

    [TestCase("studentUniqueId", 33)]
    [TestCase("essay", 5001)]
    public void It_refuses_a_value_wider_than_its_declared_contract_rather_than_truncating_it(
        string parameterName,
        int width
    )
    {
        var values = Values("1", "a", "n", "e");
        values[parameterName] = new string('x', width);

        var bind = () => BindThroughHydration(values);

        bind.Should()
            .Throw<InvalidOperationException>()
            .WithMessage($"*'{parameterName}'*{width} UTF-16 code units*")
            .Which.Message.Should()
            .NotContain(new string('x', width));
    }

    [Test]
    public void It_refuses_a_value_wider_than_its_contract_in_utf16_code_units()
    {
        var values = Values("1", "a", "n", "e");
        // 17 supplementary characters: 17 text elements, 34 UTF-16 code units, over nvarchar(32).
        values["studentUniqueId"] = string.Concat(Enumerable.Repeat("\U0001F600", 17));

        var bind = () => BindThroughHydration(values);

        bind.Should().Throw<InvalidOperationException>().WithMessage("*'studentUniqueId'*34 UTF-16*");
    }

    [Test]
    public void It_declares_strings_the_same_way_through_the_shared_bind_used_by_candidate_and_partition_commands()
    {
        var bindings = PlannedQueryParameterBinder.BindParameters(
            Plan(),
            Values("1", null, "n", "e"),
            "Test command",
            "Test parameter",
            "Unsupported."
        );
        using var command = new SqlCommand();

        foreach (var binding in bindings)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = binding.Name;
            parameter.Value = binding.Value ?? DBNull.Value;
            binding.ConfigureParameter?.Invoke(parameter);
            command.Parameters.Add(parameter);
        }

        Declaration(command, "@studentUniqueId").Should().Be((SqlDbType.NVarChar, 32));
        Declaration(command, "@firstName").Should().Be((SqlDbType.NVarChar, 75));
        command.Parameters["@firstName"].Value.Should().Be(DBNull.Value);
        bindings.Single(binding => binding.Name == "@schoolId").ConfigureParameter.Should().BeNull();
    }

    [Test]
    public void It_leaves_postgresql_parameters_exactly_as_an_untyped_scalar_binds_them()
    {
        using var contracted = new NpgsqlCommand();
        using var untyped = new NpgsqlCommand();
        HydrationBatchBuilder.AddParameters(
            contracted,
            new PageKeysetSpec.Query(Plan(), Values("1", null, "n", "e"), PageOrderingMode.DocumentId)
        );
        HydrationBatchBuilder.AddParameters(
            untyped,
            new PageKeysetSpec.Query(
                Plan() with
                {
                    PageParametersInOrder =
                    [
                        .. Plan()
                            .PageParametersInOrder.Select(parameter =>
                                parameter with
                                {
                                    Binding = QuerySqlParameterBinding.Scalar,
                                }
                            ),
                    ],
                    TotalCountParametersInOrder =
                    [
                        .. Plan()
                            .TotalCountParametersInOrder!.Value.Select(parameter =>
                                parameter with
                                {
                                    Binding = QuerySqlParameterBinding.Scalar,
                                }
                            ),
                    ],
                },
                Values("1", null, "n", "e"),
                PageOrderingMode.DocumentId
            )
        );

        Describe(contracted).Should().Equal(Describe(untyped));
    }

    [TestCase(3999, 3999)]
    [TestCase(4000, 4000)]
    [TestCase(4001, -1)]
    public void It_declares_max_only_above_the_largest_sized_sql_server_nvarchar(
        int contractLength,
        int expectedSize
    )
    {
        var plan = new PageDocumentIdSqlPlan(
            "SELECT 1",
            null,
            [
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    "note",
                    QuerySqlParameterBinding.CreateString(contractLength)
                ),
                new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
            ],
            null
        );
        string atContractLength = new('n', contractLength);

        foreach (string? value in new[] { atContractLength, "", null })
        {
            using var command = new SqlCommand();
            HydrationBatchBuilder.AddParameters(
                command,
                new PageKeysetSpec.Query(
                    plan,
                    new Dictionary<string, object?>
                    {
                        ["note"] = value,
                        ["offset"] = 0L,
                        ["limit"] = 25L,
                    },
                    PageOrderingMode.DocumentId
                )
            );

            Declaration(command, "@note").Should().Be((SqlDbType.NVarChar, expectedSize));
            command.Parameters["@note"].Value.Should().Be((object?)value ?? DBNull.Value);
        }
    }

    [Test]
    public void It_leaves_structured_and_untyped_null_parameters_configured_as_before()
    {
        var plan = new PageDocumentIdSqlPlan(
            "SELECT 1",
            null,
            [
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    "studentUniqueId",
                    QuerySqlParameterBinding.CreateString(32)
                ),
                new QuerySqlParameter(
                    QuerySqlParameterRole.Filter,
                    "claimEducationOrganizationIds",
                    QuerySqlParameterBinding.CreateMssqlStructured("dms.BigIntTable", "Id")
                ),
                new QuerySqlParameter(QuerySqlParameterRole.Filter, "schoolYear"),
                new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
            ],
            null
        );
        var values = new Dictionary<string, object?>
        {
            ["studentUniqueId"] = "1",
            ["claimEducationOrganizationIds"] = new List<long> { 255901L },
            ["schoolYear"] = null,
            ["offset"] = 0L,
            ["limit"] = 25L,
        };
        using var command = new SqlCommand();

        HydrationBatchBuilder.AddParameters(
            command,
            new PageKeysetSpec.Query(plan, values, PageOrderingMode.DocumentId)
        );

        command.Parameters["@claimEducationOrganizationIds"].SqlDbType.Should().Be(SqlDbType.Structured);
        command.Parameters["@claimEducationOrganizationIds"].TypeName.Should().Be("dms.BigIntTable");
        command.Parameters["@schoolYear"].Value.Should().Be(DBNull.Value);
        command.Parameters["@schoolYear"].Size.Should().Be(0);
        PlannedQueryParameterBinder
            .BindParameter(plan.PageParametersInOrder[2], null, "Test parameter", "Unsupported.")
            .ConfigureParameter.Should()
            .BeNull();
    }

    private static IEnumerable<(string, DbType, NpgsqlTypes.NpgsqlDbType, int, object?)> Describe(
        NpgsqlCommand command
    ) =>
        command
            .Parameters.Cast<NpgsqlParameter>()
            .Select(parameter =>
                (
                    parameter.ParameterName,
                    parameter.DbType,
                    parameter.NpgsqlDbType,
                    parameter.Size,
                    parameter.Value
                )
            );
}
