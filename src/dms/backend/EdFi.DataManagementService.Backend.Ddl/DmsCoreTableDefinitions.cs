// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

internal sealed record DmsCoreColumnDefinition(
    DbColumnName ColumnName,
    string SqlType,
    bool IsNullable,
    string? DefaultConstraintName = null,
    string? DefaultExpression = null
);

internal sealed record DmsCoreTableDefinition(
    DbTableName TableName,
    IReadOnlyList<DmsCoreColumnDefinition> Columns,
    IReadOnlyList<DbColumnName> PrimaryKeyColumns
);

internal static class DmsCoreTableDefinitions
{
    internal static DmsCoreTableDefinition Document(ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);

        return new DmsCoreTableDefinition(
            DmsTableNames.Document,
            [
                new(Col("DocumentId"), dialect.IdentityBigintColumnType, IsNullable: false),
                new(Col("DocumentUuid"), dialect.UuidColumnType, IsNullable: false),
                new(Col("ResourceKeyId"), dialect.SmallintColumnType, IsNullable: false),
                new(Col("CreatedByOwnershipTokenId"), dialect.SmallintColumnType, IsNullable: true),
                new(
                    Col("ContentVersion"),
                    "bigint",
                    IsNullable: false,
                    "DF_Document_ContentVersion",
                    SequenceDefault(dialect)
                ),
                new(
                    Col("IdentityVersion"),
                    "bigint",
                    IsNullable: false,
                    "DF_Document_IdentityVersion",
                    SequenceDefault(dialect)
                ),
                new(
                    Col("ContentLastModifiedAt"),
                    DateTimeType(dialect),
                    IsNullable: false,
                    "DF_Document_ContentLastModifiedAt",
                    dialect.CurrentTimestampDefaultExpression
                ),
                new(
                    Col("IdentityLastModifiedAt"),
                    DateTimeType(dialect),
                    IsNullable: false,
                    "DF_Document_IdentityLastModifiedAt",
                    dialect.CurrentTimestampDefaultExpression
                ),
                new(
                    Col("CreatedAt"),
                    DateTimeType(dialect),
                    IsNullable: false,
                    "DF_Document_CreatedAt",
                    dialect.CurrentTimestampDefaultExpression
                ),
            ],
            [Col("DocumentId")]
        );
    }

    internal static DmsCoreTableDefinition DocumentCache(ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);

        return new DmsCoreTableDefinition(
            DmsTableNames.DocumentCache,
            [
                new(Col("DocumentId"), dialect.DocumentIdColumnType, IsNullable: false),
                new(Col("DocumentUuid"), dialect.UuidColumnType, IsNullable: false),
                new(Col("ProjectName"), StringType(dialect, 256), IsNullable: false),
                new(Col("ResourceName"), StringType(dialect, 256), IsNullable: false),
                new(Col("ResourceVersion"), StringType(dialect, 32), IsNullable: false),
                new(Col("ContentVersion"), "bigint", IsNullable: false),
                new(Col("StreamEtag"), StreamEtagType(dialect), IsNullable: false),
                new(Col("LastModifiedAt"), DateTimeType(dialect), IsNullable: false),
                new(Col("DocumentJson"), dialect.JsonColumnType, IsNullable: false),
                new(
                    Col("ComputedAt"),
                    DateTimeType(dialect),
                    IsNullable: false,
                    DocumentCacheInventoryDefinition.DocumentCacheConstraints.ComputedAtDefault,
                    dialect.CurrentTimestampDefaultExpression
                ),
            ],
            [Col("DocumentId")]
        );
    }

    internal static DmsCoreTableDefinition DocumentProjectionWork(ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);

        return new DmsCoreTableDefinition(
            DmsTableNames.DocumentProjectionWork,
            [
                new(Col("DocumentId"), dialect.DocumentIdColumnType, IsNullable: false),
                new(Col("RequiredContentVersion"), "bigint", IsNullable: false),
                new(Col("FirstEnqueuedAt"), DateTimeType(dialect), IsNullable: false),
                new(Col("LastEnqueuedAt"), DateTimeType(dialect), IsNullable: false),
            ],
            [Col("DocumentId")]
        );
    }

    internal static DmsCoreTableDefinition CdcHeartbeat(ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);

        return new DmsCoreTableDefinition(
            DmsTableNames.CdcHeartbeat,
            [
                new(Col("HeartbeatId"), dialect.SmallintColumnType, IsNullable: false),
                new(Col("HeartbeatSequence"), "bigint", IsNullable: false),
                new(Col("HeartbeatAt"), DateTimeType(dialect), IsNullable: false),
            ],
            [Col("HeartbeatId")]
        );
    }

    private static DbColumnName Col(string name) => new(name);

    private static string StringType(ISqlDialect dialect, int maxLength) =>
        $"{dialect.Rules.ScalarTypeDefaults.StringType}({maxLength})";

    private static string StreamEtagType(ISqlDialect dialect) =>
        dialect.Rules.Dialect == SqlDialect.Mssql ? "varchar(64)" : StringType(dialect, 64);

    private static string DateTimeType(ISqlDialect dialect) => dialect.Rules.ScalarTypeDefaults.DateTimeType;

    private static string SequenceDefault(ISqlDialect dialect) =>
        dialect.RenderSequenceDefaultExpression(DmsTableNames.DmsSchema, DmsTableNames.ChangeVersionSequence);
}
