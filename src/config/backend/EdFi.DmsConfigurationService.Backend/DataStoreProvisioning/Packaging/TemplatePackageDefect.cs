// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;

/// <summary>
/// Why a template package was refused by the stager (6.2 steps 4, 5, 6 and 8). Each value maps to one failure
/// code the catalog reports; the stager never decides between codes itself.
/// </summary>
public enum TemplatePackageDefect
{
    /// <summary>The attestation did not prove the private copy trusted; <see cref="TemplatePackageException.TrustRefusal"/> names the first failing check (<c>ArtifactUntrusted</c>).</summary>
    Untrusted,

    /// <summary>The archive violates an entry rule of the delivered contract (F-P2), is not a zip archive, or an entry exceeds the size the contract allows (<c>ManifestInvalid</c>).</summary>
    PackageInvalid,

    /// <summary>The <c>restore-manifest.json</c> entry is not a version-1 manifest; the inner exception is the <see cref="Manifest.RestoreManifestException"/> (<c>ManifestInvalid</c>).</summary>
    ManifestInvalid,

    /// <summary>The manifest or the <c>.nuspec</c> declares a package identity different from the resolved one (<c>ManifestMismatch</c>).</summary>
    IdentityMismatch,

    /// <summary>The extracted artifact's SHA-256 differs from the manifest's <c>artifactSha256</c> (<c>ArtifactTampered</c>).</summary>
    ArtifactTampered,
}
