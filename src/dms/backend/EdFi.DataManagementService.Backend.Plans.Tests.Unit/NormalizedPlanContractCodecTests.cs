// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_NormalizedPlanContractCodec : WritePlanCompilerTestBase
{
    private RelationalResourceModel _model = null!;
    private ResourceWritePlan _writePlan = null!;
    private ResourceReadPlan _readPlan = null!;
    private PageDocumentIdSqlPlan _queryPlan = null!;

    [SetUp]
    public void SetUpSubject()
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

        var sourceTablePlan = _readPlan.TablePlansInDependencyOrder[0];
        var decodedTablePlan = decoded.TablePlansInDependencyOrder[0];
        decodedTablePlan.SelectByKeysetSql.Should().Be(sourceTablePlan.SelectByKeysetSql);

        decoded.KeysetTable.Table.Name.Should().Be(_readPlan.KeysetTable.Table.Name);
        decoded.KeysetTable.DocumentIdColumnName.Should().Be(_readPlan.KeysetTable.DocumentIdColumnName);

        var sourceReferenceBinding = _readPlan
            .ReferenceIdentityProjectionPlansInDependencyOrder[0]
            .BindingsInOrder[0];

        var decodedReferenceBinding = decoded
            .ReferenceIdentityProjectionPlansInDependencyOrder[0]
            .BindingsInOrder[0];

        decodedReferenceBinding.FkColumnOrdinal.Should().Be(sourceReferenceBinding.FkColumnOrdinal);
        decodedReferenceBinding
            .IdentityFieldOrdinalsInOrder.Select(static field => field.ColumnOrdinal)
            .Should()
            .Equal(
                sourceReferenceBinding.IdentityFieldOrdinalsInOrder.Select(static field =>
                    field.ColumnOrdinal
                )
            );

        decodedReferenceBinding
            .ReferenceObjectPath.Canonical.Should()
            .Be(sourceReferenceBinding.ReferenceObjectPath.Canonical);

        decodedReferenceBinding
            .ReferenceObjectPath.Should()
            .NotBeSameAs(sourceReferenceBinding.ReferenceObjectPath);

        var sourceReferenceFieldPath = sourceReferenceBinding
            .IdentityFieldOrdinalsInOrder[0]
            .ReferenceJsonPath;

        var decodedReferenceFieldPath = decodedReferenceBinding
            .IdentityFieldOrdinalsInOrder[0]
            .ReferenceJsonPath;

        decodedReferenceFieldPath.Canonical.Should().Be(sourceReferenceFieldPath.Canonical);
        decodedReferenceFieldPath.Should().NotBeSameAs(sourceReferenceFieldPath);

        var sourceDescriptorPlan = _readPlan.DescriptorProjectionPlansInOrder[0];
        var decodedDescriptorPlan = decoded.DescriptorProjectionPlansInOrder[0];
        decodedDescriptorPlan.SelectByKeysetSql.Should().Be(sourceDescriptorPlan.SelectByKeysetSql);
        decodedDescriptorPlan.ResultShape.Should().Be(sourceDescriptorPlan.ResultShape);
        decodedDescriptorPlan
            .SourcesInOrder.Select(static source => source.DescriptorIdColumnOrdinal)
            .Should()
            .Equal(
                sourceDescriptorPlan.SourcesInOrder.Select(static source => source.DescriptorIdColumnOrdinal)
            );

        var sourceDescriptorPath = sourceDescriptorPlan.SourcesInOrder[0].DescriptorValuePath;
        var decodedDescriptorPath = decodedDescriptorPlan.SourcesInOrder[0].DescriptorValuePath;
        decodedDescriptorPath.Canonical.Should().Be(sourceDescriptorPath.Canonical);
        decodedDescriptorPath.Should().NotBeSameAs(sourceDescriptorPath);
    }

    [Test]
    public void It_should_roundtrip_multi_table_resource_read_plan_through_normalized_dto_without_collapsing_story_05_shape()
    {
        var multiTableModel = CreateSupportedMultiTableModel();
        var multiTableReadPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(multiTableModel);
        var encoded = NormalizedPlanContractCodec.Encode(multiTableReadPlan);
        var decoded = NormalizedPlanContractCodec.Decode(encoded, multiTableModel);
        var reEncoded = NormalizedPlanContractCodec.Encode(decoded);

        NormalizedPlanDtoJson
            .ComputeCanonicalSha256(reEncoded)
            .Should()
            .Be(NormalizedPlanDtoJson.ComputeCanonicalSha256(encoded));

        encoded
            .TablePlansInDependencyOrder.Select(static plan => $"{plan.Table.Schema}.{plan.Table.Name}")
            .Should()
            .Equal(
                multiTableModel.TablesInDependencyOrder.Select(static table =>
                    $"{table.Table.Schema.Value}.{table.Table.Name}"
                )
            );

        decoded
            .TablePlansInDependencyOrder.Select(static plan => plan.TableModel.Table)
            .Should()
            .Equal(multiTableModel.TablesInDependencyOrder.Select(static table => table.Table));

        encoded.ReferenceIdentityProjectionPlansInDependencyOrder.Should().BeEmpty();
        encoded.DescriptorProjectionPlansInOrder.Should().BeEmpty();
        decoded.ReferenceIdentityProjectionPlansInDependencyOrder.Should().BeEmpty();
        decoded.DescriptorProjectionPlansInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_should_fail_fast_when_decoding_read_plan_with_unknown_projection_table()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var descriptorPlan = encoded.DescriptorProjectionPlansInOrder[0];
        var sources = descriptorPlan.SourcesInOrder.ToArray();

        sources[0] = sources[0] with { Table = new DbTableNameDto("edfi", "MissingProjectionTable") };

        var mutated = encoded with
        {
            DescriptorProjectionPlansInOrder = [descriptorPlan with { SourcesInOrder = [.. sources] }],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Contain(nameof(DescriptorProjectionSourceDto.Table));
        exception.Message.Should().Contain("Unknown table 'edfi.MissingProjectionTable'");
    }

    [Test]
    public void It_should_fail_fast_when_decoding_read_plan_with_unknown_reference_projection_table()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var projectionTablePlan = encoded.ReferenceIdentityProjectionPlansInDependencyOrder[0];
        var mutated = encoded with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    Table = new DbTableNameDto("edfi", "MissingReferenceProjectionTable"),
                },
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception
            .ParamName.Should()
            .Contain(nameof(ResourceReadPlanDto.ReferenceIdentityProjectionPlansInDependencyOrder));
        exception.Message.Should().Contain("Unknown table 'edfi.MissingReferenceProjectionTable'");
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
            .PageParametersInOrder.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal("schoolYear", "offset", "limit");
        decoded.TotalCountParametersInOrder.Should().NotBeNull();
        decoded
            .TotalCountParametersInOrder!.Value.Select(static parameter => parameter.ParameterName)
            .Should()
            .Equal("schoolYear");
    }

    [Test]
    public void It_should_preserve_read_plan_hash_when_model_lookup_collections_are_permuted()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var baselineHash = NormalizedPlanDtoJson.ComputeCanonicalSha256(encoded);
        var permutedModel = CreateModelWithPermutedLookups(_model);

        var decoded = NormalizedPlanContractCodec.Decode(encoded, permutedModel);
        var reEncoded = NormalizedPlanContractCodec.Encode(decoded);

        NormalizedPlanDtoJson.ComputeCanonicalSha256(reEncoded).Should().Be(baselineHash);
    }

    [Test]
    public void It_should_change_hash_when_order_sensitive_lists_are_permuted()
    {
        var baselineWriteHash = ComputeCanonicalWritePlanHash(_writePlan);
        var baselineQueryHash = ComputeCanonicalQueryPlanHash(_queryPlan);
        var sourceTablePlan = _writePlan.TablePlansInDependencyOrder[0];

        var reorderedColumnBindingsPlan = _writePlan with
        {
            TablePlansInDependencyOrder =
            [
                sourceTablePlan with
                {
                    ColumnBindings =
                    [
                        sourceTablePlan.ColumnBindings[1],
                        sourceTablePlan.ColumnBindings[0],
                        .. sourceTablePlan.ColumnBindings.Skip(2),
                    ],
                },
            ],
        };

        var sourceKeyUnificationPlan = sourceTablePlan.KeyUnificationPlans[0];
        var reorderedKeyUnificationMembersPlan = _writePlan with
        {
            TablePlansInDependencyOrder =
            [
                sourceTablePlan with
                {
                    KeyUnificationPlans =
                    [
                        sourceKeyUnificationPlan with
                        {
                            MembersInOrder =
                            [
                                sourceKeyUnificationPlan.MembersInOrder[1],
                                sourceKeyUnificationPlan.MembersInOrder[0],
                            ],
                        },
                    ],
                },
            ],
        };

        var reorderedQueryPlan = _queryPlan with
        {
            PageParametersInOrder =
            [
                _queryPlan.PageParametersInOrder[0],
                _queryPlan.PageParametersInOrder[2],
                _queryPlan.PageParametersInOrder[1],
            ],
        };

        ComputeCanonicalWritePlanHash(reorderedColumnBindingsPlan).Should().NotBe(baselineWriteHash);
        ComputeCanonicalWritePlanHash(reorderedKeyUnificationMembersPlan).Should().NotBe(baselineWriteHash);
        ComputeCanonicalQueryPlanHash(reorderedQueryPlan).Should().NotBe(baselineQueryHash);
    }

    [Test]
    public void It_should_emit_stable_duplicate_query_parameter_failures_across_permutations()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_queryPlan);

        var firstPermutation = encoded with
        {
            PageParametersInOrder =
            [
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Offset, "offset"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Limit, "OffSet"),
            ],
        };

        var secondPermutation = encoded with
        {
            PageParametersInOrder =
            [
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Limit, "OffSet"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Offset, "offset"),
            ],
        };

        Action firstAct = () => NormalizedPlanContractCodec.Decode(firstPermutation);
        Action secondAct = () => NormalizedPlanContractCodec.Decode(secondPermutation);

        var firstException = firstAct.Should().Throw<ArgumentException>().Which;
        var secondException = secondAct.Should().Throw<ArgumentException>().Which;

        secondException.ParamName.Should().Be(firstException.ParamName);
        secondException.Message.Should().Be(firstException.Message);
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
    public void It_should_fail_fast_when_descriptor_projection_ordinal_is_out_of_range()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var descriptorPlan = encoded.DescriptorProjectionPlansInOrder[0];
        var descriptorSources = descriptorPlan.SourcesInOrder.ToArray();

        descriptorSources[0] = descriptorSources[0] with { DescriptorIdColumnOrdinal = 100 };

        var mutated = encoded with
        {
            DescriptorProjectionPlansInOrder =
            [
                descriptorPlan with
                {
                    SourcesInOrder = [.. descriptorSources],
                },
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        exception.ParamName.Should().Contain(nameof(DescriptorProjectionSourceDto.DescriptorIdColumnOrdinal));
        exception.Message.Should().Contain("out of range");
    }

    [Test]
    public void It_should_fail_fast_when_descriptor_projection_result_shape_is_not_descriptor_id_then_uri()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var descriptorPlan = encoded.DescriptorProjectionPlansInOrder[0];
        var mutated = encoded with
        {
            DescriptorProjectionPlansInOrder =
            [
                descriptorPlan with
                {
                    ResultShape = descriptorPlan.ResultShape with { DescriptorIdOrdinal = 1, UriOrdinal = 0 },
                },
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Contain(nameof(DescriptorProjectionPlanDto.ResultShape));
        exception
            .Message.Should()
            .Contain(
                "Descriptor projection result shape must expose DescriptorId at ordinal 0 and Uri at ordinal 1"
            );
    }

    [Test]
    public void It_should_fail_fast_when_keyset_temp_table_name_is_not_supported()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var pgsqlKeysetTempTableName = KeysetTableConventions
            .GetKeysetTableContract(SqlDialect.Pgsql)
            .Table.Name;
        var mssqlKeysetTempTableName = KeysetTableConventions
            .GetKeysetTableContract(SqlDialect.Mssql)
            .Table.Name;
        var mutated = encoded with
        {
            KeysetTable = encoded.KeysetTable with { TempTableName = "KeysetPage" },
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Contain(nameof(KeysetTableContractDto.TempTableName));
        exception.Message.Should().Contain("Unsupported keyset temp table name");
        exception.Message.Should().Contain(pgsqlKeysetTempTableName);
        exception.Message.Should().Contain(mssqlKeysetTempTableName);
    }

    [Test]
    public void It_should_fail_fast_when_keyset_document_id_column_name_is_not_supported()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var mutated = encoded with
        {
            KeysetTable = encoded.KeysetTable with { DocumentIdColumnName = "DocId" },
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Contain(nameof(KeysetTableContractDto.DocumentIdColumnName));
        exception.Message.Should().Contain("Unsupported keyset DocumentId column name");
        exception.Message.Should().Contain("DocumentId");
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
    public void It_should_fail_fast_when_query_parameters_are_missing_offset_role()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_queryPlan);

        var mutated = encoded with
        {
            PageParametersInOrder =
            [
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Limit, "limit"),
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Be(nameof(PageDocumentIdSqlPlanDto.PageParametersInOrder));
        exception.Message.Should().Contain("exactly one Offset and one Limit role entry");
        exception.Message.Should().Contain("Offset=0");
        exception.Message.Should().Contain("Limit=1");
    }

    [Test]
    public void It_should_fail_fast_when_query_parameters_are_missing_limit_role()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_queryPlan);

        var mutated = encoded with
        {
            PageParametersInOrder =
            [
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Offset, "offset"),
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Be(nameof(PageDocumentIdSqlPlanDto.PageParametersInOrder));
        exception.Message.Should().Contain("exactly one Offset and one Limit role entry");
        exception.Message.Should().Contain("Offset=1");
        exception.Message.Should().Contain("Limit=0");
    }

    [Test]
    public void It_should_fail_fast_when_query_parameters_have_duplicate_offset_roles()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_queryPlan);

        var mutated = encoded with
        {
            PageParametersInOrder =
            [
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Offset, "offset"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Offset, "offsetTwo"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Limit, "limit"),
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Be(nameof(PageDocumentIdSqlPlanDto.PageParametersInOrder));
        exception.Message.Should().Contain("exactly one Offset and one Limit role entry");
        exception.Message.Should().Contain("Offset=2");
        exception.Message.Should().Contain("Limit=1");
    }

    [Test]
    public void It_should_fail_fast_when_query_parameters_have_duplicate_limit_roles()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_queryPlan);

        var mutated = encoded with
        {
            PageParametersInOrder =
            [
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Offset, "offset"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Limit, "limit"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Limit, "limitTwo"),
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Be(nameof(PageDocumentIdSqlPlanDto.PageParametersInOrder));
        exception.Message.Should().Contain("exactly one Offset and one Limit role entry");
        exception.Message.Should().Contain("Offset=1");
        exception.Message.Should().Contain("Limit=2");
    }

    [Test]
    public void It_should_fail_fast_when_query_parameter_name_is_invalid()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_queryPlan);
        var mutatedParameters = encoded.PageParametersInOrder.ToArray();

        mutatedParameters[0] = mutatedParameters[0] with { ParameterName = "invalid-name" };

        var mutated = encoded with { PageParametersInOrder = [.. mutatedParameters] };

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
            PageParametersInOrder =
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

    [Test]
    public void It_should_fail_fast_when_total_count_parameters_are_present_without_total_count_sql()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_queryPlan);
        var mutated = encoded with
        {
            TotalCountSql = null,
            TotalCountParametersInOrder =
            [
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.Message.Should().Contain("must be null when");
    }

    [Test]
    public void It_should_fail_fast_when_total_count_parameters_are_missing_for_total_count_sql()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_queryPlan);
        var mutated = encoded with { TotalCountParametersInOrder = null };

        var act = () => NormalizedPlanContractCodec.Decode(mutated);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.Message.Should().Contain("is required when");
    }

    [Test]
    public void It_should_fail_fast_when_total_count_parameters_include_non_filter_roles()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_queryPlan);
        var mutated = encoded with
        {
            TotalCountParametersInOrder =
            [
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Filter, "schoolYear"),
                new QuerySqlParameterDto(QuerySqlParameterRoleDto.Offset, "offset"),
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Be(nameof(PageDocumentIdSqlPlanDto.TotalCountParametersInOrder));
        exception.Message.Should().Contain("may only include Filter role entries");
        exception.Message.Should().Contain("Offset=1");
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
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: Path("$.schoolYearDescriptor"),
                    Table: table.Table,
                    FkColumn: new DbColumnName("SchoolYear_DescriptorAlias"),
                    DescriptorResource: new QualifiedResourceName("Ed-Fi", "SchoolYearTypeDescriptor")
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
            PageParametersInOrder:
            [
                new QuerySqlParameter(QuerySqlParameterRole.Filter, "schoolYear"),
                new QuerySqlParameter(QuerySqlParameterRole.Offset, "offset"),
                new QuerySqlParameter(QuerySqlParameterRole.Limit, "limit"),
            ],
            TotalCountParametersInOrder: [new QuerySqlParameter(QuerySqlParameterRole.Filter, "schoolYear")]
        );
    }

    private static JsonPathExpression Path(string value)
    {
        return JsonPathExpressionCompiler.Compile(value);
    }

    private static RelationalResourceModel CreateModelWithPermutedLookups(RelationalResourceModel model)
    {
        return model with
        {
            DocumentReferenceBindings = [.. model.DocumentReferenceBindings.Reverse()],
            DescriptorEdgeSources = [.. model.DescriptorEdgeSources.Reverse()],
        };
    }

    private static string ComputeCanonicalWritePlanHash(ResourceWritePlan plan)
    {
        return NormalizedPlanDtoJson.ComputeCanonicalSha256(NormalizedPlanContractCodec.Encode(plan));
    }

    private static string ComputeCanonicalQueryPlanHash(PageDocumentIdSqlPlan plan)
    {
        return NormalizedPlanDtoJson.ComputeCanonicalSha256(NormalizedPlanContractCodec.Encode(plan));
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
