// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

/// <summary>
/// Verifies the named default constraints emitted for the change-version mirror columns on resource root
/// tables, including dialect identifier-length shortening for long table names.
/// </summary>
[TestFixture]
public class Given_RelationalModelDdlEmitter_With_LongNamed_Root_And_Mirror_Columns
{
    // 120 characters: a valid MSSQL identifier on its own, but DF_<name>_ContentLastModifiedAt would be
    // 144 characters without shortening, exceeding the 128-character limit.
    private static readonly string _longTableName = new('A', 120);
    private string _mssqlDdl = default!;

    [SetUp]
    public void Setup()
    {
        var modelSet = BuildLongNamedRootModelSet(SqlDialect.Mssql);
        _mssqlDdl = new RelationalModelDdlEmitter(SqlDialectFactory.Create(SqlDialect.Mssql)).Emit(modelSet);
    }

    [Test]
    public void It_should_emit_a_named_default_for_the_content_last_modified_at_mirror_column()
    {
        _mssqlDdl.Should().Contain("[ContentLastModifiedAt]");
        _mssqlDdl.Should().Contain("DEFAULT (sysutcdatetime())");
    }

    [Test]
    public void It_should_keep_every_emitted_identifier_within_the_mssql_length_limit()
    {
        var maxLength = new MssqlDialectRules().MaxIdentifierLength;

        var overLimit = Regex
            .Matches(_mssqlDdl, @"\[(?<id>[^\]]+)\]")
            .Select(match => match.Groups["id"].Value)
            .Where(identifier => identifier.Length > maxLength)
            .Distinct()
            .ToArray();

        overLimit
            .Should()
            .BeEmpty(
                "all emitted MSSQL identifiers (including generated DF_ default constraint names) must stay "
                    + $"within the {maxLength}-character limit"
            );
    }

    [Test]
    public void It_should_shorten_the_mirror_default_constraint_name_for_the_long_root_table()
    {
        // The full DF_<120-char-table>_ContentLastModifiedAt name is 144 characters; emission must shorten
        // it rather than emit it verbatim.
        _mssqlDdl.Should().NotContain($"DF_{_longTableName}_ContentLastModifiedAt");
    }

    private static DerivedRelationalModelSet BuildLongNamedRootModelSet(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var resource = new QualifiedResourceName("Ed-Fi", _longTableName);
        var rootTableName = new DbTableName(schema, _longTableName);
        var rootTable = new DbTableModel(
            rootTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_LongRoot",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("ContentVersion"),
                    ColumnKind.MirroredContentVersion,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                )
                {
                    IsWritable = false,
                },
                new DbColumnModel(
                    new DbColumnName("ContentLastModifiedAt"),
                    ColumnKind.MirroredContentLastModifiedAt,
                    new RelationalScalarType(ScalarKind.DateTime),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                )
                {
                    IsWritable = false,
                },
            ],
            []
        );

        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);
        var concreteResource = new ConcreteResourceModel(
            resourceKey,
            ResourceStorageKind.RelationalTables,
            new RelationalResourceModel(
                resource,
                schema,
                ResourceStorageKind.RelationalTables,
                rootTable,
                [rootTable],
                [],
                []
            )
        );

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "abc123",
                1,
                [0xAB, 0xC1],
                [new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, new string('e', 64))],
                [resourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [concreteResource],
            [],
            [],
            [],
            []
        );
    }
}
