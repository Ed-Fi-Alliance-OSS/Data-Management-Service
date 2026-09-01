// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Fixtures;

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// The versioned root of a descriptor run's fixture-manifest.json: the definition the run
/// loaded and the analytic values the loader verified against the live database. Writing this
/// manifest is only legitimate after <see cref="PerfDescriptorFixtureLoader" /> verification
/// passed, which is what <paramref name="Verified" /> records.
/// </summary>
public sealed record PerfDescriptorFixtureManifest(
    string SchemaVersion,
    string FixtureId,
    long RowCount,
    long AccessibleCount,
    string AccessibleNamespace,
    string InaccessibleNamespace,
    string AccessibleNamespacePrefix,
    long MinDocumentId,
    long MaxDocumentId,
    long DocumentIdSum,
    string ResourceEndpoint,
    bool Verified
)
{
    public static PerfDescriptorFixtureManifest Create(PerfDescriptorFixtureDefinition definition) =>
        new(
            PerfFinalGateArtifactSchema.Version,
            definition.Kind.Id,
            definition.RowCount,
            definition.AccessibleCount,
            PerfDescriptorFixtureDefinition.AccessibleNamespace,
            PerfDescriptorFixtureDefinition.InaccessibleNamespace,
            PerfDescriptorFixtureDefinition.AccessibleNamespacePrefix,
            MinDocumentId: 1,
            definition.MaxDocumentId,
            definition.DocumentIdSum(),
            PerfDescriptorFixtureDefinition.ResourceEndpoint,
            Verified: true
        );
}
