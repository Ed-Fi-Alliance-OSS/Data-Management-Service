// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_WritePlanCompiler_BindingsAndSources : WritePlanCompilerTestBase
{
    [Test]
    public void It_should_compile_stored_column_bindings_in_model_order_with_deterministic_parameter_names()
    {
        var writePlan = new WritePlanCompiler(SqlDialect.Pgsql).Compile(_supportedRootOnlyModel);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single();

        tablePlan
            .ColumnBindings.Select(binding => binding.Column.ColumnName.Value)
            .Should()
            .Equal("DocumentId", "SchoolYear", "LocalEducationAgencyId");

        tablePlan
            .ColumnBindings.Select(binding => binding.ParameterName)
            .Should()
            .Equal("documentId", "schoolYear", "localEducationAgencyId");

        tablePlan.ColumnBindings[0].Source.Should().BeOfType<WriteValueSource.DocumentId>();
        tablePlan.ColumnBindings[1].Source.Should().BeOfType<WriteValueSource.Scalar>();
        tablePlan.ColumnBindings[2].Source.Should().BeOfType<WriteValueSource.Scalar>();

        tablePlan.UpdateSql.Should().NotBeNull();
        tablePlan.DeleteByParentSql.Should().BeNull();
        tablePlan.KeyUnificationPlans.Should().BeEmpty();

        tablePlan.BulkInsertBatching.ParametersPerRow.Should().Be(3);
        tablePlan.BulkInsertBatching.MaxRowsPerBatch.Should().Be(1000);
        tablePlan.BulkInsertBatching.MaxParametersPerCommand.Should().Be(65535);
    }

    [Test]
    public void It_should_exclude_non_writable_stored_non_key_columns_from_bindings_and_sql_column_lists()
    {
        var model = CreateSupportedRootOnlyModelWithNonWritableSchoolYear();
        var tablePlan = new WritePlanCompiler(SqlDialect.Pgsql)
            .Compile(model)
            .TablePlansInDependencyOrder.Single();

        tablePlan
            .ColumnBindings.Select(static binding => binding.Column.ColumnName.Value)
            .Should()
            .Equal("DocumentId", "LocalEducationAgencyId");

        tablePlan
            .InsertSql.Should()
            .Be(
                """
                INSERT INTO "edfi"."Student"
                (
                    "DocumentId",
                    "LocalEducationAgencyId"
                )
                VALUES
                (
                    @documentId,
                    @localEducationAgencyId
                )
                ;

                """
            );

        tablePlan
            .UpdateSql.Should()
            .Be(
                """
                UPDATE "edfi"."Student"
                SET
                    "LocalEducationAgencyId" = @localEducationAgencyId
                WHERE
                    ("DocumentId" = @documentId)
                ;

                """
            );
    }

    [Test]
    public void It_should_keep_required_key_and_key_unification_precomputed_targets_when_marked_non_writable()
    {
        var model = CreateRootOnlyModelWithNonWritableStoredKeyAndPrecomputedTargets();
        var tablePlan = new WritePlanCompiler(SqlDialect.Pgsql)
            .Compile(model)
            .TablePlansInDependencyOrder.Single();

        tablePlan
            .ColumnBindings.Select(static binding => binding.Column.ColumnName.Value)
            .Should()
            .Equal(
                "DocumentId",
                "SchoolYearCanonical",
                "SchoolYearTypeDescriptorIdCanonical",
                "SchoolYearTypeDescriptorSecondary_Present"
            );

        tablePlan.ColumnBindings[0].Source.Should().BeOfType<WriteValueSource.DocumentId>();
        tablePlan.ColumnBindings[1].Source.Should().BeOfType<WriteValueSource.Precomputed>();
        tablePlan.ColumnBindings[2].Source.Should().BeOfType<WriteValueSource.Precomputed>();
        tablePlan.ColumnBindings[3].Source.Should().BeOfType<WriteValueSource.Precomputed>();
    }

    [Test]
    public void It_should_fail_fast_when_writable_null_source_column_is_not_an_explicit_precomputed_target()
    {
        var precomputedColumnModel = CreateRootOnlyModelWithStoredPrecomputedNonKeyColumn();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(precomputedColumnModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for 'edfi.Student': column 'CanonicalSchoolYear' (kind 'Scalar') has null SourceJsonPath but is not an explicitly supported precomputed target. Mark the column IsWritable=false or add a producer plan (for example, key-unification canonical/synthetic presence)."
            );
    }

    [Test]
    public void It_should_compile_each_supported_write_value_source_for_single_table_bindings()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds();

        var writePlan = new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);
        var tablePlan = writePlan.TablePlansInDependencyOrder.Single(tablePlan =>
            tablePlan.TableModel.Table.Equals(new DbTableName(new DbSchemaName("edfi"), "StudentAddress"))
        );

        tablePlan
            .ColumnBindings.Select(binding => binding.Column.ColumnName.Value)
            .Should()
            .Equal(
                "DocumentId",
                "ParentAddressOrdinal",
                "Ordinal",
                "AddressScopeValue",
                "StreetNumber",
                "School_DocumentId",
                "ProgramTypeDescriptorId",
                "CanonicalProgramTypeCode"
            );

        tablePlan
            .ColumnBindings.Select(binding => binding.ParameterName)
            .Should()
            .Equal(
                "documentId",
                "parentAddressOrdinal",
                "ordinal",
                "addressScopeValue",
                "streetNumber",
                "school_DocumentId",
                "programTypeDescriptorId",
                "canonicalProgramTypeCode"
            );

        tablePlan.ColumnBindings[0].Source.Should().BeOfType<WriteValueSource.DocumentId>();
        tablePlan
            .ColumnBindings[1]
            .Source.Should()
            .BeEquivalentTo(new WriteValueSource.ParentKeyPart(Index: 1));
        tablePlan.ColumnBindings[2].Source.Should().BeOfType<WriteValueSource.Ordinal>();
        tablePlan
            .ColumnBindings[3]
            .Source.Should()
            .BeEquivalentTo(
                new WriteValueSource.Scalar(
                    RelativePath: new JsonPathExpression("$", []),
                    Type: new RelationalScalarType(ScalarKind.String)
                )
            );
        tablePlan
            .ColumnBindings[4]
            .Source.Should()
            .BeEquivalentTo(
                new WriteValueSource.Scalar(
                    RelativePath: new JsonPathExpression(
                        "$.streetNumber",
                        [new JsonPathSegment.Property("streetNumber")]
                    ),
                    Type: new RelationalScalarType(ScalarKind.String)
                )
            );
        tablePlan
            .ColumnBindings[5]
            .Source.Should()
            .BeEquivalentTo(new WriteValueSource.DocumentReference(BindingIndex: 0));
        tablePlan
            .ColumnBindings[6]
            .Source.Should()
            .BeEquivalentTo(
                new WriteValueSource.DescriptorReference(
                    DescriptorResource: new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"),
                    RelativePath: new JsonPathExpression(
                        "$.programTypeDescriptor",
                        [new JsonPathSegment.Property("programTypeDescriptor")]
                    ),
                    DescriptorValuePath: new JsonPathExpression(
                        "$.addresses[*].programTypeDescriptor",
                        [
                            new JsonPathSegment.Property("addresses"),
                            new JsonPathSegment.AnyArrayElement(),
                            new JsonPathSegment.Property("programTypeDescriptor"),
                        ]
                    )
                )
            );
        tablePlan.ColumnBindings[7].Source.Should().BeOfType<WriteValueSource.Precomputed>();
    }

    [Test]
    public void It_should_compile_non_unified_propagated_reference_identity_columns_as_reference_derived_sources()
    {
        var model = ReferenceDerivedWritePlanFixture.CreateModel();
        var tablePlan = new WritePlanCompiler(SqlDialect.Pgsql)
            .Compile(model)
            .TablePlansInDependencyOrder.Single();

        tablePlan
            .ColumnBindings.Single(binding =>
                binding.Column.ColumnName.Equals(new DbColumnName("School_RefSchoolYear"))
            )
            .Source.Should()
            .BeEquivalentTo(
                new WriteValueSource.ReferenceDerived(
                    new ReferenceDerivedValueSourceMetadata(
                        BindingIndex: 0,
                        ReferenceObjectPath: new JsonPathExpression(
                            "$.schoolReference",
                            [new JsonPathSegment.Property("schoolReference")]
                        ),
                        ReferenceJsonPath: new JsonPathExpression(
                            "$.schoolReference.schoolYear",
                            [
                                new JsonPathSegment.Property("schoolReference"),
                                new JsonPathSegment.Property("schoolYear"),
                            ]
                        )
                    )
                )
            );
    }

    [Test]
    public void It_should_compile_descriptor_backed_propagated_reference_identity_columns_as_reference_derived_sources()
    {
        var model = ReferenceDerivedWritePlanFixture.CreateDescriptorBackedModel();
        var tablePlan = new WritePlanCompiler(SqlDialect.Pgsql)
            .Compile(model)
            .TablePlansInDependencyOrder.Single();

        tablePlan
            .ColumnBindings.Single(binding =>
                binding.Column.ColumnName.Equals(new DbColumnName("SchoolCategoryDescriptorId"))
            )
            .Source.Should()
            .BeEquivalentTo(
                new WriteValueSource.ReferenceDerived(
                    new ReferenceDerivedValueSourceMetadata(
                        BindingIndex: 0,
                        ReferenceObjectPath: new JsonPathExpression(
                            "$.schoolReference",
                            [new JsonPathSegment.Property("schoolReference")]
                        ),
                        ReferenceJsonPath: new JsonPathExpression(
                            "$.schoolReference.schoolCategoryDescriptor",
                            [
                                new JsonPathSegment.Property("schoolReference"),
                                new JsonPathSegment.Property("schoolCategoryDescriptor"),
                            ]
                        )
                    )
                )
            );
    }

    [Test]
    public void It_should_fail_fast_when_reference_derived_column_source_path_drifts_from_binding_metadata()
    {
        var model = ReferenceDerivedWritePlanFixture.WithColumnSourceJsonPath(
            ReferenceDerivedWritePlanFixture.CreateModel(),
            "School_RefSchoolYear",
            "$.schoolReference.localSchoolYear"
        );
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for 'edfi.ProgramReferenceDerived': reference-derived source mismatch for column 'School_RefSchoolYear'. DbColumnModel.SourceJsonPath '$.schoolReference.localSchoolYear' does not match ReferenceIdentityBinding.ReferenceJsonPath '$.schoolReference.schoolYear'."
            );
    }

    [Test]
    public void It_should_treat_document_suffixed_parent_key_parts_as_parent_key_part_sources()
    {
        var model = CreateRootOnlyModelWithDocumentSuffixedParentKeyPart();
        var tablePlan = new WritePlanCompiler(SqlDialect.Pgsql)
            .Compile(model)
            .TablePlansInDependencyOrder.Single();

        tablePlan
            .ColumnBindings.Single(binding =>
                binding.Column.ColumnName.Equals(new DbColumnName("School_DocumentId"))
            )
            .Source.Should()
            .BeEquivalentTo(new WriteValueSource.ParentKeyPart(Index: 0));
        tablePlan
            .ColumnBindings.Select(static binding => binding.Source)
            .Should()
            .NotContain(source => source is WriteValueSource.DocumentId);
    }

    [Test]
    public void It_should_bind_collection_aligned_extension_scope_locators_from_explicit_identity_metadata()
    {
        var tablePlan = CompileCollectionsNestedExtensionFixtureTablePlan(
            SqlDialect.Pgsql,
            "SchoolExtensionAddress"
        );

        tablePlan
            .ColumnBindings.Select(static binding => binding.Column.ColumnName.Value)
            .Should()
            .Equal("BaseCollectionItemId", "School_DocumentId", "Zone");

        tablePlan
            .ColumnBindings[0]
            .Source.Should()
            .BeEquivalentTo(new WriteValueSource.ParentKeyPart(Index: 0));
        tablePlan.ColumnBindings[1].Source.Should().BeOfType<WriteValueSource.DocumentId>();
        tablePlan
            .ColumnBindings[2]
            .Source.Should()
            .BeEquivalentTo(
                new WriteValueSource.Scalar(
                    RelativePath: new JsonPathExpression("$.zone", [new JsonPathSegment.Property("zone")]),
                    Type: new RelationalScalarType(ScalarKind.String, MaxLength: 20)
                )
            );
    }

    [TestCase("SchoolAddress")]
    [TestCase("SchoolAddressPeriod")]
    [TestCase("SchoolExtensionIntervention")]
    public void It_should_emit_collection_key_preallocation_plan_for_collection_insert_tables(
        string tableName
    )
    {
        var tablePlan = CompileFocusedStableKeyFixtureTablePlan(SqlDialect.Pgsql, tableName);
        var collectionKeyBindingIndex = Array.FindIndex(
            [.. tablePlan.ColumnBindings],
            binding => binding.Column.Kind is ColumnKind.CollectionKey
        );

        collectionKeyBindingIndex.Should().BeGreaterThanOrEqualTo(0);
        tablePlan.CollectionKeyPreallocationPlan.Should().NotBeNull();
        tablePlan
            .CollectionKeyPreallocationPlan!.Should()
            .Be(
                new CollectionKeyPreallocationPlan(
                    ColumnName: new DbColumnName("CollectionItemId"),
                    BindingIndex: collectionKeyBindingIndex
                )
            );
        tablePlan
            .ColumnBindings[collectionKeyBindingIndex]
            .Source.Should()
            .BeOfType<WriteValueSource.Precomputed>();
        tablePlan.InsertSql.Should().Contain("@collectionItemId");
    }

    [Test]
    public void It_should_not_emit_collection_key_preallocation_plan_for_non_collection_tables()
    {
        var tablePlan = new WritePlanCompiler(SqlDialect.Pgsql)
            .Compile(_supportedRootOnlyModel)
            .TablePlansInDependencyOrder.Single();

        tablePlan.CollectionKeyPreallocationPlan.Should().BeNull();
    }

    [Test]
    public void It_should_fail_fast_when_document_reference_binding_is_missing_for_document_fk_column()
    {
        var model = CreateSingleTableModelWithMissingDocumentReferenceBinding();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("No document-reference binding matches 'edfi.StudentAddress.School_DocumentId'.");
    }

    [Test]
    public void It_should_bind_document_fk_as_document_reference_when_source_json_path_is_null()
    {
        var model = CreateSingleTableModelCoveringWriteValueSourceKinds(useNullDocumentFkSourcePath: true);
        var tablePlan = new WritePlanCompiler(SqlDialect.Pgsql)
            .Compile(model)
            .TablePlansInDependencyOrder.Single(tablePlan =>
                tablePlan.TableModel.Table.Equals(new DbTableName(new DbSchemaName("edfi"), "StudentAddress"))
            );

        tablePlan
            .ColumnBindings.Single(binding =>
                binding.Column.ColumnName.Equals(new DbColumnName("School_DocumentId"))
            )
            .Source.Should()
            .BeEquivalentTo(new WriteValueSource.DocumentReference(BindingIndex: 0));
    }

    [Test]
    public void It_should_fail_fast_when_document_reference_binding_is_missing_for_document_fk_column_with_null_source_json_path()
    {
        var model = CreateSingleTableModelWithMissingDocumentReferenceBinding(
            useNullDocumentFkSourcePath: true
        );
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("No document-reference binding matches 'edfi.StudentAddress.School_DocumentId'.");
    }

    [Test]
    public void It_should_fail_fast_when_document_reference_binding_is_duplicated_for_document_fk_column()
    {
        var model = CreateSingleTableModelWithDuplicateDocumentReferenceBinding();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for resource 'Ed-Fi.StudentAddress': duplicate document-reference binding key(s) were found: edfi.StudentAddress.School_DocumentId (count: 2)."
            );
    }

    [Test]
    public void It_should_fail_fast_when_document_reference_binding_is_duplicated_for_document_fk_column_with_null_source_json_path()
    {
        var model = CreateSingleTableModelWithDuplicateDocumentReferenceBinding(
            useNullDocumentFkSourcePath: true
        );
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for resource 'Ed-Fi.StudentAddress': duplicate document-reference binding key(s) were found: edfi.StudentAddress.School_DocumentId (count: 2)."
            );
    }

    [Test]
    public void It_should_fail_fast_when_descriptor_edge_source_is_missing_for_descriptor_fk_column()
    {
        var model = CreateSingleTableModelWithMissingDescriptorEdgeSource();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("No descriptor edge source matches 'edfi.StudentAddress.ProgramTypeDescriptorId'.");
    }

    [Test]
    public void It_should_fail_fast_when_descriptor_source_path_mismatches_descriptor_edge_source_path()
    {
        var model = CreateSingleTableModelWithMismatchedDescriptorSourcePath();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for 'edfi.StudentAddress': descriptor source mismatch for column 'ProgramTypeDescriptorId'. DbColumnModel.SourceJsonPath '$.addresses[*].programTypeCode' does not match DescriptorEdgeSource.DescriptorValuePath '$.addresses[*].programTypeDescriptor'."
            );
    }

    [Test]
    public void It_should_fail_fast_when_descriptor_edge_source_is_duplicated_for_descriptor_fk_column()
    {
        var model = CreateSingleTableModelWithDuplicateDescriptorEdgeSource();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for resource 'Ed-Fi.StudentAddress': duplicate descriptor edge source key(s) were found: edfi.StudentAddress.ProgramTypeDescriptorId (count: 2)."
            );
    }

    [Test]
    public void It_should_fail_fast_when_document_reference_binding_inventory_contains_duplicate_keys_even_when_unreferenced()
    {
        var model = CreateRootOnlyModelWithDuplicateUnusedDocumentReferenceBindingKeys();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for resource 'Ed-Fi.Student': duplicate document-reference binding key(s) were found: edfi.Student.Unused_DocumentId (count: 2)."
            );
    }

    [Test]
    public void It_should_fail_fast_when_descriptor_edge_source_inventory_contains_duplicate_keys_even_when_unreferenced()
    {
        var model = CreateRootOnlyModelWithDuplicateUnusedDescriptorEdgeSourceKeys();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(model);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for resource 'Ed-Fi.Student': duplicate descriptor edge source key(s) were found: edfi.Student.UnusedDescriptorId (count: 2)."
            );
    }

    [Test]
    public void It_should_fail_fast_when_key_column_is_unified_alias()
    {
        var unsupportedModel = CreateRootOnlyModelWithUnifiedAliasKeyColumn();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for 'edfi.Student': key column 'SchoolYearAlias' is UnifiedAlias*"
            );
    }

    [Test]
    public void It_should_fail_fast_when_key_column_does_not_exist_in_table_columns()
    {
        var unsupportedModel = CreateRootOnlyModelWithMissingKeyColumn();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for 'edfi.Student': key column 'MissingSchoolYear' does not exist in table columns."
            );
    }

    [TestCaseSource(
        typeof(SharedInvalidKeyShapeTestCases),
        nameof(SharedInvalidKeyShapeTestCases.ForWritePlanCompiler)
    )]
    public void It_should_preserve_shared_key_shape_validation_messages_for_malformed_table_keys(
        Func<RelationalResourceModel> createModel,
        string expectedMessage
    )
    {
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(createModel());

        act.Should().Throw<InvalidOperationException>().WithMessage(expectedMessage);
    }

    [Test]
    public void It_should_fail_fast_when_key_column_kind_is_not_parent_key_part_or_ordinal()
    {
        var unsupportedModel = CreateRootOnlyModelWithUnsupportedKeyColumnKind();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for 'edfi.Student': key column 'SchoolYear' has unsupported kind 'Scalar'. Supported key kinds are ParentKeyPart and Ordinal."
            );
    }

    [Test]
    public void It_should_fail_fast_when_table_contains_duplicate_column_names()
    {
        var unsupportedModel = CreateRootOnlyModelWithDuplicateColumnNames();
        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot compile write plan for 'edfi.Student': duplicate column name 'SchoolYear' encountered while building 'columnByName' map."
            );
    }

    [Test]
    public void It_should_fail_fast_for_non_relational_storage_resources()
    {
        var unsupportedModel = _supportedRootOnlyModel with
        {
            StorageKind = ResourceStorageKind.SharedDescriptorTable,
        };

        var act = () => new WritePlanCompiler(SqlDialect.Pgsql).Compile(unsupportedModel);

        act.Should()
            .Throw<NotSupportedException>()
            .WithMessage("Only relational-table resources are supported for write-plan compilation.*");
    }
}
