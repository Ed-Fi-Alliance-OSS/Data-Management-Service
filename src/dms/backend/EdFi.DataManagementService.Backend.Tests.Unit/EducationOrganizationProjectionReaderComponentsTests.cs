// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.SqlTypes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Reason = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionSchemaIncompatibilityReason;
using Result = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionSetResult;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_The_Education_Organization_Projection_Execution_Classifier
{
    [TestCase("42P01", Reason.SchemaObjectMissing)]
    [TestCase("42703", Reason.SchemaObjectMissing)]
    [TestCase("3F000", Reason.SchemaObjectMissing)]
    [TestCase("42704", Reason.SchemaObjectMissing)]
    [TestCase("42809", Reason.SchemaObjectMissing)]
    [TestCase("42883", Reason.SchemaObjectMissing)]
    [TestCase("42804", Reason.DataTypeIncompatible)]
    [TestCase("42846", Reason.DataTypeIncompatible)]
    [TestCase("42P18", Reason.DataTypeIncompatible)]
    [TestCase("22P02", Reason.DataTypeIncompatible)]
    [TestCase("22003", Reason.DataTypeIncompatible)]
    public void It_classifies_a_postgresql_schema_or_type_code_as_permanent(string sqlState, Reason reason)
    {
        EducationOrganizationProjectionExecutionClassifier
            .ClassifyPostgresql(sqlState, "PostgresException")
            .Should()
            .Be(new Result.SchemaIncompatible(reason, sqlState));
    }

    [TestCase("40P01")]
    [TestCase("55P03")]
    [TestCase("57014")]
    [TestCase("08006")]
    [TestCase("28P01")]
    [TestCase("53300")]
    [TestCase("57P01")]
    [TestCase("XX000")]
    [TestCase(null)]
    public void It_classifies_any_other_postgresql_code_as_transient(string? sqlState)
    {
        EducationOrganizationProjectionExecutionClassifier
            .ClassifyPostgresql(sqlState, "Describe")
            .Should()
            .Be(new Result.TargetUnavailable(EducationOrganizationProjectionReadStage.Execute, "Describe"));
    }

    [TestCase(207, Reason.SchemaObjectMissing)]
    [TestCase(208, Reason.SchemaObjectMissing)]
    [TestCase(4104, Reason.SchemaObjectMissing)]
    [TestCase(2812, Reason.SchemaObjectMissing)]
    [TestCase(1088, Reason.SchemaObjectMissing)]
    [TestCase(206, Reason.DataTypeIncompatible)]
    [TestCase(235, Reason.DataTypeIncompatible)]
    [TestCase(241, Reason.DataTypeIncompatible)]
    [TestCase(242, Reason.DataTypeIncompatible)]
    [TestCase(245, Reason.DataTypeIncompatible)]
    [TestCase(257, Reason.DataTypeIncompatible)]
    [TestCase(402, Reason.DataTypeIncompatible)]
    [TestCase(529, Reason.DataTypeIncompatible)]
    [TestCase(8114, Reason.DataTypeIncompatible)]
    [TestCase(8115, Reason.DataTypeIncompatible)]
    public void It_classifies_a_sql_server_schema_or_type_number_as_permanent(int number, Reason reason)
    {
        EducationOrganizationProjectionExecutionClassifier
            .ClassifyMssql(number, "SqlException")
            .Should()
            .Be(
                new Result.SchemaIncompatible(
                    reason,
                    number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                )
            );
    }

    [TestCase(1205)]
    [TestCase(1222)]
    [TestCase(-2)]
    [TestCase(4060)]
    [TestCase(18456)]
    [TestCase(10054)]
    [TestCase(40613)]
    [TestCase(50000)]
    [TestCase(null)]
    public void It_classifies_any_other_sql_server_number_as_transient(int? number)
    {
        EducationOrganizationProjectionExecutionClassifier
            .ClassifyMssql(number, "Describe")
            .Should()
            .Be(new Result.TargetUnavailable(EducationOrganizationProjectionReadStage.Execute, "Describe"));
    }

    [Test]
    public void It_recognizes_the_materialization_failures_and_nothing_broader()
    {
        Exception[] materialization =
        [
            new InvalidCastException(),
            new FormatException(),
            new OverflowException(),
            new SqlNullValueException(),
        ];
        Exception[] other =
        [
            new InvalidOperationException("other"),
            new NotSupportedException("other"),
            new TimeoutException("other"),
        ];

        materialization
            .Should()
            .OnlyContain(static exception =>
                EducationOrganizationProjectionExecutionClassifier.IsMaterializationFailure(exception)
            );
        other
            .Should()
            .NotContain(static exception =>
                EducationOrganizationProjectionExecutionClassifier.IsMaterializationFailure(exception)
            );
    }
}

