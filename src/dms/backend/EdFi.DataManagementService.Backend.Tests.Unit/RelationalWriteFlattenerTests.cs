// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_RelationalWriteFlattener
{
    private RelationalWriteFlattener _sut = null!;
    private FlattenerFixture _fixture = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new RelationalWriteFlattener();
        _fixture = FlattenerFixture.Create();
    }

    [Test]
    public void It_emits_root_and_root_extension_rows_in_compiled_binding_order()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "schoolYear": 2026,
                  "details": {
                    "code": "ABC",
                    "active": true,
                    "staffCount": 5000000000,
                    "startDate": "2026-08-19",
                    "lastModified": "2026-08-19T16:30:45Z",
                    "meetingTime": "14:05:07"
                  },
                  "schoolReference": {
                    "schoolId": 255901
                  },
                  "programTypeDescriptor": "uri://ed-fi.org/programtypedescriptor#stem",
                  "_ext": {
                    "sample": {
                      "favoriteColor": "Green"
                    }
                  }
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.ExistingDocument(345L, _fixture.DocumentUuid)
        );

        var result = _sut.Flatten(flatteningInput);

        result
            .RootRow.Values.Should()
            .Equal(
                new FlattenedWriteValue.Literal(345L),
                new FlattenedWriteValue.Literal(2026),
                new FlattenedWriteValue.Literal("ABC"),
                new FlattenedWriteValue.Literal(true),
                new FlattenedWriteValue.Literal(5000000000L),
                new FlattenedWriteValue.Literal(new DateOnly(2026, 8, 19)),
                new FlattenedWriteValue.Literal(new DateTime(2026, 8, 19, 16, 30, 45, DateTimeKind.Utc)),
                new FlattenedWriteValue.Literal(new TimeOnly(14, 5, 7)),
                new FlattenedWriteValue.Literal(901L),
                new FlattenedWriteValue.Literal(77L)
            );

        result.RootRow.NonCollectionRows.Should().ContainSingle();
        result
            .RootRow.NonCollectionRows[0]
            .Values.Should()
            .Equal(new FlattenedWriteValue.Literal(345L), new FlattenedWriteValue.Literal("Green"));
    }

    [Test]
    public void It_treats_the_selected_body_as_authoritative_input()
    {
        var originalBody = JsonNode.Parse(
            """
            {
              "schoolYear": 2026,
              "details": {
                "code": "ORIGINAL"
              },
              "_ext": {
                "sample": {
                  "favoriteColor": "Blue"
                }
              }
            }
            """
        )!;

        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "schoolYear": 2030,
                  "details": {}
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.CreateNew(_fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var result = _sut.Flatten(flatteningInput);

        originalBody["schoolYear"]!.GetValue<int>().Should().Be(2026);
        result.RootRow.Values[0].Should().BeSameAs(FlattenedWriteValue.UnresolvedRootDocumentId.Instance);
        result.RootRow.Values[1].Should().Be(new FlattenedWriteValue.Literal(2030));
        result.RootRow.Values[2].Should().Be(new FlattenedWriteValue.Literal(null));
        result.RootRow.Values[8].Should().Be(new FlattenedWriteValue.Literal(null));
        result.RootRow.Values[9].Should().Be(new FlattenedWriteValue.Literal(null));
        result.RootRow.NonCollectionRows.Should().BeEmpty();
    }

    [Test]
    public void It_rejects_scalar_values_that_do_not_match_the_compiled_type()
    {
        var flatteningInput = _fixture.CreateFlatteningInput(
            selectedBody: JsonNode.Parse(
                """
                {
                  "schoolYear": "2026"
                }
                """
            )!,
            targetContext: new RelationalWriteTargetContext.CreateNew(_fixture.DocumentUuid),
            resolvedReferences: FlattenerFixture.CreateEmptyResolvedReferences()
        );

        var act = () => _sut.Flatten(flatteningInput);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*Column 'SchoolYear' on table 'edfi.Student' expected scalar kind 'Int32' at path '$.schoolYear'*"
            );
    }

    private sealed record FlattenerFixture(
        DocumentUuid DocumentUuid,
        ResourceWritePlan WritePlan,
        ResolvedReferenceSet ResolvedReferences
    )
    {
        public FlatteningInput CreateFlatteningInput(
            JsonNode selectedBody,
            RelationalWriteTargetContext targetContext,
            ResolvedReferenceSet? resolvedReferences = null
        )
        {
            return new FlatteningInput(
                RelationalWriteOperationKind.Post,
                targetContext,
                WritePlan,
                selectedBody,
                resolvedReferences ?? ResolvedReferences
            );
        }

        public static FlattenerFixture Create()
        {
            var schoolResource = new QualifiedResourceName("Ed-Fi", "Student");
            var rootPlan = CreateRootPlan();
            var rootExtensionPlan = CreateRootExtensionPlan();

            var resourceModel = new RelationalResourceModel(
                Resource: schoolResource,
                PhysicalSchema: new DbSchemaName("edfi"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder: [rootPlan.TableModel, rootExtensionPlan.TableModel],
                DocumentReferenceBindings:
                [
                    new DocumentReferenceBinding(
                        IsIdentityComponent: false,
                        ReferenceObjectPath: CreatePath(
                            "$.schoolReference",
                            new JsonPathSegment.Property("schoolReference")
                        ),
                        Table: rootPlan.TableModel.Table,
                        FkColumn: new DbColumnName("School_DocumentId"),
                        TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
                        IdentityBindings: []
                    ),
                ],
                DescriptorEdgeSources: []
            );

            return new FlattenerFixture(
                DocumentUuid: new DocumentUuid(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")),
                WritePlan: new ResourceWritePlan(resourceModel, [rootPlan, rootExtensionPlan]),
                ResolvedReferences: CreateResolvedReferences()
            );
        }

        private static ResolvedReferenceSet CreateResolvedReferences()
        {
            var schoolReference = new DocumentReference(
                new BaseResourceInfo(
                    new ProjectName("Ed-Fi"),
                    new ResourceName("School"),
                    IsDescriptor: false
                ),
                new DocumentIdentity([new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901")]),
                new ReferentialId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                new JsonPath("$.schoolReference")
            );
            var programDescriptorReference = new DescriptorReference(
                new BaseResourceInfo(
                    new ProjectName("Ed-Fi"),
                    new ResourceName("ProgramTypeDescriptor"),
                    IsDescriptor: true
                ),
                new DocumentIdentity([
                    new DocumentIdentityElement(
                        DocumentIdentity.DescriptorIdentityJsonPath,
                        "uri://ed-fi.org/programtypedescriptor#stem"
                    ),
                ]),
                new ReferentialId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                new JsonPath("$.programTypeDescriptor")
            );

            return new ResolvedReferenceSet(
                SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>
                {
                    [new JsonPath("$.schoolReference")] = new ResolvedDocumentReference(
                        schoolReference,
                        DocumentId: 901L,
                        ResourceKeyId: 21
                    ),
                },
                SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>
                {
                    [new JsonPath("$.programTypeDescriptor")] = new ResolvedDescriptorReference(
                        programDescriptorReference,
                        DocumentId: 77L,
                        ResourceKeyId: 31
                    ),
                },
                LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
                InvalidDocumentReferences: [],
                InvalidDescriptorReferences: [],
                DocumentReferenceOccurrences: [],
                DescriptorReferenceOccurrences: []
            );
        }

        public static ResolvedReferenceSet CreateEmptyResolvedReferences()
        {
            return new ResolvedReferenceSet(
                SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
                SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
                LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
                InvalidDocumentReferences: [],
                InvalidDescriptorReferences: [],
                DocumentReferenceOccurrences: [],
                DescriptorReferenceOccurrences: []
            );
        }

        private static TableWritePlan CreateRootPlan()
        {
            var tableModel = new DbTableModel(
                Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
                JsonScope: CreatePath("$"),
                Key: new TableKey(
                    "PK_Student",
                    [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
                ),
                Columns:
                [
                    CreateColumn("DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false),
                    CreateColumn(
                        "SchoolYear",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.Int32),
                        isNullable: true,
                        sourceJsonPath: CreatePath("$.schoolYear", new JsonPathSegment.Property("schoolYear"))
                    ),
                    CreateColumn(
                        "Code",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 20),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$.details.code",
                            new JsonPathSegment.Property("details"),
                            new JsonPathSegment.Property("code")
                        )
                    ),
                    CreateColumn(
                        "IsActive",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.Boolean),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$.details.active",
                            new JsonPathSegment.Property("details"),
                            new JsonPathSegment.Property("active")
                        )
                    ),
                    CreateColumn(
                        "StaffCount",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.Int64),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$.details.staffCount",
                            new JsonPathSegment.Property("details"),
                            new JsonPathSegment.Property("staffCount")
                        )
                    ),
                    CreateColumn(
                        "StartDate",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.Date),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$.details.startDate",
                            new JsonPathSegment.Property("details"),
                            new JsonPathSegment.Property("startDate")
                        )
                    ),
                    CreateColumn(
                        "LastModified",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.DateTime),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$.details.lastModified",
                            new JsonPathSegment.Property("details"),
                            new JsonPathSegment.Property("lastModified")
                        )
                    ),
                    CreateColumn(
                        "MeetingTime",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.Time),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$.details.meetingTime",
                            new JsonPathSegment.Property("details"),
                            new JsonPathSegment.Property("meetingTime")
                        )
                    ),
                    CreateColumn(
                        "School_DocumentId",
                        ColumnKind.DocumentFk,
                        null,
                        isNullable: true,
                        targetResource: new QualifiedResourceName("Ed-Fi", "School")
                    ),
                    CreateColumn(
                        "ProgramTypeDescriptorId",
                        ColumnKind.DescriptorFk,
                        null,
                        isNullable: true,
                        targetResource: new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
                    ),
                ],
                Constraints: []
            )
            {
                IdentityMetadata = new DbTableIdentityMetadata(
                    TableKind: DbTableKind.Root,
                    PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                    RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                    ImmediateParentScopeLocatorColumns: [],
                    SemanticIdentityBindings: []
                ),
            };

            return new TableWritePlan(
                TableModel: tableModel,
                InsertSql: "insert into edfi.\"Student\" values (...)",
                UpdateSql: null,
                DeleteByParentSql: null,
                BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
                ColumnBindings:
                [
                    new WriteColumnBinding(
                        tableModel.Columns[0],
                        new WriteValueSource.DocumentId(),
                        "DocumentId"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[1],
                        new WriteValueSource.Scalar(
                            CreatePath("$.schoolYear", new JsonPathSegment.Property("schoolYear")),
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                        "SchoolYear"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[2],
                        new WriteValueSource.Scalar(
                            CreatePath(
                                "$.details.code",
                                new JsonPathSegment.Property("details"),
                                new JsonPathSegment.Property("code")
                            ),
                            new RelationalScalarType(ScalarKind.String, MaxLength: 20)
                        ),
                        "Code"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[3],
                        new WriteValueSource.Scalar(
                            CreatePath(
                                "$.details.active",
                                new JsonPathSegment.Property("details"),
                                new JsonPathSegment.Property("active")
                            ),
                            new RelationalScalarType(ScalarKind.Boolean)
                        ),
                        "IsActive"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[4],
                        new WriteValueSource.Scalar(
                            CreatePath(
                                "$.details.staffCount",
                                new JsonPathSegment.Property("details"),
                                new JsonPathSegment.Property("staffCount")
                            ),
                            new RelationalScalarType(ScalarKind.Int64)
                        ),
                        "StaffCount"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[5],
                        new WriteValueSource.Scalar(
                            CreatePath(
                                "$.details.startDate",
                                new JsonPathSegment.Property("details"),
                                new JsonPathSegment.Property("startDate")
                            ),
                            new RelationalScalarType(ScalarKind.Date)
                        ),
                        "StartDate"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[6],
                        new WriteValueSource.Scalar(
                            CreatePath(
                                "$.details.lastModified",
                                new JsonPathSegment.Property("details"),
                                new JsonPathSegment.Property("lastModified")
                            ),
                            new RelationalScalarType(ScalarKind.DateTime)
                        ),
                        "LastModified"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[7],
                        new WriteValueSource.Scalar(
                            CreatePath(
                                "$.details.meetingTime",
                                new JsonPathSegment.Property("details"),
                                new JsonPathSegment.Property("meetingTime")
                            ),
                            new RelationalScalarType(ScalarKind.Time)
                        ),
                        "MeetingTime"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[8],
                        new WriteValueSource.DocumentReference(0),
                        "School_DocumentId"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[9],
                        new WriteValueSource.DescriptorReference(
                            new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"),
                            CreatePath(
                                "$.programTypeDescriptor",
                                new JsonPathSegment.Property("programTypeDescriptor")
                            ),
                            CreatePath(
                                "$.programTypeDescriptor",
                                new JsonPathSegment.Property("programTypeDescriptor")
                            )
                        ),
                        "ProgramTypeDescriptorId"
                    ),
                ],
                KeyUnificationPlans: []
            );
        }

        private static TableWritePlan CreateRootExtensionPlan()
        {
            var tableModel = new DbTableModel(
                Table: new DbTableName(new DbSchemaName("sample"), "StudentExtension"),
                JsonScope: CreatePath(
                    "$._ext.sample",
                    new JsonPathSegment.Property("_ext"),
                    new JsonPathSegment.Property("sample")
                ),
                Key: new TableKey(
                    "PK_StudentExtension",
                    [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
                ),
                Columns:
                [
                    CreateColumn("DocumentId", ColumnKind.ParentKeyPart, null, isNullable: false),
                    CreateColumn(
                        "FavoriteColor",
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                        isNullable: true,
                        sourceJsonPath: CreatePath(
                            "$._ext.sample.favoriteColor",
                            new JsonPathSegment.Property("_ext"),
                            new JsonPathSegment.Property("sample"),
                            new JsonPathSegment.Property("favoriteColor")
                        )
                    ),
                ],
                Constraints: []
            )
            {
                IdentityMetadata = new DbTableIdentityMetadata(
                    TableKind: DbTableKind.RootExtension,
                    PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                    RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                    ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                    SemanticIdentityBindings: []
                ),
            };

            return new TableWritePlan(
                TableModel: tableModel,
                InsertSql: "insert into sample.\"StudentExtension\" values (...)",
                UpdateSql: "update sample.\"StudentExtension\" set ...",
                DeleteByParentSql: "delete from sample.\"StudentExtension\" where ...",
                BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
                ColumnBindings:
                [
                    new WriteColumnBinding(
                        tableModel.Columns[0],
                        new WriteValueSource.ParentKeyPart(0),
                        "DocumentId"
                    ),
                    new WriteColumnBinding(
                        tableModel.Columns[1],
                        new WriteValueSource.Scalar(
                            CreatePath("$.favoriteColor", new JsonPathSegment.Property("favoriteColor")),
                            new RelationalScalarType(ScalarKind.String, MaxLength: 30)
                        ),
                        "FavoriteColor"
                    ),
                ],
                KeyUnificationPlans: []
            );
        }

        private static DbColumnModel CreateColumn(
            string columnName,
            ColumnKind kind,
            RelationalScalarType? scalarType,
            bool isNullable,
            JsonPathExpression? sourceJsonPath = null,
            QualifiedResourceName? targetResource = null
        )
        {
            return new DbColumnModel(
                ColumnName: new DbColumnName(columnName),
                Kind: kind,
                ScalarType: scalarType,
                IsNullable: isNullable,
                SourceJsonPath: sourceJsonPath,
                TargetResource: targetResource,
                Storage: new ColumnStorage.Stored()
            );
        }

        private static JsonPathExpression CreatePath(string canonical, params JsonPathSegment[] segments)
        {
            return new JsonPathExpression(canonical, segments);
        }
    }
}
