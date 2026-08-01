// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Pins the compiled natural-key probe metadata against the authoritative DS 5.2 mapping set and the
/// golden DDL it produces. The probe column lists must reproduce the physical
/// <c>UX_&lt;T&gt;_RefKey</c> / <c>UX_&lt;R&gt;_NK</c> constraint column lists exactly — a probe that
/// binds a different arity, a different column, or a unified alias instead of its canonical stored
/// column cannot seek the index it was compiled for.
/// </summary>
[TestFixture]
public class Given_NaturalKeyProbes_Over_Authoritative_MappingSets
{
    private const string Ds52FixturePath =
        "../Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json";
    private const string SampleExtensionFixturePath =
        "../Fixtures/authoritative/sample/inputs/sample-api-schema-authoritative.json";

    private static readonly DbColumnName _documentIdColumn = new("DocumentId");

    private MappingSet _ds52MappingSet = null!;
    private MappingSet _sampleExtensionMappingSet = null!;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var compiler = new MappingSetCompiler();
        _ds52MappingSet = compiler.Compile(
            RuntimePlanFixtureModelSetBuilder.Build(Ds52FixturePath, SqlDialect.Pgsql)
        );
        _sampleExtensionMappingSet = compiler.Compile(
            RuntimePlanFixtureModelSetBuilder.Build(
                [(Ds52FixturePath, false), (SampleExtensionFixturePath, true)],
                SqlDialect.Pgsql
            )
        );
    }

    [Test]
    public void It_should_bind_the_unified_stored_column_for_section_not_an_alias()
    {
        var probe = _ds52MappingSet.NaturalKeyProbeTargets[new QualifiedResourceName("Ed-Fi", "Section")];
        var columnNames = probe.Columns.Select(column => column.StorageColumn.Value).ToArray();

        // UX_Section_RefKey: (CourseOffering_LocalCourseCode, SchoolId_Unified, CourseOffering_SchoolYear,
        //                     CourseOffering_SessionName, SectionIdentifier) + DocumentId
        columnNames
            .Should()
            .Equal(
                "CourseOffering_LocalCourseCode",
                "SchoolId_Unified",
                "CourseOffering_SchoolYear",
                "CourseOffering_SessionName",
                "SectionIdentifier"
            );
        columnNames.Should().NotContain("CourseOffering_SchoolReferenceSchoolId");
        columnNames.Should().NotContain("CourseOffering_SessionReferenceSchoolId");
        columnNames.Should().OnlyHaveUniqueItems("RefKey de-dups by storage column");
    }

    [Test]
    public void It_should_record_the_first_identity_path_that_collapsed_onto_a_unified_storage_column()
    {
        var probe = _ds52MappingSet.NaturalKeyProbeTargets[new QualifiedResourceName("Ed-Fi", "Section")];
        var unifiedColumn = probe.Columns.Single(column => column.StorageColumn.Value == "SchoolId_Unified");

        unifiedColumn.SourceIdentityJsonPath.Canonical.Should().Be("$.courseOfferingReference.schoolId");
        unifiedColumn.DescriptorResource.Should().BeNull();
    }

    [Test]
    public void It_should_emit_all_seven_scalar_probe_columns_for_student_section_association()
    {
        var probe = _ds52MappingSet.NaturalKeyProbeTargets[
            new QualifiedResourceName("Ed-Fi", "StudentSectionAssociation")
        ];

        probe
            .Columns.Select(column => column.StorageColumn.Value)
            .Should()
            .Equal(
                "BeginDate",
                "Section_LocalCourseCode",
                "Section_SchoolId",
                "Section_SchoolYear",
                "Section_SectionIdentifier",
                "Section_SessionName",
                "Student_StudentUniqueId"
            );
    }

    [Test]
    public void It_should_flag_the_descriptor_part_on_program()
    {
        var probe = _ds52MappingSet.NaturalKeyProbeTargets[new QualifiedResourceName("Ed-Fi", "Program")];
        var descriptorPart = probe.Columns.Single(column => column.DescriptorResource is not null);

        descriptorPart.StorageColumn.Value.Should().Be("ProgramTypeDescriptor_DescriptorId");
        descriptorPart
            .DescriptorResource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"));
        descriptorPart.SourceIdentityJsonPath.Canonical.Should().Be("$.programTypeDescriptor");
    }

    [Test]
    public void It_should_probe_the_abstract_identity_table_for_education_organization()
    {
        var probe = _ds52MappingSet.NaturalKeyProbeTargets[
            new QualifiedResourceName("Ed-Fi", "EducationOrganization")
        ];

        probe.IsAbstract.Should().BeTrue();
        probe
            .ProbeTable.Should()
            .Be(new DbTableName(new DbSchemaName("edfi"), "EducationOrganizationIdentity"));
        probe.DocumentIdColumn.Should().Be(_documentIdColumn);
        probe.Columns.Select(column => column.StorageColumn.Value).Should().Equal("EducationOrganizationId");
    }

    [Test]
    public void It_should_probe_the_reference_backed_abstract_identity_table_in_refkey_order()
    {
        var probe = _ds52MappingSet.NaturalKeyProbeTargets[
            new QualifiedResourceName("Ed-Fi", "GeneralStudentProgramAssociation")
        ];

        probe.IsAbstract.Should().BeTrue();
        probe
            .Columns.Select(column => column.StorageColumn.Value)
            .Should()
            .Equal(
                "BeginDate",
                "EducationOrganization_EducationOrganizationId",
                "Program_EducationOrganizationId",
                "Program_ProgramName",
                "Program_ProgramTypeDescriptor_DescriptorId",
                "Student_StudentUniqueId"
            );
        probe
            .Columns.Single(column => column.DescriptorResource is not null)
            .StorageColumn.Value.Should()
            .Be("Program_ProgramTypeDescriptor_DescriptorId");
    }

    [Test]
    public void It_should_not_compile_a_probe_target_for_descriptor_resources()
    {
        _ds52MappingSet
            .NaturalKeyProbeTargets.Should()
            .NotContainKey(new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"));
        _ds52MappingSet
            .OwnNaturalKeyProbesByResource.Should()
            .NotContainKey(new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"));
    }

    [Test]
    public void It_should_match_every_probe_target_to_its_physical_reference_key_constraint()
    {
        var checkedTargets = 0;

        foreach (var (resource, probe) in _ds52MappingSet.NaturalKeyProbeTargets)
        {
            var expected = probe
                .Columns.Select(column => column.StorageColumn)
                .Append(probe.DocumentIdColumn)
                .ToArray();

            // Locate the RefKey constraint STRUCTURALLY (same arity, DocumentId trailing) rather than by
            // name: ApplyDialectIdentifierShorteningPass hash-truncates long constraint names, dropping
            // the "_RefKey" token.
            var candidates = FindProbeTableConstraints(_ds52MappingSet, probe)
                .Where(constraint =>
                    constraint.Columns.Count == expected.Length
                    && constraint.Columns[^1].Equals(probe.DocumentIdColumn)
                )
                .ToArray();

            if (candidates.Length == 0)
            {
                // Not every resource is a reference target, so not every root table carries a RefKey.
                continue;
            }

            candidates.Should().ContainSingle($"resource '{resource.ProjectName}.{resource.ResourceName}'");
            candidates[0]
                .Columns.Should()
                .Equal(expected, $"resource '{resource.ProjectName}.{resource.ResourceName}'");
            checkedTargets++;
        }

        // Only resources that are actually referenced carry a RefKey, so this is well below the total
        // resource count; the floor just proves the loop did real work.
        checkedTargets.Should().BeGreaterThan(50);
    }

    [Test]
    public void It_should_collapse_reference_sourced_own_nk_parts_to_fk_columns()
    {
        var ownNk = _ds52MappingSet.OwnNaturalKeyProbesByResource[
            new QualifiedResourceName("Ed-Fi", "Section")
        ];

        // UX_Section_NK: (CourseOffering_DocumentId, SectionIdentifier) — 4 courseOffering identity paths
        // collapse onto 1 FK column.
        ownNk
            .Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Equal("CourseOffering_DocumentId", "SectionIdentifier");
        ownNk.Columns[0].ReferenceIdentityJsonPath.Should().NotBeNull();
        ownNk.Columns[0].ScalarSourceJsonPath.Should().BeNull();
        ownNk.Columns[1].ScalarSourceJsonPath.Should().NotBeNull();
        ownNk.Columns[1].ReferenceIdentityJsonPath.Should().BeNull();

        // The 409 duplicate-identity body reports one entry per identity path, not per collapsed column.
        ownNk
            .IdentityJsonPathsInOrder.Select(path => path.Canonical)
            .Should()
            .Equal(
                "$.courseOfferingReference.localCourseCode",
                "$.courseOfferingReference.schoolId",
                "$.courseOfferingReference.schoolYear",
                "$.courseOfferingReference.sessionName",
                "$.sectionIdentifier"
            );
    }

    [Test]
    public void It_should_keep_descriptor_identity_parts_as_scalars_in_the_own_natural_key()
    {
        var ownNk = _ds52MappingSet.OwnNaturalKeyProbesByResource[
            new QualifiedResourceName("Ed-Fi", "Program")
        ];

        // UX_Program_NK: (EducationOrganization_DocumentId, ProgramName, ProgramTypeDescriptor_DescriptorId)
        ownNk
            .Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Equal("EducationOrganization_DocumentId", "ProgramName", "ProgramTypeDescriptor_DescriptorId");

        var descriptorPart = ownNk.Columns[2];
        descriptorPart.ScalarSourceJsonPath.Should().NotBeNull();
        descriptorPart.ReferenceIdentityJsonPath.Should().BeNull();
        descriptorPart
            .DescriptorResource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"));
    }

    [Test]
    public void It_should_tag_exactly_one_value_source_on_every_own_natural_key_column()
    {
        foreach (var (resource, ownNk) in _ds52MappingSet.OwnNaturalKeyProbesByResource)
        {
            ownNk.Columns.Should().NotBeEmpty($"resource '{Format(resource)}'");

            foreach (var column in ownNk.Columns)
            {
                (column.ScalarSourceJsonPath is null ^ column.ReferenceIdentityJsonPath is null)
                    .Should()
                    .BeTrue(
                        $"column '{column.ColumnName.Value}' on resource '{Format(resource)}' must carry "
                            + "exactly one value source"
                    );
            }

            ownNk
                .Columns.Select(column => column.ColumnName.Value)
                .Should()
                .OnlyHaveUniqueItems($"resource '{Format(resource)}'");
        }
    }

    [Test]
    public void It_should_match_the_legacy_trigger_derived_nk_columns_for_every_ds52_resource()
    {
        AssertLegacyTriggerParity(_ds52MappingSet);
    }

    [Test]
    public void It_should_match_the_legacy_trigger_derived_nk_columns_for_every_sample_extension_resource()
    {
        AssertLegacyTriggerParity(_sampleExtensionMappingSet);
    }

    [Test]
    public void It_should_compile_the_shared_descriptor_probe_target()
    {
        var descriptorProbe = _ds52MappingSet.DescriptorProbeTarget;

        descriptorProbe.Table.Should().Be(new DbTableName(new DbSchemaName("dms"), "Descriptor"));
        descriptorProbe.UriLoweredColumn.Should().Be(new DbColumnName("UriLowered"));
        descriptorProbe.DiscriminatorColumn.Should().Be(new DbColumnName("Discriminator"));

        // The literal is the BARE resource name — byte-identical to what DescriptorWriteBodyExtractor
        // persists, NOT the "{Project}:{Resource}" form used by link injection.
        descriptorProbe
            .DiscriminatorLiteralByResource[new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")]
            .Should()
            .Be("ProgramTypeDescriptor");
        descriptorProbe.DiscriminatorLiteralByResource.Should().NotBeEmpty();

        foreach (var (resource, literal) in descriptorProbe.DiscriminatorLiteralByResource)
        {
            literal.Should().Be(resource.ResourceName);
        }
    }

    [Test]
    public void It_should_compile_descriptor_discriminator_literals_for_extension_project_descriptors()
    {
        var descriptorProbe = _sampleExtensionMappingSet.DescriptorProbeTarget;

        descriptorProbe
            .DiscriminatorLiteralByResource.Should()
            .ContainKey(new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor"));

        foreach (var (resource, literal) in descriptorProbe.DiscriminatorLiteralByResource)
        {
            literal.Should().Be(resource.ResourceName);
        }
    }

    [Test]
    public void It_should_reject_a_model_set_that_repeats_a_concrete_resource()
    {
        var modelSet = RuntimePlanFixtureModelSetBuilder.Build(Ds52FixturePath, SqlDialect.Pgsql);
        var duplicated = modelSet with
        {
            ConcreteResourcesInNameOrder =
            [
                .. modelSet.ConcreteResourcesInNameOrder,
                modelSet.ConcreteResourcesInNameOrder[0],
            ],
        };

        var act = () => MappingSetCompiler.BuildNaturalKeyProbes(duplicated);

        act.Should().Throw<InvalidOperationException>().WithMessage("*duplicate*");
    }

    [Test]
    public void It_should_reject_a_model_set_that_repeats_an_abstract_identity_table()
    {
        var modelSet = RuntimePlanFixtureModelSetBuilder.Build(Ds52FixturePath, SqlDialect.Pgsql);
        var duplicated = modelSet with
        {
            AbstractIdentityTablesInNameOrder =
            [
                .. modelSet.AbstractIdentityTablesInNameOrder,
                modelSet.AbstractIdentityTablesInNameOrder[0],
            ],
        };

        var act = () => MappingSetCompiler.BuildNaturalKeyProbes(duplicated);

        act.Should().Throw<InvalidOperationException>().WithMessage("*duplicate natural-key probe target*");
    }

    /// <summary>
    /// Differential guard: the compiled own-identity probe must reproduce, resource by resource, the
    /// column list the runtime derivation produces today from the <c>ReferentialIdentity</c> trigger
    /// parameter block. Deleted with the trigger in a later phase.
    /// </summary>
    private static void AssertLegacyTriggerParity(MappingSet mappingSet)
    {
        var comparedResources = 0;

        foreach (var (resource, ownNk) in mappingSet.OwnNaturalKeyProbesByResource)
        {
            var legacyColumns = LegacyRootNaturalKeyColumns(mappingSet, resource);

            ownNk
                .Columns.Select(column => column.ColumnName)
                .Should()
                .Equal(legacyColumns, $"probe and trigger must agree for '{Format(resource)}'");
            comparedResources++;
        }

        comparedResources.Should().BeGreaterThan(100);
    }

    /// <summary>
    /// Verbatim copy of the pre-severing
    /// <c>RelationalWriteConstraintResolver.GetRootNaturalKeyColumnsOrThrow</c> derivation: read the
    /// resource's <c>ReferentialIdentityMaintenance</c> trigger parameters, substitute the reference
    /// site's FK column for every identity element whose path is supplied by an identity-component
    /// reference binding, and de-duplicate by column name in first-seen order.
    /// </summary>
    private static IReadOnlyList<DbColumnName> LegacyRootNaturalKeyColumns(
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        var resourceModel = mappingSet
            .Model.ConcreteResourcesInNameOrder.Single(concreteResource =>
                concreteResource.RelationalModel.Resource.Equals(resource)
            )
            .RelationalModel;
        var rootTable = resourceModel.Root;

        var referentialIdentityTrigger = mappingSet.Model.TriggersInCreateOrder.Single(trigger =>
            trigger.Table.Equals(rootTable.Table)
            && trigger.Parameters is TriggerKindParameters.ReferentialIdentityMaintenance parameters
            && string.Equals(parameters.ProjectName, resource.ProjectName, StringComparison.Ordinal)
            && string.Equals(parameters.ResourceName, resource.ResourceName, StringComparison.Ordinal)
        );
        var referentialIdentityParameters = (TriggerKindParameters.ReferentialIdentityMaintenance)
            referentialIdentityTrigger.Parameters;

        Dictionary<string, DocumentReferenceBinding> identityBindingsByPath = new(StringComparer.Ordinal);

        foreach (
            var binding in resourceModel.DocumentReferenceBindings.Where(binding =>
                binding.IsIdentityComponent && binding.Table.Equals(rootTable.Table)
            )
        )
        {
            foreach (
                var canonicalReferencePath in binding.IdentityBindings.Select(referencePath =>
                    referencePath.ReferenceJsonPath.Canonical
                )
            )
            {
                identityBindingsByPath.TryAdd(canonicalReferencePath, binding);
            }
        }

        HashSet<string> seenColumns = new(StringComparer.Ordinal);
        List<DbColumnName> rootNaturalKeyColumns = [];

        foreach (var identityElement in referentialIdentityParameters.IdentityElements)
        {
            var constraintColumn = identityBindingsByPath.TryGetValue(
                identityElement.IdentityJsonPath,
                out var identityBinding
            )
                ? identityBinding.FkColumn
                : identityElement.Column;

            if (seenColumns.Add(constraintColumn.Value))
            {
                rootNaturalKeyColumns.Add(constraintColumn);
            }
        }

        return rootNaturalKeyColumns;
    }

    private static IReadOnlyList<TableConstraint.Unique> FindProbeTableConstraints(
        MappingSet mappingSet,
        NaturalKeyProbeTarget probe
    )
    {
        if (probe.IsAbstract)
        {
            return mappingSet
                .Model.AbstractIdentityTablesInNameOrder.Single(abstractIdentityTable =>
                    abstractIdentityTable.TableModel.Table.Equals(probe.ProbeTable)
                )
                .TableModel.Constraints.OfType<TableConstraint.Unique>()
                .ToArray();
        }

        return mappingSet
            .Model.ConcreteResourcesInNameOrder.Single(concreteResource =>
                concreteResource.StorageKind is ResourceStorageKind.RelationalTables
                && concreteResource.RelationalModel.Root.Table.Equals(probe.ProbeTable)
            )
            .RelationalModel.Root.Constraints.OfType<TableConstraint.Unique>()
            .ToArray();
    }

    private static string Format(QualifiedResourceName resource) =>
        $"{resource.ProjectName}.{resource.ResourceName}";
}
