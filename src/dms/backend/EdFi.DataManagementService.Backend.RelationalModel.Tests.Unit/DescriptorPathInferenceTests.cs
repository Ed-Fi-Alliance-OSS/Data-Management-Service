// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_MultiProject_EffectiveSchemaSet_With_CrossProject_Descriptor_Propagation
{
    private RelationalModelSetBuilderContext _context = default!;
    private QualifiedResourceName _schoolResource = default!;
    private QualifiedResourceName _sectionResource = default!;

    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        _context = new RelationalModelSetBuilderContext(
            effectiveSchemaSet,
            SqlDialect.Pgsql,
            new PgsqlDialectRules()
        );
        _schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        _sectionResource = new QualifiedResourceName("Sample", "Section");
    }

    [Test]
    public void It_should_propagate_descriptor_paths_across_projects()
    {
        var descriptorPaths = _context.GetDescriptorPathsForResource(_sectionResource);

        descriptorPaths.Should().ContainKey("$.schoolReference.schoolTypeDescriptor");
        descriptorPaths["$.schoolReference.schoolTypeDescriptor"]
            .DescriptorResource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"));
    }

    [Test]
    public void It_should_partition_extension_descriptor_paths()
    {
        var basePaths = _context.GetDescriptorPathsForResource(_schoolResource);
        var extensionPaths = _context.GetExtensionDescriptorPathsForResource(_schoolResource);

        basePaths.Should().NotContainKey("$._ext.sample.extensionDescriptor");
        extensionPaths.Should().ContainKey("$._ext.sample.extensionDescriptor");
    }
}
