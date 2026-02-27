// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_NormalizedPlanContractCodec
{
    private RelationalResourceModel _model = null!;
    private ResourceWritePlan _writePlan = null!;
    private ResourceReadPlan _readPlan = null!;
    private PageDocumentIdSqlPlan _queryPlan = null!;

    [SetUp]
    public void Setup()
    {
        _model = CreateModel();
        _writePlan = CreateWritePlan(_model);
        _readPlan = CreateReadPlan(_model);
        _queryPlan = CreateQueryPlan();
    }

    [Test]
    public void It_should_roundtrip_resource_write_plan_through_normalized_dto_without_losing_deterministic_shape()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_writePlan);
        var decoded = NormalizedPlanContractCodec.Decode(encoded, _model);
        var reEncoded = NormalizedPlanContractCodec.Encode(decoded);

        NormalizedPlanDtoJson
            .ComputeCanonicalSha256(reEncoded)
            .Should()
            .Be(NormalizedPlanDtoJson.ComputeCanonicalSha256(encoded));

        var sourceTablePlan = _writePlan.TablePlansInDependencyOrder[0];
        var decodedTablePlan = decoded.TablePlansInDependencyOrder[0];

        decodedTablePlan.InsertSql.Should().Be(sourceTablePlan.InsertSql);
        decodedTablePlan.UpdateSql.Should().Be(sourceTablePlan.UpdateSql);
        decodedTablePlan.DeleteByParentSql.Should().Be(sourceTablePlan.DeleteByParentSql);

        decodedTablePlan
            .TableModel.Table.Schema.Value.Should()
            .Be(sourceTablePlan.TableModel.Table.Schema.Value);
        decodedTablePlan.TableModel.Table.Name.Should().Be(sourceTablePlan.TableModel.Table.Name);

        decodedTablePlan
            .ColumnBindings.Select(static binding => binding.ParameterName)
            .Should()
            .Equal(
                "documentId",
                "parentSchoolDocumentId",
                "ordinal",
                "schoolDocumentId",
                "schoolYear",
                "calendarDocumentId",
                "gradeLevelDescriptorId",
                "schoolYearCanonical",
                "schoolYearPresent"
            );

        decodedTablePlan
            .ColumnBindings.Select(static binding => binding.Column.ColumnName.Value)
            .Should()
            .Equal(sourceTablePlan.ColumnBindings.Select(static binding => binding.Column.ColumnName.Value));

        decodedTablePlan
            .ColumnBindings.Select(static binding => GetWriteValueSourceKind(binding.Source))
            .Should()
            .Equal(
                nameof(WriteValueSource.DocumentId),
                nameof(WriteValueSource.ParentKeyPart),
                nameof(WriteValueSource.Ordinal),
                nameof(WriteValueSource.DocumentReference),
                nameof(WriteValueSource.Scalar),
                nameof(WriteValueSource.DocumentReference),
                nameof(WriteValueSource.DescriptorReference),
                nameof(WriteValueSource.Precomputed),
                nameof(WriteValueSource.Precomputed)
            );

        var sourceKeyUnificationPlan = sourceTablePlan.KeyUnificationPlans[0];
        var decodedKeyUnificationPlan = decodedTablePlan.KeyUnificationPlans[0];

        decodedKeyUnificationPlan
            .CanonicalBindingIndex.Should()
            .Be(sourceKeyUnificationPlan.CanonicalBindingIndex);

        var sourceDescriptorMember = (KeyUnificationMemberWritePlan.DescriptorMember)
            sourceKeyUnificationPlan.MembersInOrder[1];

        var decodedDescriptorMember = (KeyUnificationMemberWritePlan.DescriptorMember)
            decodedKeyUnificationPlan.MembersInOrder[1];

        decodedDescriptorMember.PresenceBindingIndex.Should().Be(sourceDescriptorMember.PresenceBindingIndex);
        decodedDescriptorMember.PresenceIsSynthetic.Should().Be(sourceDescriptorMember.PresenceIsSynthetic);
    }

    [Test]
    public void It_should_roundtrip_resource_read_plan_through_normalized_dto_without_losing_projection_metadata()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var decoded = NormalizedPlanContractCodec.Decode(encoded, _model);
        var reEncoded = NormalizedPlanContractCodec.Encode(decoded);

        NormalizedPlanDtoJson
            .ComputeCanonicalSha256(reEncoded)
            .Should()
            .Be(NormalizedPlanDtoJson.ComputeCanonicalSha256(encoded));

        decoded
            .ReferenceIdentityProjectionPlansInDependencyOrder[0]
            .BindingsInOrder[0]
            .ReferenceObjectPath.Canonical.Should()
            .Be("$.schoolReference");

        decoded
            .DescriptorProjectionPlansInOrder[0]
            .SourcesInOrder[0]
            .DescriptorValuePath.Canonical.Should()
            .Be("$.gradeLevelDescriptor");
    }

    [Test]
    public void It_should_roundtrip_query_plan_through_normalized_dto_without_losing_parameter_inventory_order()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_queryPlan);
        var decoded = NormalizedPlanContractCodec.Decode(encoded);
        var reEncoded = NormalizedPlanContractCodec.Encode(decoded);

        NormalizedPlanDtoJson
            .ComputeCanonicalSha256(reEncoded)
            .Should()
            .Be(NormalizedPlanDtoJson.ComputeCanonicalSha256(encoded));

        decoded
            .ParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal("schoolYear", "offset", "limit");
    }

    [Test]
    public void It_should_fail_fast_when_decoding_write_plan_with_unknown_table()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_writePlan);
        var mutatedTable = encoded.TablePlansInDependencyOrder[0] with
        {
            Table = new DbTableNameDto("edfi", "MissingStudentSchoolAssociation"),
        };

        var mutated = encoded with { TablePlansInDependencyOrder = [mutatedTable] };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.Message.Should().Contain("Unknown table 'edfi.MissingStudentSchoolAssociation'");
    }

    [Test]
    public void It_should_fail_fast_when_decoding_write_plan_with_unknown_column()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_writePlan);
        var tablePlan = encoded.TablePlansInDependencyOrder[0];
        var mutatedBindings = tablePlan.ColumnBindings.ToArray();

        mutatedBindings[0] = mutatedBindings[0] with { ColumnName = "MissingColumn" };

        var mutated = encoded with
        {
            TablePlansInDependencyOrder = [tablePlan with { ColumnBindings = [.. mutatedBindings] }],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.Message.Should().Contain("Unknown column 'MissingColumn'");
    }

    [Test]
    public void It_should_fail_fast_when_decoding_write_plan_with_duplicate_parameter_names()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_writePlan);
        var tablePlan = encoded.TablePlansInDependencyOrder[0];
        var mutatedBindings = tablePlan.ColumnBindings.ToArray();

        mutatedBindings[1] = mutatedBindings[1] with { ParameterName = "DocumentId" };

        var mutated = encoded with
        {
            TablePlansInDependencyOrder = [tablePlan with { ColumnBindings = [.. mutatedBindings] }],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Be(nameof(TableWritePlanDto.ColumnBindings));
        exception.Message.Should().Contain("Duplicate parameter names are not allowed");
        exception.Message.Should().Contain("'DocumentId'");
        exception.Message.Should().Contain("'documentId'");
    }

    [Test]
    public void It_should_fail_fast_when_document_reference_binding_index_is_out_of_range()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_writePlan);
        var tablePlan = encoded.TablePlansInDependencyOrder[0];
        var mutatedBindings = tablePlan.ColumnBindings.ToArray();

        mutatedBindings[2] = mutatedBindings[2] with
        {
            Source = new WriteValueSourceDto.DocumentReference(BindingIndex: 42),
        };

        var mutated = encoded with
        {
            TablePlansInDependencyOrder = [tablePlan with { ColumnBindings = [.. mutatedBindings] }],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        exception.ParamName.Should().Be("bindingIndex");
        exception.Message.Should().Contain("DocumentReferenceBindings");
    }

    [Test]
    public void It_should_fail_fast_when_reference_identity_fk_ordinal_is_out_of_range()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var projectionTablePlan = encoded.ReferenceIdentityProjectionPlansInDependencyOrder[0];
        var bindings = projectionTablePlan.BindingsInOrder.ToArray();

        bindings[0] = bindings[0] with { FkColumnOrdinal = 100 };

        var mutated = encoded with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    BindingsInOrder = [.. bindings],
                },
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        exception.ParamName.Should().Contain(nameof(ReferenceIdentityProjectionBindingDto.FkColumnOrdinal));
        exception.Message.Should().Contain("out of range");
    }

    [Test]
    public void It_should_fail_fast_when_reference_object_path_does_not_match_document_reference_binding_index()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var projectionTablePlan = encoded.ReferenceIdentityProjectionPlansInDependencyOrder[0];
        var bindings = projectionTablePlan.BindingsInOrder.ToArray();

        bindings[0] = bindings[0] with { ReferenceObjectPath = "$.calendarReference" };

        var mutated = encoded with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    BindingsInOrder = [.. bindings],
                },
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("Document-reference binding index");
        exception.Message.Should().Contain("$.schoolReference");
        exception.Message.Should().Contain("$.calendarReference");
    }

    [Test]
    public void It_should_fail_fast_when_query_parameter_name_is_invalid()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_queryPlan);
        var mutatedParameters = encoded.ParametersInOrder.ToArray();

        mutatedParameters[0] = mutatedParameters[0] with { ParameterName = "invalid-name" };

        var mutated = encoded with { ParametersInOrder = [.. mutatedParameters] };

        var act = () => NormalizedPlanContractCodec.Decode(mutated);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.Message.Should().Contain("must match pattern");
    }

    [Test]
    public void It_should_fail_fast_when_query_parameter_names_are_duplicate_case_insensitively()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_queryPlan);

        var mutated = encoded with
        {
            ParametersInOrder =
            [
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Offset, "offset"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Limit, "OffSet"),
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.Message.Should().Contain("Duplicate parameter names");
        exception.Message.Should().Contain("'OffSet'");
        exception.Message.Should().Contain("'offset'");
    }

    private static RelationalResourceModel CreateModel()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation");
        var table = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation"),
            JsonScope: Path("$"),
            Key: new TableKey(
                ConstraintName: "PK_StudentSchoolAssociation",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Ordinal"),
                    Kind: ColumnKind.Ordinal,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: Path("$.schoolReference"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "School")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_RefSchoolId"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: Path("$.schoolReference.schoolId"),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_RefSchoolYear"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: Path("$.schoolReference.schoolYear"),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Calendar_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: Path("$.calendarReference"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Calendar")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Calendar_RefCalendarCode"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    IsNullable: true,
                    SourceJsonPath: Path("$.calendarReference.calendarCode"),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYear"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: Path("$.schoolYear"),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("GradeLevel_DescriptorId"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: Path("$.gradeLevelDescriptor"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYear_Canonical"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: Path("$.schoolYear"),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYear_DescriptorAlias"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: Path("$.schoolYearDescriptor"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "SchoolYearTypeDescriptor")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYear_Present"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Boolean),
                    IsNullable: true,
                    SourceJsonPath: Path("$.schoolYearDescriptor"),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("ParentSchool_DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: table,
            TablesInDependencyOrder: [table],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: Path("$.schoolReference"),
                    Table: table.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: Path("$.schoolReference.schoolId"),
                            Column: new DbColumnName("School_RefSchoolId")
                        ),
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: Path("$.schoolReference.schoolYear"),
                            Column: new DbColumnName("School_RefSchoolYear")
                        ),
                    ]
                ),
                new DocumentReferenceBinding(
                    IsIdentityComponent: false,
                    ReferenceObjectPath: Path("$.calendarReference"),
                    Table: table.Table,
                    FkColumn: new DbColumnName("Calendar_DocumentId"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Calendar"),
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: Path("$.calendarReference.calendarCode"),
                            Column: new DbColumnName("Calendar_RefCalendarCode")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources:
            [
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: Path("$.gradeLevelDescriptor"),
                    Table: table.Table,
                    FkColumn: new DbColumnName("GradeLevel_DescriptorId"),
                    DescriptorResource: new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor")
                ),
            ]
        );
    }

    private static ResourceWritePlan CreateWritePlan(RelationalResourceModel model)
    {
        var table = model.Root;

        DbColumnModel Column(string name)
        {
            return table.Columns.Single(column =>
                string.Equals(column.ColumnName.Value, name, StringComparison.Ordinal)
            );
        }

        var tablePlan = new TableWritePlan(
            TableModel: table,
            InsertSql: "INSERT INTO [edfi].[StudentSchoolAssociation] ([DocumentId], [ParentSchool_DocumentId], [Ordinal], [School_DocumentId], [SchoolYear], [Calendar_DocumentId], [GradeLevel_DescriptorId], [SchoolYear_Canonical], [SchoolYear_Present])\nVALUES (@documentId, @parentSchoolDocumentId, @ordinal, @schoolDocumentId, @schoolYear, @calendarDocumentId, @gradeLevelDescriptorId, @schoolYearCanonical, @schoolYearPresent);",
            UpdateSql: "UPDATE [edfi].[StudentSchoolAssociation]\nSET [SchoolYear] = @schoolYear\nWHERE [DocumentId] = @documentId;",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(
                MaxRowsPerBatch: 233,
                ParametersPerRow: 9,
                MaxParametersPerCommand: 2100
            ),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    Column: Column("DocumentId"),
                    Source: new WriteValueSource.DocumentId(),
                    ParameterName: "documentId"
                ),
                new WriteColumnBinding(
                    Column: Column("ParentSchool_DocumentId"),
                    Source: new WriteValueSource.ParentKeyPart(Index: 0),
                    ParameterName: "parentSchoolDocumentId"
                ),
                new WriteColumnBinding(
                    Column: Column("Ordinal"),
                    Source: new WriteValueSource.Ordinal(),
                    ParameterName: "ordinal"
                ),
                new WriteColumnBinding(
                    Column: Column("School_DocumentId"),
                    Source: new WriteValueSource.DocumentReference(BindingIndex: 0),
                    ParameterName: "schoolDocumentId"
                ),
                new WriteColumnBinding(
                    Column: Column("SchoolYear"),
                    Source: new WriteValueSource.Scalar(
                        RelativePath: Path("$.schoolYear"),
                        Type: new RelationalScalarType(ScalarKind.Int32)
                    ),
                    ParameterName: "schoolYear"
                ),
                new WriteColumnBinding(
                    Column: Column("Calendar_DocumentId"),
                    Source: new WriteValueSource.DocumentReference(BindingIndex: 1),
                    ParameterName: "calendarDocumentId"
                ),
                new WriteColumnBinding(
                    Column: Column("GradeLevel_DescriptorId"),
                    Source: new WriteValueSource.DescriptorReference(
                        DescriptorResource: new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor"),
                        RelativePath: Path("$.gradeLevelDescriptor"),
                        DescriptorValuePath: Path("$.gradeLevelDescriptor")
                    ),
                    ParameterName: "gradeLevelDescriptorId"
                ),
                new WriteColumnBinding(
                    Column: Column("SchoolYear_Canonical"),
                    Source: new WriteValueSource.Precomputed(),
                    ParameterName: "schoolYearCanonical"
                ),
                new WriteColumnBinding(
                    Column: Column("SchoolYear_Present"),
                    Source: new WriteValueSource.Precomputed(),
                    ParameterName: "schoolYearPresent"
                ),
            ],
            KeyUnificationPlans:
            [
                new KeyUnificationWritePlan(
                    CanonicalColumn: new DbColumnName("SchoolYear_Canonical"),
                    CanonicalBindingIndex: 7,
                    MembersInOrder:
                    [
                        new KeyUnificationMemberWritePlan.ScalarMember(
                            MemberPathColumn: new DbColumnName("SchoolYear"),
                            RelativePath: Path("$.schoolYear"),
                            ScalarType: new RelationalScalarType(ScalarKind.Int32),
                            PresenceColumn: null,
                            PresenceBindingIndex: null,
                            PresenceIsSynthetic: false
                        ),
                        new KeyUnificationMemberWritePlan.DescriptorMember(
                            MemberPathColumn: new DbColumnName("SchoolYear_DescriptorAlias"),
                            RelativePath: Path("$.schoolYearDescriptor"),
                            DescriptorResource: new QualifiedResourceName(
                                "Ed-Fi",
                                "SchoolYearTypeDescriptor"
                            ),
                            PresenceColumn: new DbColumnName("SchoolYear_Present"),
                            PresenceBindingIndex: 8,
                            PresenceIsSynthetic: true
                        ),
                    ]
                ),
            ]
        );

        return new ResourceWritePlan(model, [tablePlan]);
    }

    private static ResourceReadPlan CreateReadPlan(RelationalResourceModel model)
    {
        var table = model.Root.Table;

        return new ResourceReadPlan(
            Model: model,
            KeysetTable: KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            TablePlansInDependencyOrder:
            [
                new TableReadPlan(
                    model.Root,
                    "SELECT r.[DocumentId], r.[School_DocumentId], r.[School_RefSchoolId], r.[School_RefSchoolYear], r.[GradeLevel_DescriptorId]\nFROM [edfi].[StudentSchoolAssociation] r;"
                ),
            ],
            ReferenceIdentityProjectionPlansInDependencyOrder:
            [
                new ReferenceIdentityProjectionTablePlan(
                    Table: table,
                    BindingsInOrder:
                    [
                        new ReferenceIdentityProjectionBinding(
                            IsIdentityComponent: true,
                            ReferenceObjectPath: Path("$.schoolReference"),
                            TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
                            FkColumnOrdinal: 2,
                            IdentityFieldOrdinalsInOrder:
                            [
                                new ReferenceIdentityProjectionFieldOrdinal(
                                    ReferenceJsonPath: Path("$.schoolReference.schoolId"),
                                    ColumnOrdinal: 3
                                ),
                                new ReferenceIdentityProjectionFieldOrdinal(
                                    ReferenceJsonPath: Path("$.schoolReference.schoolYear"),
                                    ColumnOrdinal: 4
                                ),
                            ]
                        ),
                    ]
                ),
            ],
            DescriptorProjectionPlansInOrder:
            [
                new DescriptorProjectionPlan(
                    SelectByKeysetSql: "SELECT r.[GradeLevel_DescriptorId], d.[Uri]\nFROM [edfi].[StudentSchoolAssociation] r\nJOIN [dms].[Descriptor] d ON d.[DescriptorId] = r.[GradeLevel_DescriptorId];",
                    ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                    SourcesInOrder:
                    [
                        new DescriptorProjectionSource(
                            DescriptorValuePath: Path("$.gradeLevelDescriptor"),
                            Table: table,
                            DescriptorResource: new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor"),
                            DescriptorIdColumnOrdinal: 8
                        ),
                    ]
                ),
            ]
        );
    }

    private static PageDocumentIdSqlPlan CreateQueryPlan()
    {
        return new PageDocumentIdSqlPlan(
            PageDocumentIdSql: "SELECT r.[DocumentId]\nFROM [edfi].[StudentSchoolAssociation] r\nWHERE r.[SchoolYear] = @schoolYear\nORDER BY r.[DocumentId] ASC\nOFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;",
            TotalCountSql: "SELECT COUNT(1)\nFROM [edfi].[StudentSchoolAssociation] r\nWHERE r.[SchoolYear] = @schoolYear;",
            ParametersInOrder:
            [
                new QuerySqlParameter(QuerySqlParameterRole.Filter, "schoolYear"),
                new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
            ]
        );
    }

    private static JsonPathExpression Path(string value)
    {
        return JsonPathExpressionCompiler.Compile(value);
    }

    private static string GetWriteValueSourceKind(WriteValueSource source)
    {
        return source switch
        {
            WriteValueSource.DocumentId => nameof(WriteValueSource.DocumentId),
            WriteValueSource.ParentKeyPart => nameof(WriteValueSource.ParentKeyPart),
            WriteValueSource.Ordinal => nameof(WriteValueSource.Ordinal),
            WriteValueSource.Scalar => nameof(WriteValueSource.Scalar),
            WriteValueSource.DocumentReference => nameof(WriteValueSource.DocumentReference),
            WriteValueSource.DescriptorReference => nameof(WriteValueSource.DescriptorReference),
            WriteValueSource.Precomputed => nameof(WriteValueSource.Precomputed),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source.GetType().Name),
        };
    }
}
