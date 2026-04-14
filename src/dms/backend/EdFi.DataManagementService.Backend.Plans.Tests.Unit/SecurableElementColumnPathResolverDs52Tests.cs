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
}
