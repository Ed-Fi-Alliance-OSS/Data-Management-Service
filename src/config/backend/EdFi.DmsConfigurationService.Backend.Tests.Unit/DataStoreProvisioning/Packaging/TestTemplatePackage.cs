// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;

/// <summary>
/// What <see cref="TestTemplatePackageBuilder.Build"/> wrote to a catalog directory and the values a test
/// asserts against: the package, its sibling attestation, the trust policy that lists the signing producer, and
/// the artifact bytes and hashes the package was built from.
/// </summary>
public sealed record TestTemplatePackage(
    string PackagePath,
    string AttestationPath,
    string AttestationJson,
    string TrustPolicyPath,
    string TrustPolicyJson,
    TemplateTrustPolicy Policy,
    string PackageId,
    string PackageVersion,
    string PackageSha256,
    string ProducerName,
    byte[] ArtifactBytes,
    string ArtifactSha256,
    string ArtifactFileName
);
