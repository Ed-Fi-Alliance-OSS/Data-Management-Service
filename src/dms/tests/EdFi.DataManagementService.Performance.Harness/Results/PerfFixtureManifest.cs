// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Fixtures;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// The versioned root of fixture-manifest.json: the definition the run loaded and the
/// analytic values the loader verified against the live database. Writing this manifest is
/// only legitimate after <see cref="PerfFixtureLoader" /> verification passed, which is what
/// <paramref name="Verified" /> records.
/// </summary>
public sealed record PerfFixtureManifest(
    string SchemaVersion,
    string DefinitionVersion,
    string FixtureId,
    long RowCount,
    long MinDocumentId,
    long MaxDocumentId,
    long GapCount,
    double GapDensity,
    long DocumentIdSum,
    int DescriptorCount,
    IReadOnlyList<string> DescriptorResourceNames,
    int ChildCollectionRowsPerStudent,
    string ResourceEndpoint,
    bool Verified
)
{
    public static PerfFixtureManifest Create(PerfFixtureDefinition definition) =>
        new(
            PerfArtifactSchema.Version,
            PerfFixtureDefinition.DefinitionVersion,
            definition.Kind.Id,
            definition.RowCount,
            PerfFixtureDefinition.MinDocumentId,
            definition.MaxDocumentId,
            definition.GapCount,
            definition.GapDensity,
            definition.DocumentIdSum(),
            PerfFixtureDefinition.DescriptorCount,
            PerfFixtureDefinition.DescriptorResourceNames,
            PerfFixtureDefinition.ChildCollectionRowsPerStudent,
            PerfFixtureDefinition.ResourceEndpoint,
            Verified: true
        );
}
