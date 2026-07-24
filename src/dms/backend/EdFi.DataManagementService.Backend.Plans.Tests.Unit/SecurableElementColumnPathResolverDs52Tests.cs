// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Integration-level tests that validate securable element column path resolution
/// against the authoritative DS 5.2 schema.
/// </summary>
[TestFixture]
public class Given_SecurableElementColumnPathResolver_with_DS52_schema
{
    private static readonly DbSchemaName EdFiSchema = new("edfi");
    private static readonly DbSchemaName DmsSchema = new("dms");

    private DerivedRelationalModelSet _modelSet = null!;
    private MappingSet _mappingSet = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        (_modelSet, _mappingSet) = Ds52FixtureHelper.BuildAndCompile();
    }

    private ConcreteResourceModel FindResource(string resourceName)
    {
        return _modelSet.ConcreteResourcesInNameOrder.First(r =>
            r.ResourceKey.Resource.ResourceName == resourceName
        );
    }

    private static QualifiedResourceName EdFiResource(string resourceName) => new("Ed-Fi", resourceName);

    private static DbTableName EdFiTable(string tableName) => new(EdFiSchema, tableName);

    private static DbTableName DmsTable(string tableName) => new(DmsSchema, tableName);

    private static DbColumnName Column(string columnName) => new(columnName);

    private IReadOnlyList<ColumnPathStep> ResolveBasisPath(string subjectResource, string basisResource)
    {
        return SecurableElementColumnPathResolver.ResolveSecurableElementColumnPath(
            EdFiResource(subjectResource),
            EdFiResource(basisResource),
            _modelSet
        );
    }

    [Test]
    public void It_should_have_securable_elements_on_resources()
    {
        // StudentSchoolAssociation should have both EdOrg and Student securable elements
        var ssa = FindResource("StudentSchoolAssociation");
        ssa.SecurableElements.EducationOrganization.Should().NotBeEmpty();
        ssa.SecurableElements.Student.Should().NotBeEmpty();
    }

    [Test]
    public void It_should_resolve_StudentSchoolAssociation_EdOrg_path()
    {
        var ssa = FindResource("StudentSchoolAssociation");
        var resource = ssa.ResourceKey.Resource;

        _mappingSet.SecurableElementColumnPathsByResource.Should().ContainKey(resource);

        var allPaths = _mappingSet.SecurableElementColumnPathsByResource[resource];
        allPaths.Should().NotBeEmpty();

        // Find the EdOrg path by kind
        var edOrgPath = allPaths.FirstOrDefault(p => p.Kind == SecurableElementKind.EducationOrganization);
        edOrgPath.Should().NotBeNull("StudentSchoolAssociation should have an EdOrg column path");
        edOrgPath!.Steps.Should().HaveCount(1);
        edOrgPath.Steps[0].SourceTable.Schema.Value.Should().Be("edfi");
        edOrgPath.Steps[0].SourceTable.Name.Should().Be("StudentSchoolAssociation");
    }

    [Test]
    public void It_should_resolve_StudentSchoolAssociation_Student_path()
    {
        var ssa = FindResource("StudentSchoolAssociation");
        var resource = ssa.ResourceKey.Resource;

        var allPaths = _mappingSet.SecurableElementColumnPathsByResource[resource];

        // Find the Student path by kind
        var studentPath = allPaths.FirstOrDefault(p => p.Kind == SecurableElementKind.Student);
        studentPath.Should().NotBeNull("StudentSchoolAssociation should have a direct Student path");
        studentPath!.Steps.Should().HaveCount(1);
        studentPath.Steps[0].TargetTable!.Value.Name.Should().Be("Student");
    }

    [Test]
    public void It_should_resolve_physical_column_names_matching_relational_model()
    {
        var ssa = FindResource("StudentSchoolAssociation");
        var resource = ssa.ResourceKey.Resource;
        var allPaths = _mappingSet.SecurableElementColumnPathsByResource[resource];

        // Verify that all column names in resolved paths correspond to actual columns
        // in the derived relational model
        foreach (var resolvedPath in allPaths)
        {
            foreach (var step in resolvedPath.Steps)
            {
                var sourceTable = _modelSet
                    .ConcreteResourcesInNameOrder.SelectMany(r => r.RelationalModel.TablesInDependencyOrder)
                    .FirstOrDefault(t => t.Table == step.SourceTable);

                sourceTable
                    .Should()
                    .NotBeNull($"Source table {step.SourceTable} should exist in the relational model");

                sourceTable!
                    .Columns.Select(c => c.ColumnName)
                    .Should()
                    .Contain(
                        step.SourceColumnName,
                        $"Column {step.SourceColumnName} should exist on table {step.SourceTable}"
                    );
            }
        }
    }

    [Test]
    public void It_should_only_resolve_namespace_paths_for_descriptors()
    {
        // Descriptors only have Namespace securable elements, not EdOrg or Person.
        // Verify that descriptor resources only produce namespace-style paths (single step, null target).
        var descriptors = _modelSet
            .ConcreteResourcesInNameOrder.Where(r =>
                r.StorageKind == ResourceStorageKind.SharedDescriptorTable
            )
            .Select(r => r.ResourceKey.Resource);

        foreach (var descriptor in descriptors)
        {
            if (_mappingSet.SecurableElementColumnPathsByResource.TryGetValue(descriptor, out var paths))
            {
                foreach (var resolvedPath in paths)
                {
                    resolvedPath
                        .Kind.Should()
                        .Be(
                            SecurableElementKind.Namespace,
                            $"Descriptor {descriptor} should be Namespace kind"
                        );
                    resolvedPath
                        .Steps.Should()
                        .HaveCount(1, $"Descriptor {descriptor} paths should be single-step");
                    resolvedPath
                        .Steps[0]
                        .TargetTable.Should()
                        .BeNull($"Descriptor {descriptor} should have null target (namespace-style)");
                }
            }
        }
    }

    [Test]
    public void It_should_resolve_the_view_basis_descriptor_example_from_authoritative_schema()
    {
        var path = ResolveBasisPath("StudentTransportation", "TransportationTypeDescriptor");

        path.Should().ContainSingle();
        path[0].SourceTable.Should().Be(EdFiTable("StudentTransportation"));
        path[0].SourceColumnName.Should().Be(Column("TransportationTypeDescriptor_DescriptorId"));
        path[0].TargetTable.Should().Be(DmsTable("Descriptor"));
        path[0].TargetColumnName.Should().Be(Column("DocumentId"));
    }

    [Test]
    public void It_should_resolve_an_indirect_descriptor_basis_from_authoritative_schema()
    {
        var path = ResolveBasisPath("StudentSectionAssociation", "MediumOfInstructionDescriptor");

        path.Should().HaveCount(2);
        path[0].SourceTable.Should().Be(EdFiTable("StudentSectionAssociation"));
        path[0].SourceColumnName.Should().Be(Column("Section_DocumentId"));
        path[0].TargetTable.Should().Be(EdFiTable("Section"));
        path[0].TargetColumnName.Should().Be(Column("DocumentId"));
        path[1].SourceTable.Should().Be(EdFiTable("Section"));
        path[1].SourceColumnName.Should().Be(Column("MediumOfInstructionDescriptor_DescriptorId"));
        path[1].TargetTable.Should().Be(DmsTable("Descriptor"));
        path[1].TargetColumnName.Should().Be(Column("DocumentId"));
    }

    [Test]
    public void It_should_resolve_a_transitive_view_basis_resource_path_from_authoritative_schema()
    {
        var path = ResolveBasisPath("CourseTranscript", "Student");

        path.Should().HaveCount(2);
        path[0].SourceTable.Should().Be(EdFiTable("CourseTranscript"));
        path[0].SourceColumnName.Should().Be(Column("StudentAcademicRecord_DocumentId"));
        path[0].TargetTable.Should().Be(EdFiTable("StudentAcademicRecord"));
        path[0].TargetColumnName.Should().Be(Column("DocumentId"));
        path[1].SourceTable.Should().Be(EdFiTable("StudentAcademicRecord"));
        path[1].SourceColumnName.Should().Be(Column("Student_DocumentId"));
        path[1].TargetTable.Should().BeNull();
        path[1].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_resolve_abstract_basis_self_reference_from_authoritative_schema()
    {
        var path = ResolveBasisPath("School", "EducationOrganization");

        path.Should().ContainSingle();
        path[0].SourceTable.Should().Be(EdFiTable("School"));
        path[0].SourceColumnName.Should().Be(Column("DocumentId"));
        path[0].TargetTable.Should().BeNull();
        path[0].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_resolve_direct_abstract_basis_reference_from_authoritative_schema()
    {
        var path = ResolveBasisPath("BellSchedule", "EducationOrganization");

        path.Should().ContainSingle();
        path[0].SourceTable.Should().Be(EdFiTable("BellSchedule"));
        path[0].SourceColumnName.Should().Be(Column("School_DocumentId"));
        path[0].TargetTable.Should().BeNull();
        path[0].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_prefer_graduation_plan_over_school_for_StudentSchoolAssociation_abstract_basis()
    {
        var path = ResolveBasisPath("StudentSchoolAssociation", "EducationOrganization");

        path.Should().NotBeEmpty();
        path.Select(step => step.SourceColumnName.Value)
            .Should()
            .Contain(column => column.Contains("GraduationPlan", StringComparison.Ordinal));
        path[0].SourceColumnName.Should().NotBe(Column("School_DocumentId"));
    }

    [Test]
    public void It_should_resolve_direct_concrete_basis_reference_from_authoritative_schema()
    {
        var path = ResolveBasisPath("StaffSchoolAssociation", "School");

        path.Should().ContainSingle();
        path[0].SourceTable.Should().Be(EdFiTable("StaffSchoolAssociation"));
        path[0].SourceColumnName.Should().Be(Column("School_DocumentId"));
        path[0].TargetTable.Should().BeNull();
        path[0].TargetColumnName.Should().BeNull();
    }

    [Test]
    public void It_should_resolve_concrete_basis_through_abstract_reference_from_authoritative_schema()
    {
        var path = ResolveBasisPath("Intervention", "School");

        path.Should().ContainSingle();
        path[0].SourceTable.Should().Be(EdFiTable("Intervention"));
        path[0].SourceColumnName.Should().Be(Column("EducationOrganization_DocumentId"));
        path[0].TargetTable.Should().BeNull();
        path[0].TargetColumnName.Should().BeNull();
    }
}