[TestFixture]
[Parallelizable]
public class Given_The_Education_Organization_Projection_Row_Reader
{
    private static readonly EducationOrganizationProjectionResultColumns _columns =
        EducationOrganizationProjectionResultColumns.Default;

    [Test]
    public async Task It_reads_every_column_by_alias_with_nulls_preserved()
    {
        var table = Table();
        table.Rows.Add(101L, "Ed-Fi:LocalEducationAgency", "District", "", DBNull.Value, 100L, 10L, 1L);
        table.Rows.Add(
            7L,
            "Ed-Fi:StateEducationAgency",
            "State",
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value
        );

        var rows = await EducationOrganizationProjectionRowReader.ReadAllAsync(
            table.CreateDataReader(),
            _columns,
            EducationOrganizationProjectionNameEncoding.Text,
            CancellationToken.None
        );

        rows.Should()
            .Equal(
                new EducationOrganizationProjectionRow(
                    101,
                    "Ed-Fi:LocalEducationAgency",
                    "District",
                    "",
                    null,
                    100,
                    10,
                    1
                ),
                new EducationOrganizationProjectionRow(
                    7,
                    "Ed-Fi:StateEducationAgency",
                    "State",
                    null,
                    null,
                    null,
                    null,
                    null
                )
            );
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task It_raises_a_materialization_failure_for_a_null_in_a_required_column(int requiredOrdinal)
    {
        var table = Table();
        object[] values =
        [
            1L,
            "Ed-Fi:School",
            "School",
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
        ];
        values[requiredOrdinal] = DBNull.Value;
        table.Rows.Add(values);

        var act = () =>
            EducationOrganizationProjectionRowReader.ReadAllAsync(
                table.CreateDataReader(),
                _columns,
                EducationOrganizationProjectionNameEncoding.Text,
                CancellationToken.None
            );

        (await act.Should().ThrowAsync<Exception>())
            .Which.Should()
            .Match<Exception>(static exception =>
                EducationOrganizationProjectionExecutionClassifier.IsMaterializationFailure(exception)
            );
    }

    [Test]
    public async Task It_rebuilds_names_from_their_utf16_bytes_unit_for_unit()
    {
        // Built at run time: a [TestCase] string loses a lone surrogate in attribute metadata.
        (string Name, string? ShortName)[] names =
        [
            ("High \ud800", "x"),
            ("Low \udc00", "x"),
            ("x", "\ud800 high"),
            ("x", "low \udfff"),
            ("Pair \ud83d\ude00", ""),
            ("Literal \ufffd", null),
        ];
        names.Take(4).Should().OnlyContain(static pair => (pair.Name + pair.ShortName).Any(char.IsSurrogate));

        var table = Table(binaryNames: true);
        for (int index = 0; index < names.Length; index++)
        {
            table.Rows.Add(
                (long)index,
                "Ed-Fi:School",
                Utf16(names[index].Name),
                names[index].ShortName is { } shortName ? Utf16(shortName) : DBNull.Value,
                DBNull.Value,
                DBNull.Value,
                DBNull.Value,
                DBNull.Value
            );
        }

        var rows = await EducationOrganizationProjectionRowReader.ReadAllAsync(
            table.CreateDataReader(),
            _columns,
            EducationOrganizationProjectionNameEncoding.Utf16LittleEndianBytes,
            CancellationToken.None
        );

        Units(rows.Select(static row => (row.NameOfInstitution, row.ShortNameOfInstitution)))
            .Should()
            .Equal(Units(names.Select(static pair => (pair.Name, pair.ShortName))));
    }

    /// <summary>Code units in hex, so a replaced surrogate cannot compare equal by accident.</summary>
    private static string[] Units(IEnumerable<(string Name, string? ShortName)> names) =>
        [
            .. names.Select(static pair =>
                $"{Hex(pair.Name)}|{(pair.ShortName is null ? "null" : Hex(pair.ShortName))}"
            ),
        ];

    private static string Hex(string value) =>
        string.Join(
            " ",
            value.Select(static unit =>
                ((int)unit).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)
            )
        );

    [Test]
    public void It_reads_each_code_unit_low_byte_first()
    {
        EducationOrganizationProjectionRowReader
            .FromUtf16LittleEndian([0x00, 0xD8, 0x41, 0x00, 0x00, 0xDC])
            .Select(static unit => (int)unit)
            .Should()
            .Equal(0xD800, 0x0041, 0xDC00);
    }

    [Test]
    public async Task It_raises_a_materialization_failure_for_bytes_that_are_not_whole_code_units()
    {
        var table = Table(binaryNames: true);
        table.Rows.Add(
            1L,
            "Ed-Fi:School",
            new byte[] { 0x41, 0x00, 0x42 },
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value
        );

        var act = () =>
            EducationOrganizationProjectionRowReader.ReadAllAsync(
                table.CreateDataReader(),
                _columns,
                EducationOrganizationProjectionNameEncoding.Utf16LittleEndianBytes,
                CancellationToken.None
            );

        (await act.Should().ThrowAsync<Exception>())
            .Which.Should()
            .Match<Exception>(static exception =>
                EducationOrganizationProjectionExecutionClassifier.IsMaterializationFailure(exception)
            );
    }

