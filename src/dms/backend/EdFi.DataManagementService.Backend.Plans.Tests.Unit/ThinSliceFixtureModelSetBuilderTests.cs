// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_ThinSliceFixtureModelSetBuilder_MultiProjectFixture(SqlDialect dialect)
{
    private const string FixturePath =
        "Fixtures/runtime-plan-compilation/multi-project-builder/fixture.manifest.json";
    private DerivedRelationalModelSet _modelSet = null!;
    private DerivedRelationalModelSet _modelSetWithReversedFixtureInputOrder = null!;

    [SetUp]
    public void Setup()
    {
        _modelSet = ThinSliceFixtureModelSetBuilder.Build(
            FixturePath,
            dialect,
            reverseResourceSchemaOrder: false,
            reverseFixtureInputOrder: false
        );
        _modelSetWithReversedFixtureInputOrder = ThinSliceFixtureModelSetBuilder.Build(
            FixturePath,
            dialect,
            reverseResourceSchemaOrder: false,
            reverseFixtureInputOrder: true
        );
    }

    [Test]
    public void It_should_load_fixture_projects_from_manifest_inputs()
    {
        _modelSet
            .EffectiveSchema.SchemaComponentsInEndpointOrder.Select(component =>
                component.ProjectEndpointName
            )
            .Should()
            .Equal("ed-fi", "sample");
        _modelSet
            .EffectiveSchema.SchemaComponentsInEndpointOrder.Single(component =>
                component.ProjectEndpointName == "ed-fi"
            )
            .IsExtensionProject.Should()
            .BeFalse();
        _modelSet
            .EffectiveSchema.SchemaComponentsInEndpointOrder.Single(component =>
                component.ProjectEndpointName == "sample"
            )
            .IsExtensionProject.Should()
            .BeTrue();
        _modelSet.EffectiveSchema.ResourceKeysInIdOrder.Should().HaveCount(2);
    }

    [Test]
    public void It_should_derive_resources_for_all_manifest_inputs()
    {
        var resources = _modelSet
            .ConcreteResourcesInNameOrder.Select(resource => resource.ResourceKey.Resource)
            .ToArray();

        resources.Should().Contain(new QualifiedResourceName("Ed-Fi", "School"));
        resources.Should().Contain(new QualifiedResourceName("Sample", "Section"));
    }

    [Test]
    public void It_should_be_deterministic_when_fixture_input_order_is_reversed()
    {
        _modelSetWithReversedFixtureInputOrder
            .EffectiveSchema.EffectiveSchemaHash.Should()
            .Be(_modelSet.EffectiveSchema.EffectiveSchemaHash);
        _modelSetWithReversedFixtureInputOrder
            .EffectiveSchema.ResourceKeyCount.Should()
            .Be(_modelSet.EffectiveSchema.ResourceKeyCount);
        _modelSetWithReversedFixtureInputOrder
            .EffectiveSchema.ResourceKeySeedHash.Should()
            .Equal(_modelSet.EffectiveSchema.ResourceKeySeedHash);
        _modelSetWithReversedFixtureInputOrder
            .EffectiveSchema.SchemaComponentsInEndpointOrder.Select(component =>
                $"{component.ProjectEndpointName}|{component.ProjectName}|{component.ProjectVersion}|{component.IsExtensionProject}|{component.ProjectHash}"
            )
            .Should()
            .Equal(
                _modelSet.EffectiveSchema.SchemaComponentsInEndpointOrder.Select(component =>
                    $"{component.ProjectEndpointName}|{component.ProjectName}|{component.ProjectVersion}|{component.IsExtensionProject}|{component.ProjectHash}"
                )
            );
        _modelSetWithReversedFixtureInputOrder
            .EffectiveSchema.ResourceKeysInIdOrder.Select(resourceKey =>
                $"{resourceKey.ResourceKeyId}|{resourceKey.Resource.ProjectName}|{resourceKey.Resource.ResourceName}|{resourceKey.ResourceVersion}|{resourceKey.IsAbstractResource}"
            )
            .Should()
            .Equal(
                _modelSet.EffectiveSchema.ResourceKeysInIdOrder.Select(resourceKey =>
                    $"{resourceKey.ResourceKeyId}|{resourceKey.Resource.ProjectName}|{resourceKey.Resource.ResourceName}|{resourceKey.ResourceVersion}|{resourceKey.IsAbstractResource}"
                )
            );
    }
}
