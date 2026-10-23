// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl.PublicContract.CompileCheck;

public static class DdlContractPublicContractCompileCheck
{
    /// <summary>
    /// Pins <see cref="IdentityElementMapping"/> to exactly <c>(Column, IdentityJsonPath)</c>: construction and
    /// deconstruction both fail to compile if a member is added or removed.
    /// </summary>
    public static IdentityElementMapping RoundTripIdentityElementMapping(
        DbColumnName column,
        string identityJsonPath
    )
    {
        var (deconstructedColumn, deconstructedPath) = new IdentityElementMapping(column, identityJsonPath);

        return new IdentityElementMapping(deconstructedColumn, deconstructedPath);
    }
}

/// <summary>
/// Pins the exact <see cref="ISqlDialect"/> member set. Every member is implemented explicitly, so a member
/// added to the interface (for example a reintroduced <c>CreateUuidv5Function</c>) fails with CS0535 and a
/// removed member fails with CS0539.
/// </summary>
public sealed class ExactSqlDialectContract : ISqlDialect
{
    ISqlDialectRules ISqlDialect.Rules => throw new NotSupportedException();

    string ISqlDialect.QuoteIdentifier(string identifier) => throw new NotSupportedException();

    string ISqlDialect.QualifyTable(DbTableName table) => throw new NotSupportedException();

    string ISqlDialect.RenderColumnType(RelationalScalarType scalarType) => throw new NotSupportedException();

    string ISqlDialect.DocumentIdColumnType => throw new NotSupportedException();

    string ISqlDialect.OrdinalColumnType => throw new NotSupportedException();

    string ISqlDialect.RenderScriptPrologue() => throw new NotSupportedException();

    string ISqlDialect.CreateSchemaIfNotExists(DbSchemaName schema) => throw new NotSupportedException();

    string ISqlDialect.CreateTableHeader(DbTableName table) => throw new NotSupportedException();

    string ISqlDialect.DropTriggerIfExists(DbTableName table, string triggerName) =>
        throw new NotSupportedException();

    DdlPattern ISqlDialect.ViewCreationPattern => throw new NotSupportedException();

    string ISqlDialect.CreateSequenceIfNotExists(DbSchemaName schema, string sequenceName, long startWith) =>
        throw new NotSupportedException();

    string ISqlDialect.CreateIndexIfNotExists(
        DbTableName table,
        string indexName,
        IReadOnlyList<DbColumnName> columns,
        bool isUnique,
        IReadOnlyList<DbColumnName>? includeColumns,
        DbColumnName? notNullFilterColumn
    ) => throw new NotSupportedException();

    string ISqlDialect.AddForeignKeyConstraint(
        DbTableName table,
        string constraintName,
        IReadOnlyList<DbColumnName> columns,
        DbTableName targetTable,
        IReadOnlyList<DbColumnName> targetColumns,
        ReferentialAction onDelete,
        ReferentialAction onUpdate
    ) => throw new NotSupportedException();

    string ISqlDialect.AddUniqueConstraint(
        DbTableName table,
        string constraintName,
        IReadOnlyList<DbColumnName> columns
    ) => throw new NotSupportedException();

    string ISqlDialect.AddCheckConstraint(DbTableName table, string constraintName, string checkExpression) =>
        throw new NotSupportedException();

    string ISqlDialect.RenderColumnDefinition(
        DbColumnName columnName,
        string sqlType,
        bool isNullable,
        string? defaultExpression
    ) => throw new NotSupportedException();

    string ISqlDialect.RenderColumnDefinitionWithNamedDefault(
        DbColumnName columnName,
        string sqlType,
        bool isNullable,
        string? constraintName,
        string? defaultExpression
    ) => throw new NotSupportedException();

    string ISqlDialect.RenderPrimaryKeyClause(IReadOnlyList<DbColumnName> columns) =>
        throw new NotSupportedException();

    string ISqlDialect.RenderNamedPrimaryKeyClause(
        string constraintName,
        IReadOnlyList<DbColumnName> columns,
        bool clustered
    ) => throw new NotSupportedException();

    string ISqlDialect.RenderReferentialAction(ReferentialAction action) => throw new NotSupportedException();

    string ISqlDialect.CreateExtensionIfNotExists(string extensionName) => throw new NotSupportedException();

    string ISqlDialect.CreateGetMaxChangeVersionFunction(DbSchemaName schema) =>
        throw new NotSupportedException();

    string ISqlDialect.CreateThrowErrorFunction(DbSchemaName schema) => throw new NotSupportedException();

    string ISqlDialect.CreateUserDefinedTableTypeIfNotExists(
        DbSchemaName schema,
        string typeName,
        string columnName,
        string columnType
    ) => throw new NotSupportedException();

    string ISqlDialect.SmallintColumnType => throw new NotSupportedException();

    string ISqlDialect.UuidColumnType => throw new NotSupportedException();

    string ISqlDialect.JsonColumnType => throw new NotSupportedException();

    string ISqlDialect.IdentityIntColumnType => throw new NotSupportedException();

    string ISqlDialect.IdentityBigintColumnType => throw new NotSupportedException();

    string ISqlDialect.CurrentTimestampDefaultExpression => throw new NotSupportedException();

    string ISqlDialect.RenderBinaryColumnType(int length) => throw new NotSupportedException();

    string ISqlDialect.RenderSequenceDefaultExpression(DbSchemaName schema, string sequenceName) =>
        throw new NotSupportedException();

    string ISqlDialect.RenderBinaryLiteral(byte[] value) => throw new NotSupportedException();

    string ISqlDialect.RenderBooleanLiteral(bool value) => throw new NotSupportedException();

    string ISqlDialect.RenderStringLiteral(string value) => throw new NotSupportedException();

    string ISqlDialect.RenderSmallintLiteral(short value) => throw new NotSupportedException();

    string ISqlDialect.RenderIntegerLiteral(int value) => throw new NotSupportedException();

    string ISqlDialect.RenderComputedColumnDefinition(
        DbColumnName columnName,
        string sqlType,
        DbColumnName canonicalColumn,
        DbColumnName? presenceColumn
    ) => throw new NotSupportedException();
}
