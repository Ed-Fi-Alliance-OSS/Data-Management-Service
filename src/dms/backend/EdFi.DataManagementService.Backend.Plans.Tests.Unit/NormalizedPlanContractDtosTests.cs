// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_NormalizedPlanContractDtos
{
    private ResourceWritePlanDto _writePlan = null!;
    private ResourceReadPlanDto _readPlan = null!;
    private PageDocumentIdSqlPlanDto _queryPlan = null!;

    [SetUp]
    public void Setup()
    {
        var resource = new QualifiedResourceNameDto("Ed-Fi", "StudentSchoolAssociation");
        var table = new DbTableNameDto("edfi", "StudentSchoolAssociation");

        _writePlan = new ResourceWritePlanDto(
            resource,
            [
                new TableWritePlanDto(
                    Table: table,
                    InsertSql: "INSERT INTO [edfi].[StudentSchoolAssociation] ([DocumentId], [Ordinal], [SchoolYear])\r\nVALUES (@documentId, @ordinal, @schoolYear);",
                    UpdateSql: "UPDATE [edfi].[StudentSchoolAssociation]\r\nSET [SchoolYear] = @schoolYear\r\nWHERE [DocumentId] = @documentId;",
                    DeleteByParentSql: null,
                    BulkInsertBatching: new BulkInsertBatchingInfoDto(
                        MaxRowsPerBatch: 700,
                        ParametersPerRow: 7,
                        MaxParametersPerCommand: 2100
                    ),
                    ColumnBindings:
                    [
                        new WriteColumnBindingDto(
                            ColumnName: "DocumentId",
                            Source: new WriteValueSourceDto.DocumentId(),
                            ParameterName: "documentId"
                        ),
                        new WriteColumnBindingDto(
                            ColumnName: "Ordinal",
                            Source: new WriteValueSourceDto.Ordinal(),
                            ParameterName: "ordinal"
                        ),
                        new WriteColumnBindingDto(
                            ColumnName: "School_DocumentId",
                            Source: new WriteValueSourceDto.ParentKeyPart(Index: 0),
                            ParameterName: "schoolDocumentId"
                        ),
                        new WriteColumnBindingDto(
                            ColumnName: "SchoolYear",
                            Source: new WriteValueSourceDto.Scalar(
                                RelativePath: "$.schoolYear",
                                ScalarType: new RelationalScalarTypeDto(NormalizedScalarKind.Int32)
                            ),
                            ParameterName: "schoolYear"
                        ),
                        new WriteColumnBindingDto(
                            ColumnName: "Calendar_DocumentId",
                            Source: new WriteValueSourceDto.DocumentReference(BindingIndex: 2),
                            ParameterName: "calendarDocumentId"
                        ),
                        new WriteColumnBindingDto(
                            ColumnName: "GradeLevel_DescriptorId",
                            Source: new WriteValueSourceDto.DescriptorReference(
                                DescriptorResource: new QualifiedResourceNameDto(
                                    "Ed-Fi",
                                    "GradeLevelDescriptor"
                                ),
                                RelativePath: "$.gradeLevelDescriptor",
                                DescriptorValuePath: "$.gradeLevelDescriptor"
                            ),
                            ParameterName: "gradeLevelDescriptorId"
                        ),
                        new WriteColumnBindingDto(
                            ColumnName: "SchoolYear_Canonical",
                            Source: new WriteValueSourceDto.Precomputed(),
                            ParameterName: "schoolYearCanonical"
                        ),
                    ],
                    KeyUnificationPlans:
                    [
                        new KeyUnificationWritePlanDto(
                            CanonicalColumnName: "SchoolYear_Canonical",
                            CanonicalBindingIndex: 6,
                            MembersInOrder:
                            [
                                new KeyUnificationMemberWritePlanDto.ScalarMember(
                                    MemberPathColumnName: "SchoolYear",
                                    RelativePath: "$.schoolYear",
                                    ScalarType: new RelationalScalarTypeDto(NormalizedScalarKind.Int32),
                                    PresenceColumnName: null,
                                    PresenceBindingIndex: null,
                                    PresenceIsSynthetic: false
                                ),
                                new KeyUnificationMemberWritePlanDto.DescriptorMember(
                                    MemberPathColumnName: "SchoolYear_DescriptorAlias",
                                    RelativePath: "$.schoolYearDescriptor",
                                    DescriptorResource: new QualifiedResourceNameDto(
                                        "Ed-Fi",
                                        "SchoolYearTypeDescriptor"
                                    ),
                                    PresenceColumnName: "SchoolYear_Present",
                                    PresenceBindingIndex: 1,
                                    PresenceIsSynthetic: true
                                ),
                            ]
                        ),
                    ]
                ),
            ]
        );

        _readPlan = new ResourceReadPlanDto(
            Resource: resource,
            KeysetTable: new KeysetTableContractDto("page", "DocumentId"),
            TablePlansInDependencyOrder:
            [
                new TableReadPlanDto(
                    Table: table,
                    SelectByKeysetSql: "SELECT r.[DocumentId], r.[School_DocumentId]\r\nFROM [edfi].[StudentSchoolAssociation] r;"
                ),
            ],
            ReferenceIdentityProjectionPlansInDependencyOrder:
            [
                new ReferenceIdentityProjectionTablePlanDto(
                    Table: table,
                    BindingsInOrder:
                    [
                        new ReferenceIdentityProjectionBindingDto(
                            IsIdentityComponent: true,
                            ReferenceObjectPath: "$.schoolReference",
                            TargetResource: new QualifiedResourceNameDto("Ed-Fi", "School"),
                            FkColumnOrdinal: 2,
                            IdentityFieldOrdinalsInOrder:
                            [
                                new ReferenceIdentityProjectionFieldOrdinalDto(
                                    ReferenceJsonPath: "$.schoolReference.schoolId",
                                    ColumnOrdinal: 3
                                ),
                                new ReferenceIdentityProjectionFieldOrdinalDto(
                                    ReferenceJsonPath: "$.schoolReference.schoolYear",
                                    ColumnOrdinal: 4
                                ),
                            ]
                        ),
                    ]
                ),
            ],
            DescriptorProjectionPlansInOrder:
            [
                new DescriptorProjectionPlanDto(
                    SelectByKeysetSql: "SELECT r.[GradeLevel_DescriptorId], d.[Uri]\r\nFROM [edfi].[StudentSchoolAssociation] r;",
                    ResultShape: new DescriptorProjectionResultShapeDto(
                        DescriptorIdOrdinal: 0,
                        UriOrdinal: 1
                    ),
                    SourcesInOrder:
                    [
                        new DescriptorProjectionSourceDto(
                            DescriptorValuePath: "$.gradeLevelDescriptor",
                            Table: table,
                            DescriptorResource: new QualifiedResourceNameDto("Ed-Fi", "GradeLevelDescriptor"),
                            DescriptorIdColumnOrdinal: 2
                        ),
                    ]
                ),
            ]
        );

        _queryPlan = new PageDocumentIdSqlPlanDto(
            PageDocumentIdSql: "SELECT r.[DocumentId]\r\nFROM [edfi].[StudentSchoolAssociation] r\r\nORDER BY r.[DocumentId] ASC\r\nOFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY\r\n;",
            TotalCountSql: "SELECT COUNT(1)\r\nFROM [edfi].[StudentSchoolAssociation] r\r\nWHERE r.[SchoolYear] = @schoolYear\r\n;",
            PageParametersInOrder:
            [
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Offset, "offset"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Limit, "limit"),
            ],
            TotalCountParametersInOrder:
            [
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
            ]
        );
    }

    [Test]
    public void It_should_preserve_authoritative_ordering_for_order_sensitive_collections()
    {
        _writePlan
            .TablePlansInDependencyOrder[0]
            .ColumnBindings.Select(binding => binding.ParameterName)
            .Should()
            .Equal(
                "documentId",
                "ordinal",
                "schoolDocumentId",
                "schoolYear",
                "calendarDocumentId",
                "gradeLevelDescriptorId",
                "schoolYearCanonical"
            );

        _writePlan
            .TablePlansInDependencyOrder[0]
            .KeyUnificationPlans[0]
            .MembersInOrder.Select(member => member.MemberPathColumnName)
            .Should()
            .Equal("SchoolYear", "SchoolYear_DescriptorAlias");

        _readPlan
            .ReferenceIdentityProjectionPlansInDependencyOrder[0]
            .BindingsInOrder[0]
            .IdentityFieldOrdinalsInOrder.Select(field => field.ReferenceJsonPath)
            .Should()
            .Equal("$.schoolReference.schoolId", "$.schoolReference.schoolYear");

        _queryPlan
            .PageParametersInOrder.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("schoolYear", "offset", "limit");
        _queryPlan.TotalCountParametersInOrder.Should().NotBeNull();
        _queryPlan
            .TotalCountParametersInOrder!.Value.Select(parameter => parameter.ParameterName)
            .Should()
            .Equal("schoolYear");
    }

    [Test]
    public void It_should_emit_canonical_json_with_lf_newlines_and_stable_sha256_hashes()
    {
        var writeJsonA = NormalizedPlanDtoJson.EmitCanonicalJson(_writePlan);
        var writeJsonB = NormalizedPlanDtoJson.EmitCanonicalJson(_writePlan);
        var readJsonA = NormalizedPlanDtoJson.EmitCanonicalJson(_readPlan);
        var readJsonB = NormalizedPlanDtoJson.EmitCanonicalJson(_readPlan);
        var queryJsonA = NormalizedPlanDtoJson.EmitCanonicalJson(_queryPlan);
        var queryJsonB = NormalizedPlanDtoJson.EmitCanonicalJson(_queryPlan);

        writeJsonA.Should().Be(writeJsonB);
        readJsonA.Should().Be(readJsonB);
        queryJsonA.Should().Be(queryJsonB);

        writeJsonA.Should().EndWith("\n");
        readJsonA.Should().EndWith("\n");
        queryJsonA.Should().EndWith("\n");

        writeJsonA.Should().NotContain("\r");
        readJsonA.Should().NotContain("\r");
        queryJsonA.Should().NotContain("\r");

        var writeHashA = NormalizedPlanDtoJson.ComputeCanonicalSha256(writeJsonA);
        var writeHashB = NormalizedPlanDtoJson.ComputeCanonicalSha256(_writePlan);
        var readHashA = NormalizedPlanDtoJson.ComputeCanonicalSha256(readJsonA);
        var readHashB = NormalizedPlanDtoJson.ComputeCanonicalSha256(_readPlan);
        var queryHashA = NormalizedPlanDtoJson.ComputeCanonicalSha256(queryJsonA);
        var queryHashB = NormalizedPlanDtoJson.ComputeCanonicalSha256(_queryPlan);

        writeHashA.Should().Be(writeHashB).And.HaveLength(64);
        readHashA.Should().Be(readHashB).And.HaveLength(64);
        queryHashA.Should().Be(queryHashB).And.HaveLength(64);
    }

    [Test]
    public void It_should_emit_multi_table_read_plan_json_with_explicit_empty_projection_arrays()
    {
        var multiTableReadPlan = new ResourceReadPlanDto(
            Resource: new QualifiedResourceNameDto("Ed-Fi", "Student"),
            KeysetTable: new KeysetTableContractDto("page", "DocumentId"),
            TablePlansInDependencyOrder:
            [
                new TableReadPlanDto(
                    Table: new DbTableNameDto("edfi", "Student"),
                    SelectByKeysetSql: "SELECT r.[DocumentId]\r\nFROM [edfi].[Student] r;"
                ),
                new TableReadPlanDto(
                    Table: new DbTableNameDto("sample", "StudentExtension"),
                    SelectByKeysetSql: "SELECT e.[DocumentId]\r\nFROM [sample].[StudentExtension] e;"
                ),
            ],
            ReferenceIdentityProjectionPlansInDependencyOrder: [],
            DescriptorProjectionPlansInOrder: []
        );

        var json = NormalizedPlanDtoJson.EmitCanonicalJson(multiTableReadPlan);

        json.Should().Contain("\"reference_identity_projection_plans_in_dependency_order\": []");
        json.Should().Contain("\"descriptor_projection_plans_in_order\": []");
        json.Should().Contain("\"schema\": \"edfi\"");
        json.Should().Contain("\"name\": \"Student\"");
        json.Should().Contain("\"schema\": \"sample\"");
        json.Should().Contain("\"name\": \"StudentExtension\"");
        json.IndexOf("\"name\": \"Student\"", StringComparison.Ordinal)
            .Should()
            .BeLessThan(json.IndexOf("\"name\": \"StudentExtension\"", StringComparison.Ordinal));
    }

    [Test]
    public void It_should_change_query_hash_when_authoritative_parameter_order_changes()
    {
        var reorderedQueryPlan = _queryPlan with
        {
            PageParametersInOrder =
            [
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Limit, "limit"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Offset, "offset"),
            ],
        };

        var originalHash = NormalizedPlanDtoJson.ComputeCanonicalSha256(_queryPlan);
        var reorderedHash = NormalizedPlanDtoJson.ComputeCanonicalSha256(reorderedQueryPlan);

        reorderedHash.Should().NotBe(originalHash);
    }
}
