// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalEdOrgAuthorizationSubjectSelector
{
    private static readonly DbSchemaName _edfiSchema = new("edfi");
    private static readonly DbColumnName _documentId = new("DocumentId");
    private static readonly string[] _strategyNames = ["RelationshipsWithEdOrgsOnly"];

    [Test]
    public void It_should_select_the_root_subject_for_a_derived_resource_fixture()
    {
        (_, var mappingSet) = Ds52FixtureHelper.BuildAndCompile();

        var result = RelationalEdOrgAuthorizationSubjectSelector.Select(
            mappingSet,
            new QualifiedResourceName("Ed-Fi", "CourseTranscript"),
            _strategyNames
        );

        result.Outcome.Should().Be(RelationalEdOrgAuthorizationSubjectSelectionOutcome.Success);
        result.Subjects.Should().ContainSingle();
        result.Subjects[0].ReadableName.Should().Be("EducationOrganizationId");
        result.Subjects[0].JsonPath.Should().Be("$.studentAcademicRecordReference.educationOrganizationId");
        result.Subjects[0].Table.Should().Be(new DbTableName(_edfiSchema, "CourseTranscript"));
        result
            .Subjects[0]
            .Column.Should()
            .Be(new DbColumnName("StudentAcademicRecord_EducationOrganizationId"));
    }

    [Test]
    public void It_should_prefer_the_root_candidate_when_root_and_child_paths_share_a_metaed_name()
    {
        var rootTable = CreateRootTable(
            Table("TestResource"),
            [
                new DbColumnModel(
                    Col("SchoolReference_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    Col("SchoolReference_SchoolId"),
                    ColumnKind.Scalar,
                    null,
                    false,
                    Path("$.schoolReference.schoolId"),
                    null
                ),
            ]
        );

        var childTableName = Table("TestResourceClassPeriod");
        var childTable = CreateChildTable(
            childTableName,
            "$.classPeriods[*]",
            [
                new DbColumnModel(Col("CollectionItemId"), ColumnKind.Scalar, null, false, null, null),
                new DbColumnModel(
                    Col("ClassPeriod_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "ClassPeriod")
                ),
                new DbColumnModel(Col("ClassPeriod_SchoolId"), ColumnKind.Scalar, null, false, null, null),
            ]
        );

        var model = CreateModelWithTables(
            "TestResource",
            rootTable,
            [childTable],
            [
                new DocumentReferenceBinding(
                    true,
                    Path("$.schoolReference"),
                    rootTable.Table,
                    Col("SchoolReference_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "School"),
                    [
                        new ReferenceIdentityBinding(
                            Path("$.schoolReference.schoolId"),
                            Path("$.schoolReference.schoolId"),
                            Col("SchoolReference_SchoolId")
                        ),
                    ]
                ),
                new DocumentReferenceBinding(
                    true,
                    Path("$.classPeriods[*].classPeriodReference"),
                    childTableName,
                    Col("ClassPeriod_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "ClassPeriod"),
                    [
                        new ReferenceIdentityBinding(
                            Path("$.schoolReference.schoolId"),
                            Path("$.classPeriods[*].classPeriodReference.schoolId"),
                            Col("ClassPeriod_SchoolId")
                        ),
                    ]
                ),
            ]
        );

        var mappingSet = CreateMappingSet(
            CreateConcrete(
                "TestResource",
                model,
                new ResourceSecurableElements(
                    [
                        new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId"),
                        new EdOrgSecurableElement(
                            "$.classPeriods[*].classPeriodReference.schoolId",
                            "SchoolId"
                        ),
                    ],
                    [],
                    [],
                    [],
                    []
                )
            )
        );

        var result = RelationalEdOrgAuthorizationSubjectSelector.Select(
            mappingSet,
            new QualifiedResourceName("Ed-Fi", "TestResource"),
            _strategyNames
        );

        result.Outcome.Should().Be(RelationalEdOrgAuthorizationSubjectSelectionOutcome.Success);
        result.Subjects.Should().ContainSingle();
        result.Subjects[0].ReadableName.Should().Be("SchoolId");
        result.Subjects[0].Table.Should().Be(rootTable.Table);
        result.Subjects[0].Column.Should().Be(Col("SchoolReference_SchoolId"));
    }

    [Test]
    public void It_should_retain_multiple_root_subjects_and_ignore_child_only_candidates()
    {
        var rootTable = CreateRootTable(
            Table("TestResource"),
            [
                new DbColumnModel(
                    Col("SchoolReference_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    Col("SchoolReference_SchoolId"),
                    ColumnKind.Scalar,
                    null,
                    false,
                    Path("$.schoolReference.schoolId"),
                    null
                ),
                new DbColumnModel(
                    Col("LocalEducationAgencyReference_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "LocalEducationAgency")
                ),
                new DbColumnModel(
                    Col("LocalEducationAgencyReference_LocalEducationAgencyId"),
                    ColumnKind.Scalar,
                    null,
                    false,
                    Path("$.localEducationAgencyReference.localEducationAgencyId"),
                    null
                ),
            ]
        );

        var childTableName = Table("TestResourceProgram");
        var childTable = CreateChildTable(
            childTableName,
            "$.programs[*]",
            [
                new DbColumnModel(Col("CollectionItemId"), ColumnKind.Scalar, null, false, null, null),
                new DbColumnModel(
                    Col("Program_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "Program")
                ),
                new DbColumnModel(
                    Col("Program_EducationOrganizationId"),
                    ColumnKind.Scalar,
                    null,
                    false,
                    null,
                    null
                ),
            ]
        );

        var model = CreateModelWithTables(
            "TestResource",
            rootTable,
            [childTable],
            [
                new DocumentReferenceBinding(
                    true,
                    Path("$.schoolReference"),
                    rootTable.Table,
                    Col("SchoolReference_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "School"),
                    [
                        new ReferenceIdentityBinding(
                            Path("$.schoolReference.schoolId"),
                            Path("$.schoolReference.schoolId"),
                            Col("SchoolReference_SchoolId")
                        ),
                    ]
                ),
                new DocumentReferenceBinding(
                    true,
                    Path("$.localEducationAgencyReference"),
                    rootTable.Table,
                    Col("LocalEducationAgencyReference_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "LocalEducationAgency"),
                    [
                        new ReferenceIdentityBinding(
                            Path("$.localEducationAgencyReference.localEducationAgencyId"),
                            Path("$.localEducationAgencyReference.localEducationAgencyId"),
                            Col("LocalEducationAgencyReference_LocalEducationAgencyId")
                        ),
                    ]
                ),
                new DocumentReferenceBinding(
                    true,
                    Path("$.programs[*].programReference"),
                    childTableName,
                    Col("Program_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "Program"),
                    [
                        new ReferenceIdentityBinding(
                            Path("$.educationOrganizationReference.educationOrganizationId"),
                            Path("$.programs[*].programReference.educationOrganizationId"),
                            Col("Program_EducationOrganizationId")
                        ),
                    ]
                ),
            ]
        );

        var mappingSet = CreateMappingSet(
            CreateConcrete(
                "TestResource",
                model,
                new ResourceSecurableElements(
                    [
                        new EdOrgSecurableElement("$.schoolReference.schoolId", "SchoolId"),
                        new EdOrgSecurableElement(
                            "$.localEducationAgencyReference.localEducationAgencyId",
                            "LocalEducationAgencyId"
                        ),
                        new EdOrgSecurableElement(
                            "$.programs[*].programReference.educationOrganizationId",
                            "ProgramEducationOrganizationId"
                        ),
                    ],
                    [],
                    [],
                    [],
                    []
                )
            )
        );

        var result = RelationalEdOrgAuthorizationSubjectSelector.Select(
            mappingSet,
            new QualifiedResourceName("Ed-Fi", "TestResource"),
            _strategyNames
        );

        result.Outcome.Should().Be(RelationalEdOrgAuthorizationSubjectSelectionOutcome.Success);
        result
            .Subjects.Select(static subject => subject.ReadableName)
            .Should()
            .Equal("SchoolId", "LocalEducationAgencyId");
    }

    [Test]
    public void It_should_fail_when_only_child_table_candidates_exist()
    {
        var rootTable = CreateRootTable(Table("TestResource"));
        var childTableName = Table("TestResourceClassPeriod");
        var childTable = CreateChildTable(
            childTableName,
            "$.classPeriods[*]",
            [
                new DbColumnModel(Col("CollectionItemId"), ColumnKind.Scalar, null, false, null, null),
                new DbColumnModel(
                    Col("ClassPeriod_DocumentId"),
                    ColumnKind.DocumentFk,
                    null,
                    false,
                    null,
                    new QualifiedResourceName("Ed-Fi", "ClassPeriod")
                ),
                new DbColumnModel(Col("ClassPeriod_SchoolId"), ColumnKind.Scalar, null, false, null, null),
            ]
        );

        var model = CreateModelWithTables(
            "TestResource",
            rootTable,
            [childTable],
            [
                new DocumentReferenceBinding(
                    true,
                    Path("$.classPeriods[*].classPeriodReference"),
                    childTableName,
                    Col("ClassPeriod_DocumentId"),
                    new QualifiedResourceName("Ed-Fi", "ClassPeriod"),
                    [
                        new ReferenceIdentityBinding(
                            Path("$.schoolReference.schoolId"),
                            Path("$.classPeriods[*].classPeriodReference.schoolId"),
                            Col("ClassPeriod_SchoolId")
                        ),
                    ]
                ),
            ]
        );

        var mappingSet = CreateMappingSet(
            CreateConcrete(
                "TestResource",
                model,
                new ResourceSecurableElements(
                    [
                        new EdOrgSecurableElement(
                            "$.classPeriods[*].classPeriodReference.schoolId",
                            "SchoolId"
                        ),
                    ],
                    [],
                    [],
                    [],
                    []
                )
            )
        );

        var result = RelationalEdOrgAuthorizationSubjectSelector.Select(
            mappingSet,
            new QualifiedResourceName("Ed-Fi", "TestResource"),
            _strategyNames
        );

        result
            .Outcome.Should()
            .Be(RelationalEdOrgAuthorizationSubjectSelectionOutcome.SecurityConfigurationError);
        result.FailureMessage.Should().Contain("RelationshipsWithEdOrgsOnly");
        result.FailureMessage.Should().Contain("$.classPeriods[*].classPeriodReference.schoolId");
        result.FailureMessage.Should().Contain("TestResourceClassPeriod");
    }

    private static DbTableName Table(string name) => new(_edfiSchema, name);

    private static DbColumnName Col(string name) => new(name);

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    private static ResourceKeyEntry ResourceKey(short id, string resource) =>
        new(id, new QualifiedResourceName("Ed-Fi", resource), "1.0", false);

    private static DbTableModel CreateRootTable(
        DbTableName table,
        IReadOnlyList<DbColumnModel>? columns = null
    ) =>
        new(
            table,
            Path("$"),
            new TableKey("PK_Test", [new DbKeyColumn(_documentId, ColumnKind.ParentKeyPart)]),
            columns ?? [],
            []
        );

    private static DbTableModel CreateChildTable(
        DbTableName table,
        string jsonScope,
        IReadOnlyList<DbColumnModel> columns
    ) =>
        new(
            table,
            Path(jsonScope),
            new TableKey("PK_TestChild", [new DbKeyColumn(Col("CollectionItemId"), ColumnKind.Scalar)]),
            columns,
            []
        );

    private static RelationalResourceModel CreateModelWithTables(
        string resource,
        DbTableModel root,
        IReadOnlyList<DbTableModel> childTables,
        IReadOnlyList<DocumentReferenceBinding> bindings
    ) =>
        new(
            new QualifiedResourceName("Ed-Fi", resource),
            _edfiSchema,
            ResourceStorageKind.RelationalTables,
            root,
            [root, .. childTables],
            bindings,
            []
        );

    private static ConcreteResourceModel CreateConcrete(
        string resource,
        RelationalResourceModel model,
        ResourceSecurableElements securableElements
    ) =>
        new(ResourceKey(1, resource), ResourceStorageKind.RelationalTables, model)
        {
            SecurableElements = securableElements,
        };

    private static MappingSet CreateMappingSet(ConcreteResourceModel concreteResourceModel)
    {
        var resourceKey = concreteResourceModel.ResourceKey;

        return new MappingSet(
            Key: new MappingSetKey("schema-hash", SqlDialect.Pgsql, "v1"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: new EffectiveSchemaInfo(
                    ApiSchemaFormatVersion: "1.0",
                    RelationalMappingVersion: "v1",
                    EffectiveSchemaHash: "schema-hash",
                    ResourceKeyCount: 1,
                    ResourceKeySeedHash: [1, 2, 3],
                    SchemaComponentsInEndpointOrder:
                    [
                        new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                    ],
                    ResourceKeysInIdOrder: [resourceKey]
                ),
                Dialect: SqlDialect.Pgsql,
                ProjectSchemasInEndpointOrder:
                [
                    new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, _edfiSchema),
                ],
                ConcreteResourcesInNameOrder: [concreteResourceModel],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resourceKey.Resource] = resourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKey.ResourceKeyId] = resourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }
}
