// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcStateStorePath")]
public class Given_CdcStateStorePathResolver
{
    [Test]
    public void It_resolves_the_default_local_state_root_outside_the_bootstrap_manifest_root()
    {
        CdcStateStorePathResolver resolver = new();

        resolver.RootPath.Should().EndWith(Path.Combine("eng", "docker-compose", ".cdc-state"));
        resolver.RootPath.Should().NotContain(Path.Combine(".bootstrap", "bootstrap-manifest.json"));
        resolver.RootPath.Should().NotContain($"{Path.DirectorySeparatorChar}.bootstrap");
    }

    [Test]
    public void It_resolves_stable_binding_and_incident_paths_from_validated_identity_segments()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), $"cdc-state-path-{Guid.NewGuid()}");
        CdcStateStorePathResolver resolver = new(rootPath);
        CdcBindingIdentity identity = new("dms-local", "default", "1", "data-store-1", 7);

        CdcStateStorePathResolution binding = resolver.ResolveBindingPath(identity);
        CdcStateStorePathResolution incident = resolver.ResolveIncidentPath(identity);

        binding.Succeeded.Should().BeTrue();
        incident.Succeeded.Should().BeTrue();
        binding
            .FilePath.Should()
            .Be(Path.Combine(rootPath, "bindings", "dms-local", "data-store-1", "7.json"));
        incident
            .FilePath.Should()
            .Be(Path.Combine(rootPath, "incidents", "dms-local", "data-store-1", "7.json"));
    }

    [TestCase("dms/local", "data-store-1", 1, "$.deploymentKey")]
    [TestCase("dms-local", "../data-store-1", 1, "$.instanceKey")]
    [TestCase("dms-local", "data-store-1", 0, "$.generation")]
    public void It_rejects_unsafe_identity_segments_before_returning_a_local_path(
        string deploymentKey,
        string instanceKey,
        long generation,
        string expectedPath
    )
    {
        CdcStateStorePathResolver resolver = new("/tmp/cdc-state");
        CdcBindingIdentity identity = new(deploymentKey, "default", "1", instanceKey, generation);

        CdcStateStorePathResolution result = resolver.ResolveBindingPath(identity);

        result.Succeeded.Should().BeFalse();
        result.FilePath.Should().BeNull();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Path == expectedPath);
    }
}
