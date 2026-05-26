// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;

namespace EdFi.DataManagementService.Tests.Integration.Fixtures;

/// <summary>
/// Resolves the on-disk repository directory that owns each fixture's source
/// ApiSchema files and its <c>fixture.json</c> manifest. The per-dialect baseline
/// caches use this directory to feed the same DDL-generation pipeline the
/// existing backend integration tests rely on
/// (<see cref="EffectiveSchemaFixtureLoader"/> +
/// <see cref="EdFi.DataManagementService.Backend.Ddl.DdlPipelineHelpers"/>).
/// </summary>
internal static class FixtureRepositoryPaths
{
    private static readonly IReadOnlyDictionary<FixtureKey, string> _repoRelativePaths = new Dictionary<
        FixtureKey,
        string
    >
    {
        [FixtureKey.FocusedStableKeyUpdateSemantics] =
            "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics",
        [FixtureKey.ProfileRootOnlyMerge] =
            "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/profile-root-only-merge",
        [FixtureKey.DescriptorRuntime] =
            "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/descriptor-runtime",
        [FixtureKey.ProfileSeparateTableMerge] =
            "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/profile-separate-table-merge",
        [FixtureKey.ProfileNestedAndRootExtensionChildren] =
            "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-nested-and-root-extension-children",
        [FixtureKey.ProfileCollectionAlignedExtension] =
            "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-collection-aligned-extension",
        [FixtureKey.ProfileCollectionAlignedExtensionHiddenDescendant] =
            "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-collection-aligned-extension-hidden-descendant",
    };

    /// <summary>
    /// Resolves the absolute repository-relative directory for a fixture key.
    /// </summary>
    public static string ResolveFixtureDirectory(FixtureKey key)
    {
        if (!_repoRelativePaths.TryGetValue(key, out string? relativePath))
        {
            throw new InvalidOperationException($"No repository path mapping for FixtureKey '{key}'.");
        }

        return FixturePathResolver.ResolveRepositoryRelativePath(AppContext.BaseDirectory, relativePath);
    }
}