    [Test]
    public async Task It_raises_a_materialization_failure_for_a_null_required_name_read_as_bytes()
    {
        var table = Table(binaryNames: true);
        table.Rows.Add(
            1L,
            "Ed-Fi:School",
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value,
            DBNull.Value
        );

        var act = () =>
            EducationOrganizationProjectionRowReader.ReadAllAsync(
                table.CreateDataReader(),
                _columns,
                EducationOrganizationProjectionNameEncoding.Utf16LittleEndianBytes,
                CancellationToken.None
            );

        (await act.Should().ThrowAsync<Exception>())
            .Which.Should()
            .Match<Exception>(static exception =>
                EducationOrganizationProjectionExecutionClassifier.IsMaterializationFailure(exception)
            );
    }

    private static byte[] Utf16(string value)
    {
        // Unit by unit, little-endian: Encoding.Unicode would replace a lone surrogate.
        byte[] bytes = new byte[value.Length * 2];
        for (int index = 0; index < value.Length; index++)
        {
            bytes[2 * index] = (byte)value[index];
            bytes[(2 * index) + 1] = (byte)(value[index] >> 8);
        }

        return bytes;
    }

    private static DataTable Table(bool binaryNames = false)
    {
        Type nameType = binaryNames ? typeof(byte[]) : typeof(string);
        var table = new DataTable();
        table.Columns.Add(_columns.EducationOrganizationId.Value, typeof(long));
        table.Columns.Add(_columns.Discriminator.Value, typeof(string));
        table.Columns.Add(_columns.NameOfInstitution.Value, nameType);
        table.Columns.Add(_columns.ShortNameOfInstitution.Value, nameType);
        table.Columns.Add(_columns.LocalEducationAgencyReference.Value, typeof(long));
        table.Columns.Add(_columns.ParentLocalEducationAgencyReference.Value, typeof(long));
        table.Columns.Add(_columns.EducationServiceCenterReference.Value, typeof(long));
        table.Columns.Add(_columns.StateEducationAgencyReference.Value, typeof(long));
        return table;
    }
}

[TestFixture]
[Parallelizable]
public class Given_The_Education_Organization_Projection_Set_Reader_Registrations
{
    [Test]
    public void It_registers_the_postgresql_reader_scoped_in_place_of_any_other()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEducationOrganizationProjectionSetReader>(
            FakeItEasy.A.Fake<IEducationOrganizationProjectionSetReader>()
        );

        services.AddPostgresqlEducationOrganizationProjectionSetReader();

        services
            .Should()
            .ContainSingle(static descriptor =>
                descriptor.ServiceType == typeof(IEducationOrganizationProjectionSetReader)
            )
            .Which.Should()
            .Match<ServiceDescriptor>(static descriptor =>
                descriptor.Lifetime == ServiceLifetime.Scoped
                && descriptor.ImplementationType == typeof(PostgresqlEducationOrganizationProjectionSetReader)
            );
    }

    [Test]
    public void It_registers_the_sql_server_reader_scoped_in_place_of_any_other()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEducationOrganizationProjectionSetReader>(
            FakeItEasy.A.Fake<IEducationOrganizationProjectionSetReader>()
        );

        services.AddMssqlEducationOrganizationProjectionSetReader();

        services
            .Should()
            .ContainSingle(static descriptor =>
                descriptor.ServiceType == typeof(IEducationOrganizationProjectionSetReader)
            )
            .Which.Should()
            .Match<ServiceDescriptor>(static descriptor =>
                descriptor.Lifetime == ServiceLifetime.Scoped
                && descriptor.ImplementationType == typeof(MssqlEducationOrganizationProjectionSetReader)
            );
    }

    [Test]
    public void It_is_part_of_the_postgresql_runtime_services()
    {
        var services = new ServiceCollection();

        services.AddPostgresqlDocumentCacheRuntimeServices(new ConfigurationBuilder().Build());

        services
            .Should()
            .ContainSingle(static descriptor =>
                descriptor.ServiceType == typeof(IEducationOrganizationProjectionSetReader)
                && descriptor.ImplementationType == typeof(PostgresqlEducationOrganizationProjectionSetReader)
            );
    }

    [Test]
    public void It_is_part_of_the_sql_server_runtime_services()
    {
        var services = new ServiceCollection();

        services.AddMssqlDocumentCacheRuntimeServices(new ConfigurationBuilder().Build());

        services
            .Should()
            .ContainSingle(static descriptor =>
                descriptor.ServiceType == typeof(IEducationOrganizationProjectionSetReader)
                && descriptor.ImplementationType == typeof(MssqlEducationOrganizationProjectionSetReader)
            );
    }
}
