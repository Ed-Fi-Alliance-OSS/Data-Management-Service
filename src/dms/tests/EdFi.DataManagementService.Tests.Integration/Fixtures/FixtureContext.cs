// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.Integration.Fixtures;

/// <summary>
/// Resolved test-time context for a fixture: where its ApiSchema lives on disk,
/// where its test-owned profile XML catalog lives, and the qualified resource names
/// the fake claim-set provider must grant CRUD on.
/// </summary>
public sealed record FixtureContext(
    FixtureKey Key,
    string ApiSchemaDirectory,
    string ProfileXmlDirectory,
    IReadOnlyList<QualifiedResourceName> Resources
);

/// <summary>
/// A project-qualified resource name used to build resource-claim URIs for the fake
/// claim-set provider. Project and resource names are matched against ApiSchema
/// values case-insensitively when forming the claim URI.
/// </summary>
public readonly record struct QualifiedResourceName(string ProjectName, string ResourceName);

/// <summary>
/// Identifies a known integration-test fixture. Each value maps to an on-disk
/// ApiSchema directory and the resource set the fake claim-set provider exposes.
/// </summary>
public enum FixtureKey
{
    SmallReferentialIdentity,
    FocusedStableKeyUpdateSemantics,
    ProfileRootOnlyMerge,
    ProfileSeparateTableMerge,
    ProfileNestedAndRootExtensionChildren,
    ProfileCollectionAlignedExtension,
    ProfileCollectionAlignedExtensionHiddenDescendant,
}
