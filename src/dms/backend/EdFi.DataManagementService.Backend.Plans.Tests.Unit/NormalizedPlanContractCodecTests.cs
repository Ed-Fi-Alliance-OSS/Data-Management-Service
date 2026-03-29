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
using static EdFi.DataManagementService.Backend.Plans.Tests.Unit.ReadPlanProjectionMutationHelper;

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
    public void It_should_roundtrip_collection_key_preallocation_plan_through_normalized_dto()
    {
        var model = CreateFocusedStableKeyFixtureResourceModel(SqlDialect.Pgsql);
        var sourcePlan = new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);
        var encoded = NormalizedPlanContractCodec.Encode(sourcePlan);
        var decoded = NormalizedPlanContractCodec.Decode(encoded, model);

        var sourceTablePlan = sourcePlan.TablePlansInDependencyOrder.Single(tablePlan =>
            string.Equals(tablePlan.TableModel.Table.Name, "SchoolAddress", StringComparison.Ordinal)
        );
        var decodedTablePlan = decoded.TablePlansInDependencyOrder.Single(tablePlan =>
            string.Equals(tablePlan.TableModel.Table.Name, "SchoolAddress", StringComparison.Ordinal)
        );

        encoded
            .TablePlansInDependencyOrder.Single(tablePlan =>
                string.Equals(tablePlan.Table.Name, "SchoolAddress", StringComparison.Ordinal)
            )
            .CollectionKeyPreallocationPlan.Should()
            .Be(
                new CollectionKeyPreallocationPlanDto(
                    ColumnName: "CollectionItemId",
                    BindingIndex: sourceTablePlan.CollectionKeyPreallocationPlan!.BindingIndex
                )
            );

        decodedTablePlan
            .CollectionKeyPreallocationPlan.Should()
            .Be(sourceTablePlan.CollectionKeyPreallocationPlan);
    }

    [Test]
    public void It_should_roundtrip_collection_merge_plan_through_normalized_dto()
    {
        var model = CreateFocusedStableKeyFixtureResourceModel(SqlDialect.Pgsql);
        var sourcePlan = new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);
        var encoded = NormalizedPlanContractCodec.Encode(sourcePlan);
        var decoded = NormalizedPlanContractCodec.Decode(encoded, model);
        var reEncoded = NormalizedPlanContractCodec.Encode(decoded);

        NormalizedPlanDtoJson
            .ComputeCanonicalSha256(reEncoded)
            .Should()
            .Be(NormalizedPlanDtoJson.ComputeCanonicalSha256(encoded));

        var sourceTablePlan = sourcePlan.TablePlansInDependencyOrder.Single(tablePlan =>
            string.Equals(tablePlan.TableModel.Table.Name, "SchoolAddress", StringComparison.Ordinal)
        );
        var encodedTablePlan = encoded.TablePlansInDependencyOrder.Single(tablePlan =>
            string.Equals(tablePlan.Table.Name, "SchoolAddress", StringComparison.Ordinal)
        );
        var decodedTablePlan = decoded.TablePlansInDependencyOrder.Single(tablePlan =>
            string.Equals(tablePlan.TableModel.Table.Name, "SchoolAddress", StringComparison.Ordinal)
        );

        encodedTablePlan.CollectionMergePlan.Should().NotBeNull();
        encodedTablePlan.DeleteByParentSql.Should().BeNull();

        var sourceCollectionMergePlan = sourceTablePlan.CollectionMergePlan!;
        var encodedCollectionMergePlan = encodedTablePlan.CollectionMergePlan!;

        encodedCollectionMergePlan
            .SemanticIdentityBindings.Select(binding => (binding.RelativePath, binding.BindingIndex))
            .Should()
            .Equal(
                sourceCollectionMergePlan.SemanticIdentityBindings.Select(binding =>
                    (binding.RelativePath.Canonical, binding.BindingIndex)
                )
            );
        encodedCollectionMergePlan
            .StableRowIdentityBindingIndex.Should()
            .Be(sourceCollectionMergePlan.StableRowIdentityBindingIndex);
        encodedCollectionMergePlan
            .UpdateByStableRowIdentitySql.Should()
            .Be(sourceCollectionMergePlan.UpdateByStableRowIdentitySql);
        encodedCollectionMergePlan
            .DeleteByStableRowIdentitySql.Should()
            .Be(sourceCollectionMergePlan.DeleteByStableRowIdentitySql);
        encodedCollectionMergePlan
            .OrdinalBindingIndex.Should()
            .Be(sourceCollectionMergePlan.OrdinalBindingIndex);
        encodedCollectionMergePlan
            .CompareBindingIndexesInOrder.Should()
            .Equal(sourceCollectionMergePlan.CompareBindingIndexesInOrder);

        decodedTablePlan.CollectionMergePlan.Should().NotBeNull();
        decodedTablePlan.DeleteByParentSql.Should().BeNull();

        var decodedCollectionMergePlan = decodedTablePlan.CollectionMergePlan!;

        decodedCollectionMergePlan
            .SemanticIdentityBindings.Select(binding =>
                (binding.RelativePath.Canonical, binding.BindingIndex)
            )
            .Should()
            .Equal(
                sourceCollectionMergePlan.SemanticIdentityBindings.Select(binding =>
                    (binding.RelativePath.Canonical, binding.BindingIndex)
                )
            );
        decodedCollectionMergePlan
            .StableRowIdentityBindingIndex.Should()
            .Be(sourceCollectionMergePlan.StableRowIdentityBindingIndex);
        decodedCollectionMergePlan
            .UpdateByStableRowIdentitySql.Should()
            .Be(sourceCollectionMergePlan.UpdateByStableRowIdentitySql);
        decodedCollectionMergePlan
            .DeleteByStableRowIdentitySql.Should()
            .Be(sourceCollectionMergePlan.DeleteByStableRowIdentitySql);
        decodedCollectionMergePlan
            .OrdinalBindingIndex.Should()
            .Be(sourceCollectionMergePlan.OrdinalBindingIndex);
        decodedCollectionMergePlan
            .CompareBindingIndexesInOrder.Should()
            .Equal(sourceCollectionMergePlan.CompareBindingIndexesInOrder);
    }

    [Test]
    public void It_should_roundtrip_resource_read_plan_through_normalized_dto_without_losing_projection_metadata()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var decoded = NormalizedPlanContractCodec.Decode(encoded, _model);
        var reEncoded = NormalizedPlanContractCodec.Encode(decoded);
        var canonicalJson = NormalizedPlanDtoJson.EmitCanonicalJson(encoded);

        NormalizedPlanDtoJson
            .ComputeCanonicalSha256(reEncoded)
            .Should()
            .Be(NormalizedPlanDtoJson.ComputeCanonicalSha256(encoded));

        canonicalJson.Should().Contain("\"reference_identity_projection_plans_in_dependency_order\"");
        canonicalJson.Should().Contain("\"reference_object_path\": \"$.schoolReference\"");
        canonicalJson.Should().Contain("\"descriptor_projection_plans_in_order\"");
        canonicalJson.Should().Contain("\"descriptor_value_path\": \"$.gradeLevelDescriptor\"");

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
    public void It_should_roundtrip_multiple_descriptor_projection_plans_without_collapsing_collection_order()
    {
        var readPlan = CreateReadPlanWithSplitDescriptorProjectionPlans(CreateReadPlan(_model));
        var encoded = NormalizedPlanContractCodec.Encode(readPlan);
        var decoded = NormalizedPlanContractCodec.Decode(encoded, _model);
        var reEncoded = NormalizedPlanContractCodec.Encode(decoded);

        NormalizedPlanDtoJson
            .ComputeCanonicalSha256(reEncoded)
            .Should()
            .Be(NormalizedPlanDtoJson.ComputeCanonicalSha256(encoded));

        encoded.DescriptorProjectionPlansInOrder.Should().HaveCount(2);
        decoded.DescriptorProjectionPlansInOrder.Should().HaveCount(2);

        decoded
            .DescriptorProjectionPlansInOrder.Select(static plan => plan.SelectByKeysetSql)
            .Should()
            .Equal("SELECT descriptor_plan_0;\n", "SELECT descriptor_plan_1;\n");

        decoded
            .DescriptorProjectionPlansInOrder.SelectMany(static plan => plan.SourcesInOrder)
            .Select(static source => source.DescriptorValuePath.Canonical)
            .Should()
            .Equal("$.gradeLevelDescriptor", "$.schoolYearDescriptor");
    }

    [Test]
    public void It_should_roundtrip_multiple_reference_projection_table_plans_without_collapsing_dependency_order()
    {
        var multiTableModel = CreateInterleavedReferenceProjectionModel(rootBindingFirst: false);
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(multiTableModel);
        var encoded = NormalizedPlanContractCodec.Encode(readPlan);
        var decoded = NormalizedPlanContractCodec.Decode(encoded, multiTableModel);
        var reEncoded = NormalizedPlanContractCodec.Encode(decoded);

        NormalizedPlanDtoJson
            .ComputeCanonicalSha256(reEncoded)
            .Should()
            .Be(NormalizedPlanDtoJson.ComputeCanonicalSha256(encoded));

        encoded.ReferenceIdentityProjectionPlansInDependencyOrder.Should().HaveCount(2);
        decoded.ReferenceIdentityProjectionPlansInDependencyOrder.Should().HaveCount(2);

        decoded
            .ReferenceIdentityProjectionPlansInDependencyOrder.Select(static plan => plan.Table)
            .Should()
            .Equal(
                multiTableModel.TablesInDependencyOrder[0].Table,
                multiTableModel.TablesInDependencyOrder[1].Table
            );

        decoded
            .ReferenceIdentityProjectionPlansInDependencyOrder[0]
            .BindingsInOrder.Select(static binding => binding.ReferenceObjectPath.Canonical)
            .Should()
            .Equal("$.schoolReference");

        decoded
            .ReferenceIdentityProjectionPlansInDependencyOrder[1]
            .BindingsInOrder.Select(static binding => binding.ReferenceObjectPath.Canonical)
            .Should()
            .Equal("$.addresses[*].calendarReference", "$.addresses[*].sessionReference");
    }

    [Test]
    public void It_should_roundtrip_grouped_reference_projection_fields_through_normalized_dto_without_losing_logical_field_order_or_ordinals()
    {
        var groupedModel = CreateGroupedReferenceProjectionResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(groupedModel);
        var encoded = NormalizedPlanContractCodec.Encode(readPlan);
        var decoded = NormalizedPlanContractCodec.Decode(encoded, groupedModel);
        var reEncoded = NormalizedPlanContractCodec.Encode(decoded);

        NormalizedPlanDtoJson
            .ComputeCanonicalSha256(reEncoded)
            .Should()
            .Be(NormalizedPlanDtoJson.ComputeCanonicalSha256(encoded));

        var sourceBinding = readPlan
            .ReferenceIdentityProjectionPlansInDependencyOrder.Single()
            .BindingsInOrder.Single();
        var decodedBinding = decoded
            .ReferenceIdentityProjectionPlansInDependencyOrder.Single()
            .BindingsInOrder.Single();

        decodedBinding.ReferenceObjectPath.Canonical.Should().Be("$.schoolReference");
        decodedBinding
            .IdentityFieldOrdinalsInOrder.Select(static field => field.ReferenceJsonPath.Canonical)
            .Should()
            .Equal(
                sourceBinding.IdentityFieldOrdinalsInOrder.Select(static field =>
                    field.ReferenceJsonPath.Canonical
                )
            );
        decodedBinding
            .IdentityFieldOrdinalsInOrder.Select(static field => field.ColumnOrdinal)
            .Should()
            .Equal(sourceBinding.IdentityFieldOrdinalsInOrder.Select(static field => field.ColumnOrdinal));
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
    public void It_should_fail_fast_when_model_document_reference_binding_order_is_permuted_relative_to_the_encoded_read_plan()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var permutedModel = CreateModelWithPermutedDocumentReferenceBindings(_model);

        var act = () => NormalizedPlanContractCodec.Decode(encoded, permutedModel);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("Decoded read plan for resource");
        exception.Message.Should().Contain("reference identity projection binding at index '0'");
        exception.Message.Should().Contain("$.schoolReference");
        exception.Message.Should().Contain("$.calendarReference");
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
    public void It_should_fail_fast_when_collection_merge_plan_is_combined_with_delete_by_parent_sql()
    {
        var (model, encoded, schoolAddressIndex) = CreateFocusedStableKeyEncodedWritePlan();
        var tablePlans = encoded.TablePlansInDependencyOrder.ToArray();

        tablePlans[schoolAddressIndex] = tablePlans[schoolAddressIndex] with
        {
            DeleteByParentSql = "DELETE FROM schoolAddress WHERE DocumentId = @documentId;",
        };

        var mutated = encoded with { TablePlansInDependencyOrder = [.. tablePlans] };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Be("tablePlanDto");
        exception.Message.Should().Contain(nameof(TableWritePlanDto.CollectionMergePlan));
        exception.Message.Should().Contain(nameof(TableWritePlanDto.DeleteByParentSql));
    }

    [Test]
    public void It_should_fail_fast_when_collection_merge_semantic_identity_bindings_are_empty()
    {
        var (model, encoded, schoolAddressIndex) = CreateFocusedStableKeyEncodedWritePlan();
        var tablePlans = encoded.TablePlansInDependencyOrder.ToArray();

        tablePlans[schoolAddressIndex] = tablePlans[schoolAddressIndex] with
        {
            CollectionMergePlan = tablePlans[schoolAddressIndex].CollectionMergePlan! with
            {
                SemanticIdentityBindings = [],
            },
        };

        var mutated = encoded with { TablePlansInDependencyOrder = [.. tablePlans] };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Be("tablePlanDto");
        exception.Message.Should().Contain(nameof(CollectionMergePlanDto.SemanticIdentityBindings));
        exception.Message.Should().Contain("must be non-empty");
    }

    [Test]
    public void It_should_fail_fast_when_collection_merge_semantic_identity_binding_index_is_out_of_range()
    {
        var (model, encoded, schoolAddressIndex) = CreateFocusedStableKeyEncodedWritePlan();
        var tablePlans = encoded.TablePlansInDependencyOrder.ToArray();
        var semanticIdentityBindings = tablePlans[schoolAddressIndex]
            .CollectionMergePlan!.SemanticIdentityBindings.ToArray();

        semanticIdentityBindings[0] = semanticIdentityBindings[0] with { BindingIndex = 999 };

        tablePlans[schoolAddressIndex] = tablePlans[schoolAddressIndex] with
        {
            CollectionMergePlan = tablePlans[schoolAddressIndex].CollectionMergePlan! with
            {
                SemanticIdentityBindings = [.. semanticIdentityBindings],
            },
        };

        var mutated = encoded with { TablePlansInDependencyOrder = [.. tablePlans] };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        exception.ParamName.Should().Contain(nameof(CollectionMergePlanDto.SemanticIdentityBindings));
        exception.Message.Should().Contain("out of range");
    }

    [Test]
    public void It_should_fail_fast_when_collection_merge_stable_row_identity_binding_index_is_out_of_range()
    {
        var (model, encoded, schoolAddressIndex) = CreateFocusedStableKeyEncodedWritePlan();
        var tablePlans = encoded.TablePlansInDependencyOrder.ToArray();

        tablePlans[schoolAddressIndex] = tablePlans[schoolAddressIndex] with
        {
            CollectionMergePlan = tablePlans[schoolAddressIndex].CollectionMergePlan! with
            {
                StableRowIdentityBindingIndex = 999,
            },
        };

        var mutated = encoded with { TablePlansInDependencyOrder = [.. tablePlans] };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        exception.ParamName.Should().Contain(nameof(CollectionMergePlanDto.StableRowIdentityBindingIndex));
        exception.Message.Should().Contain("out of range");
    }

    [Test]
    public void It_should_fail_fast_when_collection_merge_ordinal_binding_index_is_out_of_range()
    {
        var (model, encoded, schoolAddressIndex) = CreateFocusedStableKeyEncodedWritePlan();
        var tablePlans = encoded.TablePlansInDependencyOrder.ToArray();

        tablePlans[schoolAddressIndex] = tablePlans[schoolAddressIndex] with
        {
            CollectionMergePlan = tablePlans[schoolAddressIndex].CollectionMergePlan! with
            {
                OrdinalBindingIndex = 999,
            },
        };

        var mutated = encoded with { TablePlansInDependencyOrder = [.. tablePlans] };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        exception.ParamName.Should().Contain(nameof(CollectionMergePlanDto.OrdinalBindingIndex));
        exception.Message.Should().Contain("out of range");
    }

    [Test]
    public void It_should_fail_fast_when_collection_merge_compare_binding_index_is_out_of_range()
    {
        var (model, encoded, schoolAddressIndex) = CreateFocusedStableKeyEncodedWritePlan();
        var tablePlans = encoded.TablePlansInDependencyOrder.ToArray();

        tablePlans[schoolAddressIndex] = tablePlans[schoolAddressIndex] with
        {
            CollectionMergePlan = tablePlans[schoolAddressIndex].CollectionMergePlan! with
            {
                CompareBindingIndexesInOrder = [999],
            },
        };

        var mutated = encoded with { TablePlansInDependencyOrder = [.. tablePlans] };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        exception.ParamName.Should().Contain(nameof(CollectionMergePlanDto.CompareBindingIndexesInOrder));
        exception.Message.Should().Contain("out of range");
    }

    [Test]
    public void It_should_fail_fast_when_collection_key_preallocation_binding_index_is_out_of_range()
    {
        var (model, encoded, schoolAddressIndex) = CreateFocusedStableKeyEncodedWritePlan();
        var tablePlans = encoded.TablePlansInDependencyOrder.ToArray();

        tablePlans[schoolAddressIndex] = tablePlans[schoolAddressIndex] with
        {
            CollectionKeyPreallocationPlan = tablePlans[
                schoolAddressIndex
            ].CollectionKeyPreallocationPlan! with
            {
                BindingIndex = 999,
            },
        };

        var mutated = encoded with { TablePlansInDependencyOrder = [.. tablePlans] };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        exception.ParamName.Should().Contain(nameof(CollectionKeyPreallocationPlanDto.BindingIndex));
        exception.Message.Should().Contain("out of range");
    }

    [Test]
    public void It_should_fail_fast_when_collection_merge_plan_is_missing_collection_key_preallocation_metadata()
    {
        var (model, encoded, schoolAddressIndex) = CreateFocusedStableKeyEncodedWritePlan();
        var tablePlans = encoded.TablePlansInDependencyOrder.ToArray();

        tablePlans[schoolAddressIndex] = tablePlans[schoolAddressIndex] with
        {
            CollectionKeyPreallocationPlan = null,
        };

        var mutated = encoded with { TablePlansInDependencyOrder = [.. tablePlans] };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Be(nameof(TableWritePlan.CollectionKeyPreallocationPlan));
        exception.Message.Should().Contain(nameof(TableWritePlan.CollectionMergePlan));
        exception.Message.Should().Contain(nameof(TableWritePlan.CollectionKeyPreallocationPlan));
    }

    [Test]
    public void It_should_fail_fast_when_collection_merge_stable_row_identity_binding_index_does_not_match_collection_key_preallocation_binding_index()
    {
        var (model, encoded, schoolAddressIndex) = CreateFocusedStableKeyEncodedWritePlan();
        var tablePlans = encoded.TablePlansInDependencyOrder.ToArray();
        var tablePlan = tablePlans[schoolAddressIndex];
        var stableRowIdentityBindingIndex = tablePlan.CollectionMergePlan!.StableRowIdentityBindingIndex;

        tablePlans[schoolAddressIndex] = tablePlan with
        {
            CollectionKeyPreallocationPlan = tablePlan.CollectionKeyPreallocationPlan! with
            {
                BindingIndex = stableRowIdentityBindingIndex is 0 ? 1 : 0,
            },
        };

        var mutated = encoded with { TablePlansInDependencyOrder = [.. tablePlans] };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Contain(nameof(CollectionKeyPreallocationPlan.BindingIndex));
        exception.Message.Should().Contain(nameof(CollectionMergePlan.StableRowIdentityBindingIndex));
        exception.Message.Should().Contain(nameof(CollectionKeyPreallocationPlan.BindingIndex));
    }

    [Test]
    public void It_should_fail_fast_when_collection_merge_stable_row_identity_binding_column_does_not_match_collection_key_preallocation_column_name()
    {
        var (model, encoded) = CreateEncodedWritePlanWithAlternateCollectionKeyBinding();
        var tablePlan = encoded.TablePlansInDependencyOrder[0];
        var columnBindings = tablePlan.ColumnBindings.ToArray();
        var stableRowIdentityBindingIndex = tablePlan.CollectionMergePlan!.StableRowIdentityBindingIndex;

        columnBindings[stableRowIdentityBindingIndex] = columnBindings[stableRowIdentityBindingIndex] with
        {
            ColumnName = "AlternateCollectionItemId",
        };

        var mutated = encoded with
        {
            TablePlansInDependencyOrder = [tablePlan with { ColumnBindings = [.. columnBindings] }],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<ArgumentException>().Which;
        exception.ParamName.Should().Contain(nameof(CollectionKeyPreallocationPlan.ColumnName));
        exception.Message.Should().Contain(nameof(CollectionMergePlan.StableRowIdentityBindingIndex));
        exception.Message.Should().Contain(nameof(CollectionKeyPreallocationPlan.ColumnName));
        exception.Message.Should().Contain("AlternateCollectionItemId");
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

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("Decoded read plan for resource");
        exception
            .Message.Should()
            .Contain(
                "descriptor projection plan at index '0' result shape must expose DescriptorId at ordinal '0' and Uri at ordinal '1'"
            );
    }

    [Test]
    public void It_should_fail_fast_when_descriptor_projection_result_shape_is_null()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var descriptorPlan = encoded.DescriptorProjectionPlansInOrder[0];
        var mutated = encoded with
        {
            DescriptorProjectionPlansInOrder = [descriptorPlan with { ResultShape = null! }],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<ArgumentNullException>().Which;
        exception
            .ParamName.Should()
            .Be(
                $"{nameof(ResourceReadPlanDto.DescriptorProjectionPlansInOrder)}[0].{nameof(DescriptorProjectionPlanDto.ResultShape)}"
            );
    }

    [Test]
    public void It_should_fail_fast_when_reference_identity_projection_binding_count_does_not_match_model()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var projectionTablePlan = encoded.ReferenceIdentityProjectionPlansInDependencyOrder[0];
        var mutated = encoded with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    BindingsInOrder = [],
                },
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("Decoded read plan for resource");
        exception.Message.Should().Contain("reference identity projection table");
        exception.Message.Should().Contain("binding count '0'");
        exception.Message.Should().Contain("authoritative DocumentReferenceBindings count '2'");
    }

    [Test]
    public void It_should_fail_fast_when_reference_identity_projection_table_is_duplicated()
    {
        var model = CreateInterleavedReferenceProjectionModel(rootBindingFirst: false);
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(model);
        var encoded = NormalizedPlanContractCodec.Encode(readPlan);
        var projectionTablePlans = encoded.ReferenceIdentityProjectionPlansInDependencyOrder.ToArray();
        var mutated = encoded with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlans[0],
                projectionTablePlans[0],
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("Decoded read plan for resource");
        exception.Message.Should().Contain("reference identity projection includes duplicate table");
    }

    [Test]
    public void It_should_fail_fast_when_descriptor_projection_plan_has_no_sources()
    {
        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var descriptorPlan = encoded.DescriptorProjectionPlansInOrder[0];
        var mutated = encoded with
        {
            DescriptorProjectionPlansInOrder = [descriptorPlan with { SourcesInOrder = [] }],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("Decoded read plan for resource");
        exception
            .Message.Should()
            .Contain("descriptor projection plan at index '0' must contain at least one source");
        exception.Message.Should().Contain("contiguous slice of authoritative DescriptorEdgeSources");
    }

    [Test]
    public void It_should_fail_with_the_same_projection_contract_reason_as_direct_validation_when_a_descriptor_projection_source_is_duplicated()
    {
        var mutatedReadPlan = CreateReadPlanWithDuplicatedDescriptorProjectionSource(
            _readPlan,
            sourceIndex: 0,
            targetIndex: 1
        );
        var expectedReason = GetProjectionValidationFailureReason(mutatedReadPlan);

        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var descriptorPlan = encoded.DescriptorProjectionPlansInOrder[0];
        var sources = descriptorPlan.SourcesInOrder.ToArray();

        sources[1] = sources[0];

        var mutated = encoded with
        {
            DescriptorProjectionPlansInOrder = [descriptorPlan with { SourcesInOrder = [.. sources] }],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        GetDecodedProjectionValidationFailureReason(exception.Message).Should().Be(expectedReason);
    }

    [Test]
    public void It_should_fail_with_the_same_projection_contract_reason_as_direct_validation_when_descriptor_projection_source_order_is_reordered()
    {
        var mutatedReadPlan = CreateReadPlanWithSwappedDescriptorProjectionSources(_readPlan);
        var expectedReason = GetProjectionValidationFailureReason(mutatedReadPlan);

        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var descriptorPlan = encoded.DescriptorProjectionPlansInOrder[0];
        var sources = descriptorPlan.SourcesInOrder.ToArray();
        var firstSource = sources[0];

        sources[0] = sources[1];
        sources[1] = firstSource;

        var mutated = encoded with
        {
            DescriptorProjectionPlansInOrder = [descriptorPlan with { SourcesInOrder = [.. sources] }],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        GetDecodedProjectionValidationFailureReason(exception.Message).Should().Be(expectedReason);
    }

    [Test]
    public void It_should_fail_with_the_same_projection_contract_reason_as_direct_validation_when_descriptor_projection_plan_order_is_reordered()
    {
        var readPlan = CreateReadPlanWithSplitDescriptorProjectionPlans(CreateReadPlan(_model));
        var mutatedReadPlan = CreateReadPlanWithSwappedDescriptorProjectionPlans(readPlan);
        var expectedReason = GetProjectionValidationFailureReason(mutatedReadPlan);

        var encoded = NormalizedPlanContractCodec.Encode(readPlan);
        var descriptorProjectionPlans = encoded.DescriptorProjectionPlansInOrder.ToArray();
        var firstPlan = descriptorProjectionPlans[0];

        descriptorProjectionPlans[0] = descriptorProjectionPlans[1];
        descriptorProjectionPlans[1] = firstPlan;

        var mutated = encoded with { DescriptorProjectionPlansInOrder = [.. descriptorProjectionPlans] };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        GetDecodedProjectionValidationFailureReason(exception.Message).Should().Be(expectedReason);
    }

    [Test]
    public void It_should_fail_with_the_same_projection_contract_reason_as_direct_validation_when_a_split_descriptor_projection_plan_is_empty()
    {
        var readPlan = CreateReadPlanWithSplitDescriptorProjectionPlans(CreateReadPlan(_model));
        var descriptorProjectionPlans = readPlan.DescriptorProjectionPlansInOrder.ToArray();
        var mutatedReadPlan = readPlan with
        {
            DescriptorProjectionPlansInOrder =
            [
                descriptorProjectionPlans[0],
                descriptorProjectionPlans[1] with
                {
                    SourcesInOrder = [],
                },
            ],
        };
        var expectedReason = GetProjectionValidationFailureReason(mutatedReadPlan);

        var encoded = NormalizedPlanContractCodec.Encode(readPlan);
        var encodedDescriptorProjectionPlans = encoded.DescriptorProjectionPlansInOrder.ToArray();
        var mutated = encoded with
        {
            DescriptorProjectionPlansInOrder =
            [
                encodedDescriptorProjectionPlans[0],
                encodedDescriptorProjectionPlans[1] with
                {
                    SourcesInOrder = [],
                },
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        GetDecodedProjectionValidationFailureReason(exception.Message).Should().Be(expectedReason);
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
        exception.Message.Should().Contain("Decoded read plan for resource");
        exception.Message.Should().Contain("ReferenceObjectPath");
        exception.Message.Should().Contain("$.schoolReference");
        exception.Message.Should().Contain("$.calendarReference");
    }

    [Test]
    public void It_should_fail_with_the_same_projection_contract_reason_as_direct_validation_when_a_reference_identity_projection_binding_is_duplicated()
    {
        var mutatedReadPlan = CreateReadPlanWithDuplicatedReferenceProjectionBinding(
            _readPlan,
            sourceIndex: 0,
            targetIndex: 1
        );
        var expectedReason = GetProjectionValidationFailureReason(mutatedReadPlan);

        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var projectionTablePlan = encoded.ReferenceIdentityProjectionPlansInDependencyOrder[0];
        var bindings = projectionTablePlan.BindingsInOrder.ToArray();

        bindings[1] = bindings[0];

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
        GetDecodedProjectionValidationFailureReason(exception.Message).Should().Be(expectedReason);
    }

    [Test]
    public void It_should_fail_with_the_same_projection_contract_reason_as_direct_validation_when_reference_identity_projection_binding_order_is_reordered()
    {
        var mutatedReadPlan = CreateReadPlanWithSwappedReferenceProjectionBindings(_readPlan);
        var expectedReason = GetProjectionValidationFailureReason(mutatedReadPlan);

        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var projectionTablePlan = encoded.ReferenceIdentityProjectionPlansInDependencyOrder[0];
        var bindings = projectionTablePlan.BindingsInOrder.ToArray();
        var firstBinding = bindings[0];

        bindings[0] = bindings[1];
        bindings[1] = firstBinding;

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
        GetDecodedProjectionValidationFailureReason(exception.Message).Should().Be(expectedReason);
    }

    [Test]
    public void It_should_fail_with_the_same_projection_contract_reason_as_direct_validation_when_reference_projection_table_plan_order_is_reordered()
    {
        var model = CreateInterleavedReferenceProjectionModel(rootBindingFirst: false);
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(model);
        var mutatedReadPlan = CreateReadPlanWithSwappedReferenceProjectionTablePlans(readPlan);
        var expectedReason = GetProjectionValidationFailureReason(mutatedReadPlan);

        var encoded = NormalizedPlanContractCodec.Encode(readPlan);
        var projectionTablePlans = encoded.ReferenceIdentityProjectionPlansInDependencyOrder.ToArray();
        var firstTablePlan = projectionTablePlans[0];

        projectionTablePlans[0] = projectionTablePlans[1];
        projectionTablePlans[1] = firstTablePlan;

        var mutated = encoded with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder = [.. projectionTablePlans],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        GetDecodedProjectionValidationFailureReason(exception.Message).Should().Be(expectedReason);
    }

    [Test]
    public void It_should_fail_with_the_same_projection_contract_reason_as_direct_validation_when_an_extra_reference_projection_table_plan_is_appended()
    {
        var model = CreateInterleavedReferenceProjectionModel(rootBindingFirst: false);
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(model);
        var mutatedReadPlan = CreateReadPlanWithAppendedReferenceProjectionTablePlan(
            readPlan,
            sourceIndex: 1
        );
        var expectedReason = GetProjectionValidationFailureReason(mutatedReadPlan);

        var encoded = NormalizedPlanContractCodec.Encode(readPlan);
        var projectionTablePlans = encoded.ReferenceIdentityProjectionPlansInDependencyOrder.ToArray();
        var mutated = encoded with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                .. projectionTablePlans,
                projectionTablePlans[1],
            ],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        GetDecodedProjectionValidationFailureReason(exception.Message).Should().Be(expectedReason);
    }

    [Test]
    public void It_should_fail_with_the_same_projection_contract_reason_as_direct_validation_when_reference_identity_component_is_mismatched()
    {
        var mutatedReadPlan = CreateReadPlanWithReferenceIdentityComponent(
            _readPlan,
            isIdentityComponent: false
        );
        var expectedReason = GetProjectionValidationFailureReason(mutatedReadPlan);

        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var projectionTablePlan = encoded.ReferenceIdentityProjectionPlansInDependencyOrder[0];
        var bindings = projectionTablePlan.BindingsInOrder.ToArray();

        bindings[0] = bindings[0] with { IsIdentityComponent = false };

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
        GetDecodedProjectionValidationFailureReason(exception.Message).Should().Be(expectedReason);
    }

    [Test]
    public void It_should_fail_with_the_same_projection_contract_reason_as_direct_validation_when_reference_projection_target_resource_is_mismatched()
    {
        var mutatedReadPlan = CreateReadPlanWithReferenceTargetResource(
            _readPlan,
            new QualifiedResourceName("Ed-Fi", "Calendar")
        );
        var expectedReason = GetProjectionValidationFailureReason(mutatedReadPlan);

        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var projectionTablePlan = encoded.ReferenceIdentityProjectionPlansInDependencyOrder[0];
        var bindings = projectionTablePlan.BindingsInOrder.ToArray();

        bindings[0] = bindings[0] with { TargetResource = new QualifiedResourceNameDto("Ed-Fi", "Calendar") };

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
        GetDecodedProjectionValidationFailureReason(exception.Message).Should().Be(expectedReason);
    }

    [Test]
    public void It_should_fail_with_the_same_projection_contract_reason_as_direct_validation_when_descriptor_projection_source_target_resource_is_mismatched()
    {
        var mutatedReadPlan = CreateReadPlanWithDescriptorProjectionSourceDescriptorResource(
            _readPlan,
            new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"),
            sourceIndex: 0
        );
        var expectedReason = GetProjectionValidationFailureReason(mutatedReadPlan);

        var encoded = NormalizedPlanContractCodec.Encode(_readPlan);
        var descriptorPlan = encoded.DescriptorProjectionPlansInOrder[0];
        var sources = descriptorPlan.SourcesInOrder.ToArray();

        sources[0] = sources[0] with
        {
            DescriptorResource = new QualifiedResourceNameDto("Ed-Fi", "ProgramTypeDescriptor"),
        };

        var mutated = encoded with
        {
            DescriptorProjectionPlansInOrder = [descriptorPlan with { SourcesInOrder = [.. sources] }],
        };

        var act = () => NormalizedPlanContractCodec.Decode(mutated, _model);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        GetDecodedProjectionValidationFailureReason(exception.Message).Should().Be(expectedReason);
    }

    [Test]
    public void It_should_fail_with_the_same_projection_contract_reason_as_direct_validation_when_reference_identity_projection_binding_resolves_to_zero_logical_fields()
    {
        var model = CreateModelWithEmptySchoolReferenceIdentityBindings();
        var readPlan = CreateReadPlan(model);
        var projectionTablePlan = readPlan.ReferenceIdentityProjectionPlansInDependencyOrder.Single();
        var bindings = projectionTablePlan.BindingsInOrder.ToArray();

        bindings[0] = bindings[0] with { IdentityFieldOrdinalsInOrder = [] };

        var mutatedReadPlan = readPlan with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    BindingsInOrder = [.. bindings],
                },
            ],
        };
        var expectedReason = GetProjectionValidationFailureReason(mutatedReadPlan);
        var encoded = NormalizedPlanContractCodec.Encode(mutatedReadPlan);

        var act = () => NormalizedPlanContractCodec.Decode(encoded, model);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        GetDecodedProjectionValidationFailureReason(exception.Message).Should().Be(expectedReason);
    }

    [Test]
    public void It_should_fail_with_the_same_projection_contract_reason_as_direct_validation_when_a_grouped_reference_projection_field_is_duplicated()
    {
        var model = CreateGroupedReferenceProjectionResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(model);
        var mutatedReadPlan = CreateReadPlanWithDuplicatedReferenceProjectionField(
            readPlan,
            sourceIndex: 0,
            targetIndex: 1
        );
        var expectedReason = GetProjectionValidationFailureReason(mutatedReadPlan);
        var mutated = CreateEncodedReadPlanWithMutatedSingleReferenceProjectionFields(
            readPlan,
            identityFieldOrdinals =>
            {
                identityFieldOrdinals[1] = identityFieldOrdinals[0];
                return identityFieldOrdinals;
            }
        );

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        GetDecodedProjectionValidationFailureReason(exception.Message).Should().Be(expectedReason);
    }

    [Test]
    public void It_should_fail_with_the_same_projection_contract_reason_as_direct_validation_when_a_grouped_reference_projection_field_is_omitted()
    {
        var model = CreateGroupedReferenceProjectionResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(model);
        var mutatedReadPlan = CreateReadPlanWithOmittedReferenceProjectionField(
            readPlan,
            omittedFieldIndex: 1
        );
        var expectedReason = GetProjectionValidationFailureReason(mutatedReadPlan);
        var mutated = CreateEncodedReadPlanWithMutatedSingleReferenceProjectionFields(
            readPlan,
            identityFieldOrdinals => [identityFieldOrdinals[0]]
        );

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        GetDecodedProjectionValidationFailureReason(exception.Message).Should().Be(expectedReason);
    }

    [Test]
    public void It_should_fail_with_the_same_projection_contract_reason_as_direct_validation_when_grouped_reference_projection_field_order_is_reordered()
    {
        var model = CreateGroupedReferenceProjectionResourceModel();
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(model);
        var mutatedReadPlan = CreateReadPlanWithSwappedReferenceProjectionFields(readPlan);
        var expectedReason = GetProjectionValidationFailureReason(mutatedReadPlan);
        var mutated = CreateEncodedReadPlanWithMutatedSingleReferenceProjectionFields(
            readPlan,
            identityFieldOrdinals =>
            {
                var firstField = identityFieldOrdinals[0];
                identityFieldOrdinals[0] = identityFieldOrdinals[1];
                identityFieldOrdinals[1] = firstField;
                return identityFieldOrdinals;
            }
        );

        var act = () => NormalizedPlanContractCodec.Decode(mutated, model);

        var exception = act.Should().Throw<InvalidOperationException>().Which;
        GetDecodedProjectionValidationFailureReason(exception.Message).Should().Be(expectedReason);
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

    private static RelationalResourceModel CreateModelWithEmptySchoolReferenceIdentityBindings()
    {
        var model = CreateModel();
        var bindings = model.DocumentReferenceBindings.ToArray();

        bindings[0] = bindings[0] with { IdentityBindings = [] };

        return model with
        {
            DocumentReferenceBindings = [.. bindings],
        };
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
            ],
            CollectionMergePlan: null
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
                        new ReferenceIdentityProjectionBinding(
                            IsIdentityComponent: false,
                            ReferenceObjectPath: Path("$.calendarReference"),
                            TargetResource: new QualifiedResourceName("Ed-Fi", "Calendar"),
                            FkColumnOrdinal: 5,
                            IdentityFieldOrdinalsInOrder:
                            [
                                new ReferenceIdentityProjectionFieldOrdinal(
                                    ReferenceJsonPath: Path("$.calendarReference.calendarCode"),
                                    ColumnOrdinal: 6
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
                        new DescriptorProjectionSource(
                            DescriptorValuePath: Path("$.schoolYearDescriptor"),
                            Table: table,
                            DescriptorResource: new QualifiedResourceName(
                                "Ed-Fi",
                                "SchoolYearTypeDescriptor"
                            ),
                            DescriptorIdColumnOrdinal: 10
                        ),
                    ]
                ),
            ]
        );
    }

    private static RelationalResourceModel CreateInterleavedReferenceProjectionModel(bool rootBindingFirst)
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentReferenceGrouping"),
            JsonScope: Path("$"),
            Key: new TableKey(
                ConstraintName: "PK_StudentReferenceGrouping",
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
            ],
            Constraints: []
        );

        var childTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentAddressReferenceGrouping"),
            JsonScope: Path("$.addresses[*]"),
            Key: new TableKey(
                ConstraintName: "PK_StudentAddressReferenceGrouping",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("Ordinal"), ColumnKind.Ordinal),
                ]
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
                    ColumnName: new DbColumnName("Calendar_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: Path("$.addresses[*].calendarReference"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Calendar")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Calendar_RefCalendarCode"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    IsNullable: true,
                    SourceJsonPath: Path("$.addresses[*].calendarReference.calendarCode"),
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Session_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: Path("$.addresses[*].sessionReference"),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "Session")
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("Session_RefSessionName"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 128),
                    IsNullable: true,
                    SourceJsonPath: Path("$.addresses[*].sessionReference.sessionName"),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        var rootBinding = new DocumentReferenceBinding(
            IsIdentityComponent: true,
            ReferenceObjectPath: Path("$.schoolReference"),
            Table: rootTable.Table,
            FkColumn: new DbColumnName("School_DocumentId"),
            TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    ReferenceJsonPath: Path("$.schoolReference.schoolId"),
                    Column: new DbColumnName("School_RefSchoolId")
                ),
            ]
        );
        var calendarBinding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: Path("$.addresses[*].calendarReference"),
            Table: childTable.Table,
            FkColumn: new DbColumnName("Calendar_DocumentId"),
            TargetResource: new QualifiedResourceName("Ed-Fi", "Calendar"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    ReferenceJsonPath: Path("$.addresses[*].calendarReference.calendarCode"),
                    Column: new DbColumnName("Calendar_RefCalendarCode")
                ),
            ]
        );
        var sessionBinding = new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: Path("$.addresses[*].sessionReference"),
            Table: childTable.Table,
            FkColumn: new DbColumnName("Session_DocumentId"),
            TargetResource: new QualifiedResourceName("Ed-Fi", "Session"),
            IdentityBindings:
            [
                new ReferenceIdentityBinding(
                    ReferenceJsonPath: Path("$.addresses[*].sessionReference.sessionName"),
                    Column: new DbColumnName("Session_RefSessionName")
                ),
            ]
        );

        var documentReferenceBindings = rootBindingFirst
            ? new[] { rootBinding, calendarBinding, sessionBinding }
            : new[] { calendarBinding, rootBinding, sessionBinding };

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "StudentReferenceGrouping"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable, childTable],
            DocumentReferenceBindings: documentReferenceBindings,
            DescriptorEdgeSources: []
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

    private static ResourceReadPlanDto CreateEncodedReadPlanWithMutatedSingleReferenceProjectionFields(
        ResourceReadPlan readPlan,
        Func<
            ReferenceIdentityProjectionFieldOrdinalDto[],
            ReferenceIdentityProjectionFieldOrdinalDto[]
        > mutate
    )
    {
        var encoded = NormalizedPlanContractCodec.Encode(readPlan);
        var projectionTablePlan = encoded.ReferenceIdentityProjectionPlansInDependencyOrder.Single();
        var binding = projectionTablePlan.BindingsInOrder.Single();
        var mutatedFieldOrdinals = mutate(binding.IdentityFieldOrdinalsInOrder.ToArray());

        return encoded with
        {
            ReferenceIdentityProjectionPlansInDependencyOrder =
            [
                projectionTablePlan with
                {
                    BindingsInOrder =
                    [
                        binding with
                        {
                            IdentityFieldOrdinalsInOrder = [.. mutatedFieldOrdinals],
                        },
                    ],
                },
            ],
        };
    }

    private static RelationalResourceModel CreateGroupedReferenceProjectionResourceModel()
    {
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var schoolReferencePath = Path("$.schoolReference");
        var schoolIdPath = Path("$.schoolReference.schoolId");
        var schoolYearPath = Path("$.schoolReference.schoolYear");
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "StudentGroupedReferenceProjection"),
            JsonScope: Path("$"),
            Key: new TableKey(
                ConstraintName: "PK_StudentGroupedReferenceProjection",
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
                    ColumnName: new DbColumnName("School_DocumentId"),
                    Kind: ColumnKind.DocumentFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: schoolReferencePath,
                    TargetResource: schoolResource
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolIdCanonical"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_RefSchoolIdSecondary"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: schoolIdPath,
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolIdCanonical"),
                        PresenceColumn: new DbColumnName("School_DocumentId")
                    )
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_RefSchoolYear"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: schoolYearPath,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("School_RefSchoolIdPrimary"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: schoolIdPath,
                    TargetResource: null,
                    Storage: new ColumnStorage.UnifiedAlias(
                        CanonicalColumn: new DbColumnName("SchoolIdCanonical"),
                        PresenceColumn: new DbColumnName("School_DocumentId")
                    )
                ),
            ],
            Constraints: []
        )
        {
            KeyUnificationClasses =
            [
                new KeyUnificationClass(
                    CanonicalColumn: new DbColumnName("SchoolIdCanonical"),
                    MemberPathColumns:
                    [
                        new DbColumnName("School_RefSchoolIdSecondary"),
                        new DbColumnName("School_RefSchoolIdPrimary"),
                    ]
                ),
            ],
        };

        return new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "StudentGroupedReferenceProjection"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings:
            [
                new DocumentReferenceBinding(
                    IsIdentityComponent: true,
                    ReferenceObjectPath: schoolReferencePath,
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("School_DocumentId"),
                    TargetResource: schoolResource,
                    IdentityBindings:
                    [
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: schoolIdPath,
                            Column: new DbColumnName("School_RefSchoolIdSecondary")
                        ),
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: schoolYearPath,
                            Column: new DbColumnName("School_RefSchoolYear")
                        ),
                        new ReferenceIdentityBinding(
                            ReferenceJsonPath: schoolIdPath,
                            Column: new DbColumnName("School_RefSchoolIdPrimary")
                        ),
                    ]
                ),
            ],
            DescriptorEdgeSources: []
        );
    }

    private static RelationalResourceModel CreateModelWithPermutedDocumentReferenceBindings(
        RelationalResourceModel model
    )
    {
        return model with { DocumentReferenceBindings = [.. model.DocumentReferenceBindings.Reverse()] };
    }

    private static (
        RelationalResourceModel Model,
        ResourceWritePlanDto Encoded,
        int SchoolAddressIndex
    ) CreateFocusedStableKeyEncodedWritePlan()
    {
        var model = CreateFocusedStableKeyFixtureResourceModel(SqlDialect.Pgsql);
        var encoded = NormalizedPlanContractCodec.Encode(
            new WritePlanCompiler(SqlDialect.Pgsql).Compile(model)
        );
        var schoolAddressIndex = Array.FindIndex(
            encoded.TablePlansInDependencyOrder.ToArray(),
            static tablePlan => string.Equals(tablePlan.Table.Name, "SchoolAddress", StringComparison.Ordinal)
        );

        schoolAddressIndex.Should().BeGreaterOrEqualTo(0);

        return (model, encoded, schoolAddressIndex);
    }

    private static (
        RelationalResourceModel Model,
        ResourceWritePlanDto Encoded
    ) CreateEncodedWritePlanWithAlternateCollectionKeyBinding()
    {
        var addressTypePath = new JsonPathExpression(
            "$.addressType",
            [new JsonPathSegment.Property("addressType")]
        );
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolAddress"),
            new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
            new TableKey(
                "PK_SchoolAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
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
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("AlternateCollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("AddressType"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 32),
                    IsNullable: false,
                    SourceJsonPath: addressTypePath,
                    TargetResource: null
                ),
            ],
            []
        );

        var model = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: tableModel,
            TablesInDependencyOrder: [tableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        var writePlan = new ResourceWritePlan(
            model,
            [
                new TableWritePlan(
                    TableModel: tableModel,
                    InsertSql: "INSERT SQL",
                    UpdateSql: null,
                    DeleteByParentSql: null,
                    BulkInsertBatching: new BulkInsertBatchingInfo(100, 5, 2100),
                    ColumnBindings:
                    [
                        new WriteColumnBinding(
                            Column: tableModel.Columns[0],
                            Source: new WriteValueSource.DocumentId(),
                            ParameterName: "documentId"
                        ),
                        new WriteColumnBinding(
                            Column: tableModel.Columns[1],
                            Source: new WriteValueSource.Precomputed(),
                            ParameterName: "collectionItemId"
                        ),
                        new WriteColumnBinding(
                            Column: tableModel.Columns[2],
                            Source: new WriteValueSource.Precomputed(),
                            ParameterName: "alternateCollectionItemId"
                        ),
                        new WriteColumnBinding(
                            Column: tableModel.Columns[3],
                            Source: new WriteValueSource.Ordinal(),
                            ParameterName: "ordinal"
                        ),
                        new WriteColumnBinding(
                            Column: tableModel.Columns[4],
                            Source: new WriteValueSource.Scalar(
                                addressTypePath,
                                new RelationalScalarType(ScalarKind.String, MaxLength: 32)
                            ),
                            ParameterName: "addressType"
                        ),
                    ],
                    KeyUnificationPlans: [],
                    CollectionMergePlan: new CollectionMergePlan(
                        SemanticIdentityBindings:
                        [
                            new CollectionMergeSemanticIdentityBinding(
                                RelativePath: addressTypePath,
                                BindingIndex: 4
                            ),
                        ],
                        StableRowIdentityBindingIndex: 1,
                        UpdateByStableRowIdentitySql: "UPDATE COLLECTION SQL",
                        DeleteByStableRowIdentitySql: "DELETE COLLECTION SQL",
                        OrdinalBindingIndex: 3,
                        CompareBindingIndexesInOrder: [1, 3, 4]
                    ),
                    CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                        ColumnName: new DbColumnName("CollectionItemId"),
                        BindingIndex: 1
                    )
                ),
            ]
        );

        return (model, NormalizedPlanContractCodec.Encode(writePlan));
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

    private static string GetProjectionValidationFailureReason(ResourceReadPlan readPlan)
    {
        var act = () =>
            ReadPlanProjectionContractValidator.ValidateOrThrow(
                readPlan,
                reason => new InvalidOperationException(reason)
            );

        return act.Should().Throw<InvalidOperationException>().Which.Message;
    }

    private static string GetDecodedProjectionValidationFailureReason(string message)
    {
        const string prefix = "Decoded read plan for resource '";
        const string separator = "' has invalid projection metadata. ";

        message.Should().StartWith(prefix);
        message.Should().EndWith(".");

        var separatorIndex = message.IndexOf(separator, StringComparison.Ordinal);
        separatorIndex.Should().BeGreaterOrEqualTo(prefix.Length);

        return message[(separatorIndex + separator.Length)..^1];
    }
}
