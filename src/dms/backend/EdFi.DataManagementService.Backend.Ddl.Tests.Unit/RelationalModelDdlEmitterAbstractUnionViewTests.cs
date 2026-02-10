// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_Pgsql_Ddl_Emitter_With_Abstract_Union_View
{
    private string _sql = default!;

    [SetUp]
    public void Setup()
    {
        var dialectRules = new PgsqlDialectRules();
        var emitter = new RelationalModelDdlEmitter(dialectRules);
        var modelSet = AbstractUnionViewFixture.Build(dialectRules.Dialect);

        _sql = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_union_view_arms_with_explicit_casts()
    {
        _sql.Should().Contain("CREATE VIEW \"edfi\".\"EducationOrganization_View\" AS");
        _sql.Should().Contain("CAST(\"DocumentId\" AS bigint) AS \"DocumentId\"");
        _sql.Should().Contain("CAST(\"SchoolId\" AS bigint) AS \"EducationOrganizationId\"");
        _sql.Should().Contain("CAST('Ed-Fi:LocalEducationAgency' AS varchar(256)) AS \"Discriminator\"");
        _sql.Should().Contain("CAST('Ed-Fi:School' AS varchar(256)) AS \"Discriminator\"");
    }

    [Test]
    public void It_should_emit_union_arms_in_declared_order()
    {
        var localEducationAgencyFromIndex = _sql.IndexOf(
            "FROM \"edfi\".\"LocalEducationAgency\"",
            StringComparison.Ordinal
        );
        var schoolFromIndex = _sql.IndexOf("FROM \"edfi\".\"School\"", StringComparison.Ordinal);

        localEducationAgencyFromIndex.Should().BeGreaterThanOrEqualTo(0);
        schoolFromIndex.Should().BeGreaterThan(localEducationAgencyFromIndex);
        _sql.Should().Contain("UNION ALL");
    }
}

[TestFixture]
public class Given_Mssql_Ddl_Emitter_With_Abstract_Union_View
{
    private string _sql = default!;

    [SetUp]
    public void Setup()
    {
        var dialectRules = new MssqlDialectRules();
        var emitter = new RelationalModelDdlEmitter(dialectRules);
        var modelSet = AbstractUnionViewFixture.Build(dialectRules.Dialect);

        _sql = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_union_view_arms_with_explicit_casts()
    {
        _sql.Should().Contain("CREATE VIEW [edfi].[EducationOrganization_View] AS");
        _sql.Should().Contain("CAST([DocumentId] AS bigint) AS [DocumentId]");
        _sql.Should().Contain("CAST([SchoolId] AS bigint) AS [EducationOrganizationId]");
        _sql.Should().Contain("CAST('Ed-Fi:LocalEducationAgency' AS nvarchar(256)) AS [Discriminator]");
        _sql.Should().Contain("CAST('Ed-Fi:School' AS nvarchar(256)) AS [Discriminator]");
    }

    [Test]
    public void It_should_emit_union_arms_in_declared_order()
    {
        var localEducationAgencyFromIndex = _sql.IndexOf(
            "FROM [edfi].[LocalEducationAgency]",
            StringComparison.Ordinal
        );
        var schoolFromIndex = _sql.IndexOf("FROM [edfi].[School]", StringComparison.Ordinal);

        localEducationAgencyFromIndex.Should().BeGreaterThanOrEqualTo(0);
        schoolFromIndex.Should().BeGreaterThan(localEducationAgencyFromIndex);
        _sql.Should().Contain("UNION ALL");
    }
}

internal static class AbstractUnionViewFixture
{
    private static readonly DbSchemaName _schema = new("edfi");
    private static readonly DbColumnName _documentId = new("DocumentId");
    private static readonly DbColumnName _localEducationAgencyId = new("LocalEducationAgencyId");
    private static readonly DbColumnName _schoolId = new("SchoolId");
    private static readonly DbColumnName _educationOrganizationId = new("EducationOrganizationId");
    private static readonly DbColumnName _discriminator = new("Discriminator");
    private static readonly JsonPathExpression _rootJsonPath = new("$", Array.Empty<JsonPathSegment>());
    private static readonly JsonPathExpression _educationOrganizationIdPath = new(
        "$.educationOrganizationId",
        new JsonPathSegment[] { new JsonPathSegment.Property("educationOrganizationId") }
    );

    public static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var abstractResource = new QualifiedResourceName("Ed-Fi", "EducationOrganization");
        var localEducationAgencyResource = new QualifiedResourceName("Ed-Fi", "LocalEducationAgency");
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");

        var abstractResourceKey = new ResourceKeyEntry((short)1, abstractResource, "1.0.0", true);
        var localEducationAgencyResourceKey = new ResourceKeyEntry(
            (short)2,
            localEducationAgencyResource,
            "1.0.0",
            false
        );
        var schoolResourceKey = new ResourceKeyEntry((short)3, schoolResource, "1.0.0", false);

        var localEducationAgencyTable = BuildRootTable(
            "LocalEducationAgency",
            "PK_LocalEducationAgency",
            _localEducationAgencyId,
            new RelationalScalarType(ScalarKind.Int64)
        );
        var schoolTable = BuildRootTable(
            "School",
            "PK_School",
            _schoolId,
            new RelationalScalarType(ScalarKind.Int32)
        );

        var localEducationAgencyModel = BuildConcreteResourceModel(
            localEducationAgencyResourceKey,
            localEducationAgencyTable
        );
        var schoolModel = BuildConcreteResourceModel(schoolResourceKey, schoolTable);

        var abstractUnionView = new AbstractUnionViewInfo(
            abstractResourceKey,
            new DbTableName(_schema, "EducationOrganization_View"),
            new AbstractUnionViewOutputColumn[]
            {
                new AbstractUnionViewOutputColumn(
                    _documentId,
                    new RelationalScalarType(ScalarKind.Int64),
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new AbstractUnionViewOutputColumn(
                    _educationOrganizationId,
                    new RelationalScalarType(ScalarKind.Int64),
                    _educationOrganizationIdPath,
                    TargetResource: null
                ),
                new AbstractUnionViewOutputColumn(
                    _discriminator,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 256),
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            },
            new AbstractUnionViewArm[]
            {
                new AbstractUnionViewArm(
                    localEducationAgencyResourceKey,
                    localEducationAgencyTable.Table,
                    new AbstractUnionViewProjectionExpression[]
                    {
                        new AbstractUnionViewProjectionExpression.SourceColumn(_documentId),
                        new AbstractUnionViewProjectionExpression.SourceColumn(_localEducationAgencyId),
                        new AbstractUnionViewProjectionExpression.StringLiteral("Ed-Fi:LocalEducationAgency"),
                    }
                ),
                new AbstractUnionViewArm(
                    schoolResourceKey,
                    schoolTable.Table,
                    new AbstractUnionViewProjectionExpression[]
                    {
                        new AbstractUnionViewProjectionExpression.SourceColumn(_documentId),
                        new AbstractUnionViewProjectionExpression.SourceColumn(_schoolId),
                        new AbstractUnionViewProjectionExpression.StringLiteral("Ed-Fi:School"),
                    }
                ),
            }
        );

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                3,
                new byte[] { 0x01 },
                new[] { new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false) },
                new[] { abstractResourceKey, localEducationAgencyResourceKey, schoolResourceKey }
            ),
            dialect,
            new[] { new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, _schema) },
            new[] { localEducationAgencyModel, schoolModel },
            Array.Empty<AbstractIdentityTableInfo>(),
            new[] { abstractUnionView },
            Array.Empty<DbIndexInfo>(),
            Array.Empty<DbTriggerInfo>()
        );
    }

    private static ConcreteResourceModel BuildConcreteResourceModel(
        ResourceKeyEntry resourceKey,
        DbTableModel rootTable
    )
    {
        var relationalModel = new RelationalResourceModel(
            resourceKey.Resource,
            _schema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            new[] { rootTable },
            Array.Empty<DocumentReferenceBinding>(),
            Array.Empty<DescriptorEdgeSource>()
        );

        return new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel);
    }

    private static DbTableModel BuildRootTable(
        string tableName,
        string primaryKeyName,
        DbColumnName identityColumn,
        RelationalScalarType identityType
    )
    {
        return new DbTableModel(
            new DbTableName(_schema, tableName),
            _rootJsonPath,
            new TableKey(primaryKeyName, new[] { new DbKeyColumn(_documentId, ColumnKind.ParentKeyPart) }),
            new DbColumnModel[]
            {
                new DbColumnModel(
                    _documentId,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    identityColumn,
                    ColumnKind.Scalar,
                    identityType,
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            },
            Array.Empty<TableConstraint>()
        );
    }
}
