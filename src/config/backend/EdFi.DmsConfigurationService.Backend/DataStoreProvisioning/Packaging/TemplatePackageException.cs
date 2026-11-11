// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;

/// <summary>
/// A template package was refused while being staged or extracted. <see cref="Defect"/> says which contract was
/// violated; the message names the rule and, where useful, an entry count, and never echoes an entry name, a
/// manifest value, or any other content of the package. A refusal is a verdict about the package, raised to the
/// catalog, which maps the defect to a failure code.
/// </summary>
public sealed class TemplatePackageException : Exception
{
    public TemplatePackageException(
        TemplatePackageDefect defect,
        string message,
        Exception? innerException = null
    )
        : base(message, innerException)
    {
        Defect = defect;
    }

    /// <summary>An untrusted verdict of <see cref="TemplateAttestationVerifier"/> over the private copy's hash.</summary>
    public TemplatePackageException(TrustRefusalReason trustRefusal)
        : base($"The package attestation did not verify against the private copy: {trustRefusal}.")
    {
        Defect = TemplatePackageDefect.Untrusted;
        TrustRefusal = trustRefusal;
    }

    public TemplatePackageDefect Defect { get; }

    /// <summary>The first failing attestation check when <see cref="Defect"/> is <see cref="TemplatePackageDefect.Untrusted"/>; null otherwise.</summary>
    public TrustRefusalReason? TrustRefusal { get; }
}
