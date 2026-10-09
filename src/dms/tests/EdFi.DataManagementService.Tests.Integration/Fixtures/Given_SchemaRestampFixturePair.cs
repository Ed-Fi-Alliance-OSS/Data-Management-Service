// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Fixtures;

[TestFixture]
public sealed class Given_SchemaRestampFixturePair
{
    [Test]
    public void It_changes_only_the_effective_hash_while_preserving_compatibility_metadata()
    {
        string sourceDirectory = FixtureRepositoryPaths.ResolveFixtureDirectory(
            FixtureKey.SchemaRestampSource
        );
        string targetDirectory = FixtureRepositoryPaths.ResolveFixtureDirectory(
            FixtureKey.SchemaRestampTarget
        );
        var source = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(sourceDirectory).EffectiveSchema;
        var target = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(targetDirectory).EffectiveSchema;

        target.EffectiveSchemaHash.Should().NotBe(source.EffectiveSchemaHash);
        target.ApiSchemaFormatVersion.Should().Be(source.ApiSchemaFormatVersion);
        target.RelationalMappingVersion.Should().Be(source.RelationalMappingVersion);
        target.ResourceKeyCount.Should().Be(source.ResourceKeyCount);
        target.ResourceKeySeedHash.Should().Equal(source.ResourceKeySeedHash);
        target.ResourceKeysInIdOrder.Should().Equal(source.ResourceKeysInIdOrder);
        target
            .SchemaComponentsInEndpointOrder.Select(component =>
                (
                    component.ProjectEndpointName,
                    component.ProjectName,
                    component.ProjectVersion,
                    component.IsExtensionProject
                )
            )
            .Should()
            .Equal(
                source.SchemaComponentsInEndpointOrder.Select(component =>
                    (
                        component.ProjectEndpointName,
                        component.ProjectName,
                        component.ProjectVersion,
                        component.IsExtensionProject
                    )
                )
            );
    }
}
