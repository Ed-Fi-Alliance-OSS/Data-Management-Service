// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_Pgsql_Ddl_Emitter_With_Primary_Key_Constraint_Name
{
    private string _sql = default!;

    [SetUp]
    public void Setup()
    {
        var dialectRules = new PgsqlDialectRules();
        var emitter = new RelationalModelDdlEmitter(dialectRules);
        var modelSet = PrimaryKeyFixture.Build(dialectRules.Dialect, "PK_School");

        _sql = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_named_primary_key_constraint()
    {
        _sql.Should().Contain("CONSTRAINT \"PK_School\" PRIMARY KEY (\"DocumentId\")");
    }
}

[TestFixture]
public class Given_Mssql_Ddl_Emitter_With_Primary_Key_Constraint_Name
{
    private string _sql = default!;

    [SetUp]
    public void Setup()
    {
        var dialectRules = new MssqlDialectRules();
        var emitter = new RelationalModelDdlEmitter(dialectRules);
        var modelSet = PrimaryKeyFixture.Build(dialectRules.Dialect, "PK_School");

        _sql = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_named_primary_key_constraint()
    {
        _sql.Should().Contain("CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId])");
    }
}

internal static class PrimaryKeyFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect, string primaryKeyName)
    {
        var schema = new DbSchemaName("edfi");
        var tableName = new DbTableName(schema, "School");
        var columnName = new DbColumnName("DocumentId");
        var keyColumn = new DbKeyColumn(columnName, ColumnKind.ParentKeyPart);
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);
        var table = new DbTableModel(
            tableName,
            new JsonPathExpression("$", Array.Empty<JsonPathSegment>()),
            new TableKey(primaryKeyName, [keyColumn]),
            new[]
            {
                new DbColumnModel(
                    columnName,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            },
            Array.Empty<TableConstraint>()
        );
        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            table,
            [table],
            Array.Empty<DocumentReferenceBinding>(),
            Array.Empty<DescriptorEdgeSource>()
        );

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                1,
                [0x01],
                [new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false)],
                [resourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)],
            Array.Empty<AbstractIdentityTableInfo>(),
            Array.Empty<AbstractUnionViewInfo>(),
            Array.Empty<DbIndexInfo>(),
            Array.Empty<DbTriggerInfo>()
        );
    }
}
