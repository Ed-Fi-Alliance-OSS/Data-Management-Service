// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_Complete_Model_Set_With_Descriptors
{
    private DerivedRelationalModelSet? _modelSet;

    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();

        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
        var dialectRules = new PgsqlDialectRules();

        _modelSet = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, dialectRules);
    }

    [Test]
    public void It_Should_Include_Descriptor_Resources_With_SharedDescriptorTable_Storage()
    {
        _modelSet.Should().NotBeNull();

        var descriptorResources = _modelSet!
            .ConcreteResourcesInNameOrder.Where(r =>
                r.ResourceKey.Resource.ResourceName.EndsWith("Descriptor", StringComparison.Ordinal)
            )
            .ToList();

        descriptorResources.Should().NotBeEmpty("hand-authored fixture should contain descriptors");

        foreach (var descriptorResource in descriptorResources)
        {
            descriptorResource.StorageKind.Should().Be(ResourceStorageKind.SharedDescriptorTable);
        }
    }

    [Test]
    public void It_Should_Not_Create_Per_Descriptor_Tables()
    {
        _modelSet.Should().NotBeNull();

        var descriptorResources = _modelSet!
            .ConcreteResourcesInNameOrder.Where(r =>
                r.ResourceKey.Resource.ResourceName.EndsWith("Descriptor", StringComparison.Ordinal)
            )
            .ToList();

        descriptorResources.Should().NotBeEmpty();

        foreach (var descriptorResource in descriptorResources)
        {
            descriptorResource.RelationalModel.TablesInDependencyOrder.Should().HaveCount(1);

            var rootTable = descriptorResource.RelationalModel.Root;
            rootTable.Table.Schema.Value.Should().Be("dms");
            rootTable.Table.Name.Should().Be("Descriptor");
        }
    }

    [Test]
    public void It_Should_Include_Descriptor_Metadata()
    {
        _modelSet.Should().NotBeNull();

        var descriptorResources = _modelSet!
            .ConcreteResourcesInNameOrder.Where(r =>
                r.ResourceKey.Resource.ResourceName.EndsWith("Descriptor", StringComparison.Ordinal)
            )
            .ToList();

        descriptorResources.Should().NotBeEmpty();

        foreach (var descriptorResource in descriptorResources)
        {
            descriptorResource.DescriptorMetadata.Should().NotBeNull();
            descriptorResource
                .DescriptorMetadata!.DiscriminatorStrategy.Should()
                .Be(DiscriminatorStrategy.ResourceKeyId);
            descriptorResource.DescriptorMetadata.ColumnContract.Should().NotBeNull();
            descriptorResource.DescriptorMetadata.ColumnContract.Namespace.Value.Should().Be("Namespace");
            descriptorResource.DescriptorMetadata.ColumnContract.CodeValue.Value.Should().Be("CodeValue");
        }
    }

    [Test]
    public void It_Should_Preserve_Non_Descriptor_Resources()
    {
        _modelSet.Should().NotBeNull();

        var nonDescriptorResources = _modelSet!
            .ConcreteResourcesInNameOrder.Where(r =>
                !r.ResourceKey.Resource.ResourceName.EndsWith("Descriptor", StringComparison.Ordinal)
            )
            .ToList();

        nonDescriptorResources.Should().NotBeEmpty();

        foreach (var resource in nonDescriptorResources)
        {
            resource.StorageKind.Should().Be(ResourceStorageKind.RelationalTables);
            resource.DescriptorMetadata.Should().BeNull();
        }
    }
}
