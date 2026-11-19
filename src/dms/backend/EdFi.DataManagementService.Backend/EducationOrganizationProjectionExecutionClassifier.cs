// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.SqlTypes;
using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using SchemaReason = EdFi.DataManagementService.Backend.External.EducationOrganizationProjectionSchemaIncompatibilityReason;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Classifies a failure of the Execute and Materialize stage of the projection read. Consulted only for
/// that stage: the type-based acquisition classifiers would read a missing column as an unreachable
/// database, so they are never applied here.
/// </summary>
/// <remarks>
/// <para>
/// The permanent lists name the error codes that mean the target's physical schema or stored types do
/// not match the compiled statement. Every other code - deadlock, lock or statement timeout,
/// cancellation by the server, connection loss, authorization, resource exhaustion - and a failure
/// with no code is transient. That default is a documented bound on genuinely unknown failures (the
/// job layer retries them), not a claim that every permanent failure is recognized; a deterministic
/// code met in testing is proposed for the permanent list.
/// </para>
/// <para>
/// This assembly references neither driver, so each provider reader extracts its own code -
/// PostgreSQL's <c>SQLSTATE</c>, SQL Server's error number - and passes it in.
/// </para>
/// </remarks>
internal static class EducationOrganizationProjectionExecutionClassifier
{
    private static readonly HashSet<string> _postgresqlSchemaObjectMissing = new(StringComparer.Ordinal)
    {
        "42P01", // undefined_table
        "42703", // undefined_column
        "3F000", // invalid_schema_name
        "42704", // undefined_object
        "42809", // wrong_object_type
        "42883", // undefined_function
    };

    private static readonly HashSet<string> _postgresqlDataTypeIncompatible = new(StringComparer.Ordinal)
    {
        "42804", // datatype_mismatch
        "42846", // cannot_coerce
        "42P18", // indeterminate_datatype
        "22P02", // invalid_text_representation
        "22003", // numeric_value_out_of_range
    };

    private static readonly HashSet<int> _mssqlSchemaObjectMissing =
    [
        207, // invalid column name
        208, // invalid object name
        4104, // multi-part identifier could not be bound
        2812, // stored procedure not found
        1088, // object not found or no permission
    ];

    private static readonly HashSet<int> _mssqlDataTypeIncompatible =
    [
        206, // operand type clash
        235, // cannot convert char value to money
        241, // conversion failed converting date/time from string
        242, // conversion of char to datetime out of range
        245, // conversion failed converting a value to a data type
        257, // implicit conversion not allowed
        402, // data types incompatible in operator
        447, // expression type invalid for COLLATE: a name column that is no longer character data
        529, // explicit conversion not allowed
        8114, // error converting data type
        8115, // arithmetic overflow converting to data type
    ];

    /// <summary>
    /// Classifies a PostgreSQL execution failure by its <c>SQLSTATE</c>.
    /// </summary>
    public static EducationOrganizationProjectionSetResult ClassifyPostgresql(
        string? sqlState,
        string describe
    )
    {
        if (sqlState is not null && _postgresqlSchemaObjectMissing.Contains(sqlState))
        {
            return new EducationOrganizationProjectionSetResult.SchemaIncompatible(
                SchemaReason.SchemaObjectMissing,
                sqlState
            );
        }

        if (sqlState is not null && _postgresqlDataTypeIncompatible.Contains(sqlState))
        {
            return new EducationOrganizationProjectionSetResult.SchemaIncompatible(
                SchemaReason.DataTypeIncompatible,
                sqlState
            );
        }

        return new EducationOrganizationProjectionSetResult.TargetUnavailable(
            EducationOrganizationProjectionReadStage.Execute,
            describe
        );
    }

    /// <summary>
    /// Classifies a SQL Server execution failure by its error number.
    /// </summary>
    public static EducationOrganizationProjectionSetResult ClassifyMssql(int? number, string describe)
    {
        if (number is { } schemaNumber && _mssqlSchemaObjectMissing.Contains(schemaNumber))
        {
            return new EducationOrganizationProjectionSetResult.SchemaIncompatible(
                SchemaReason.SchemaObjectMissing,
                schemaNumber.ToString(CultureInfo.InvariantCulture)
            );
        }

        if (number is { } typeNumber && _mssqlDataTypeIncompatible.Contains(typeNumber))
        {
            return new EducationOrganizationProjectionSetResult.SchemaIncompatible(
                SchemaReason.DataTypeIncompatible,
                typeNumber.ToString(CultureInfo.InvariantCulture)
            );
        }

        return new EducationOrganizationProjectionSetResult.TargetUnavailable(
            EducationOrganizationProjectionReadStage.Execute,
            describe
        );
    }

    /// <summary>
    /// True for an exception raised while reading a returned value as the type the row requires.
    /// </summary>
    public static bool IsMaterializationFailure(Exception exception) =>
        exception is InvalidCastException or FormatException or OverflowException or SqlNullValueException;
}
