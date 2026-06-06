// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_MssqlTokenInfoEducationOrganizationLookup
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly QualifiedResourceName _educationOrganizationResource = new(
        "Ed-Fi",
        "EducationOrganization"
    );
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _localEducationAgencyResource = new(
        "Ed-Fi",
        "LocalEducationAgency"
    );

    [Test]
    public async Task It_returns_empty_without_executing_sql_for_empty_claims()
    {
        var executor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateLookup(executor);

        var result = await sut.GetEducationOrganizations([], CreateMappingSet());

        result.Should().BeEmpty();
        executor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_empty_without_mapping_metadata_for_empty_claims()
    {
        var executor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateLookup(executor);

        var result = await sut.GetEducationOrganizations([]);

        result.Should().BeEmpty();
        executor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_requires_mapping_metadata_for_non_empty_claims()
    {
        var executor = new InMemoryRelationalCommandExecutor([]);
        var sut = CreateLookup(executor);

        var act = async () => await sut.GetEducationOrganizations([new EducationOrganizationId(255901L)]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*requires a MappingSet*");
    }

    [Test]
    public async Task It_executes_the_relational_hierarchy_query_with_sql_server_scalar_parameters()
    {
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("EducationOrganizationId", 255901L),
                        ("NameOfInstitution", "Grand Bend High School"),
                        ("Discriminator", "Ed-Fi:School"),
                        ("AncestorDiscriminator", "Ed-Fi:LocalEducationAgency"),
                        ("AncestorEducationOrganizationId", 255900L)
                    ),
                    RelationalAccessTestData.CreateRow(
                        ("EducationOrganizationId", 255901L),
                        ("NameOfInstitution", "Grand Bend High School"),
                        ("Discriminator", "Ed-Fi:School"),
                        ("AncestorDiscriminator", "Ed-Fi:School"),
                        ("AncestorEducationOrganizationId", 255901L)
                    )
                ),
            ]),
        ]);
        var sut = CreateLookup(executor);

        var result = await sut.GetEducationOrganizations(
            [
                new EducationOrganizationId(255901L),
                new EducationOrganizationId(255900L),
                new EducationOrganizationId(255901L),
            ],
            CreateMappingSet()
        );

        result
            .Should()
            .Equal(
                new TokenInfoEducationOrganization(
                    255901L,
                    "Grand Bend High School",
                    "Ed-Fi:School",
                    "Ed-Fi:LocalEducationAgency",
                    255900L
                ),
                new TokenInfoEducationOrganization(
                    255901L,
                    "Grand Bend High School",
                    "Ed-Fi:School",
                    "Ed-Fi:School",
                    255901L
                )
            );

        executor.Commands.Should().ContainSingle();
        var command = executor.Commands[0];
        command
            .CommandText.Should()
            .Contain("FROM [auth].[EducationOrganizationIdToEducationOrganizationId] h");
        command
            .CommandText.Should()
            .Contain(
                "WHERE c.[EducationOrganizationId] IN (@ClaimEducationOrganizationIds_0, @ClaimEducationOrganizationIds_1)"
            );
        command
            .CommandText.Should()
            .Contain("a.[EducationOrganizationId] AS [SourceEducationOrganizationId]");
        command.CommandText.Should().Contain("ORDER BY");
        command.CommandText.Should().NotContain("EducationOrganizationHierarchy");

        command.Parameters.Should().HaveCount(2);
        command.Parameters[0].Name.Should().Be("@ClaimEducationOrganizationIds_0");
        command.Parameters[0].Value.Should().Be(255900L);
        command.Parameters[0].ConfigureParameter.Should().BeNull();
        command.Parameters[1].Name.Should().Be("@ClaimEducationOrganizationIds_1");
        command.Parameters[1].Value.Should().Be(255901L);
        command.Parameters[1].ConfigureParameter.Should().BeNull();
    }

    [Test]
    public async Task It_executes_the_relational_hierarchy_query_with_a_sql_server_structured_parameter()
    {
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("EducationOrganizationId", 2000L),
                        ("NameOfInstitution", "Structured Parameter School"),
                        ("Discriminator", "Ed-Fi:School"),
                        ("AncestorDiscriminator", "Ed-Fi:School"),
                        ("AncestorEducationOrganizationId", 2000L)
                    )
                ),
            ]),
        ]);
        var sut = CreateLookup(executor);

        var result = await sut.GetEducationOrganizations(
            [
                .. Enumerable
                    .Range(1, 2000)
                    .Reverse()
                    .Select(static value => new EducationOrganizationId(value)),
            ],
            CreateMappingSet()
        );

        result
            .Should()
            .Equal(
                new TokenInfoEducationOrganization(
                    2000L,
                    "Structured Parameter School",
                    "Ed-Fi:School",
                    "Ed-Fi:School",
                    2000L
                )
            );

        executor.Commands.Should().ContainSingle();
        var command = executor.Commands[0];
        command
            .CommandText.Should()
            .Contain(
                "WHERE c.[EducationOrganizationId] IN (SELECT [Id] FROM @ClaimEducationOrganizationIds)"
            );
        command.Parameters.Should().ContainSingle();
        command.Parameters[0].Name.Should().Be("@ClaimEducationOrganizationIds");
        command.Parameters[0].Value.Should().BeOfType<DataTable>();

        var claimEducationOrganizationIdsTable = (DataTable)command.Parameters[0].Value!;
        claimEducationOrganizationIdsTable.Columns.Should().ContainSingle();
        claimEducationOrganizationIdsTable.Columns[0].ColumnName.Should().Be("Id");
        claimEducationOrganizationIdsTable.Columns[0].DataType.Should().Be(typeof(long));
        claimEducationOrganizationIdsTable.Rows.Should().HaveCount(2000);
        claimEducationOrganizationIdsTable.Rows[0].ItemArray.Should().Equal(1L);
        claimEducationOrganizationIdsTable.Rows[^1].ItemArray.Should().Equal(2000L);

        var sqlParameter = new SqlParameter();
        command.Parameters[0].ConfigureParameter.Should().NotBeNull();
        command.Parameters[0].ConfigureParameter!(sqlParameter);
        sqlParameter.SqlDbType.Should().Be(SqlDbType.Structured);
        sqlParameter.TypeName.Should().Be("dms.BigIntTable");
    }

    private static MssqlTokenInfoEducationOrganizationLookup CreateLookup(
        InMemoryRelationalCommandExecutor executor
    ) => new(executor, new MssqlRelationalParameterConfigurator());

    private static MappingSet CreateMappingSet()
    {
        ResourceKeyEntry[] resourceKeys =
        [
            new(1, _educationOrganizationResource, "5.2.0", true),
            new(2, _schoolResource, "5.2.0", false),
            new(3, _localEducationAgencyResource, "5.2.0", false),
        ];
        var resourceKeysByResource = resourceKeys.ToDictionary(
            static resourceKey => resourceKey.Resource,
            static resourceKey => resourceKey
        );
        var effectiveSchema = new EffectiveSchemaInfo(
            "5.2.0",
            "v1",
            "hash",
            (short)resourceKeys.Length,
            [1, 2, 3],
            [new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.2.0", false, "edfi-hash")],
            resourceKeys
        );
        var modelSet = new DerivedRelationalModelSet(
            effectiveSchema,
            SqlDialect.Mssql,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "5.2.0", false, _edfiSchema)],
            [
                CreateEdOrgConcreteResource(
                    resourceKeysByResource[_schoolResource],
                    "School",
                    "SchoolId",
                    "$.schoolId"
                ),
                CreateEdOrgConcreteResource(
                    resourceKeysByResource[_localEducationAgencyResource],
                    "LocalEducationAgency",
                    "LocalEducationAgencyId",
                    "$.localEducationAgencyId"
                ),
            ],
            [],
            [CreateEducationOrganizationUnionView(resourceKeysByResource)],
            [],
            []
        );

        return new MappingSet(
            new MappingSetKey("hash", SqlDialect.Mssql, "v1"),
            modelSet,
            new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            resourceKeys.ToDictionary(
                static resourceKey => resourceKey.Resource,
                static resourceKey => resourceKey.ResourceKeyId
            ),
            resourceKeys.ToDictionary(
                static resourceKey => resourceKey.ResourceKeyId,
                static resourceKey => resourceKey
            ),
            new Dictionary<QualifiedResourceName, IReadOnlyList<ResolvedSecurableElementPath>>()
        );
    }

    private static ConcreteResourceModel CreateEdOrgConcreteResource(
        ResourceKeyEntry resourceKey,
        string tableName,
        string identityColumn,
        string identityPath
    )
    {
        var rootTable = CreateRootTable(
            tableName,
            [
                ScalarColumn(identityColumn, identityPath, ScalarKind.Int64),
                ScalarColumn("NameOfInstitution", "$.nameOfInstitution"),
            ]
        );
        var model = new RelationalResourceModel(
            resourceKey.Resource,
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            [],
            []
        );

        return new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, model);
    }

    private static AbstractUnionViewInfo CreateEducationOrganizationUnionView(
        IReadOnlyDictionary<QualifiedResourceName, ResourceKeyEntry> resourceKeysByResource
    )
    {
        AbstractUnionViewOutputColumn[] outputColumns =
        [
            new(new DbColumnName("DocumentId"), new RelationalScalarType(ScalarKind.Int64), null, null),
            new(
                new DbColumnName("EducationOrganizationId"),
                new RelationalScalarType(ScalarKind.Int64),
                new JsonPathExpression("$.educationOrganizationId", []),
                null
            ),
            new(
                new DbColumnName("Discriminator"),
                new RelationalScalarType(ScalarKind.String, 256),
                null,
                null
            ),
        ];

        return new AbstractUnionViewInfo(
            resourceKeysByResource[_educationOrganizationResource],
            new DbTableName(_edfiSchema, "EducationOrganization_View"),
            outputColumns,
            [
                CreateUnionArm(resourceKeysByResource[_schoolResource], "School", "SchoolId", "Ed-Fi:School"),
                CreateUnionArm(
                    resourceKeysByResource[_localEducationAgencyResource],
                    "LocalEducationAgency",
                    "LocalEducationAgencyId",
                    "Ed-Fi:LocalEducationAgency"
                ),
            ]
        );
    }

    private static AbstractUnionViewArm CreateUnionArm(
        ResourceKeyEntry concreteResourceKey,
        string tableName,
        string identityColumn,
        string discriminator
    ) =>
        new(
            concreteResourceKey,
            new DbTableName(_edfiSchema, tableName),
            [
                new AbstractUnionViewProjectionExpression.SourceColumn(new DbColumnName("DocumentId")),
                new AbstractUnionViewProjectionExpression.SourceColumn(new DbColumnName(identityColumn)),
                new AbstractUnionViewProjectionExpression.StringLiteral(discriminator),
            ]
        );

    private static DbTableModel CreateRootTable(string tableName, IReadOnlyList<DbColumnModel> extraColumns)
    {
        return new DbTableModel(
            new DbTableName(_edfiSchema, tableName),
            new JsonPathExpression("$", []),
            new TableKey(
                $"PK_{tableName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null
                ),
                .. extraColumns,
            ],
            []
        );
    }

    private static DbColumnModel ScalarColumn(
        string columnName,
        string sourcePath,
        ScalarKind scalarKind = ScalarKind.String
    )
    {
        return new DbColumnModel(
            new DbColumnName(columnName),
            ColumnKind.Scalar,
            new RelationalScalarType(scalarKind),
            false,
            new JsonPathExpression(sourcePath, []),
            null
        );
    }
}
