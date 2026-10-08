// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// Reads the public signing key from an X.509 certificate: the development certificate through
/// <see cref="DevelopmentCertificateStore"/>, otherwise the configured certificate file. The key id is the certificate
/// thumbprint, as before. A certificate that cannot be read fails the load; one without an RSA public key is counted as
/// a discarded record.
/// </summary>
public sealed class CertificateSigningKeySource(
    IOptions<IdentityOptions> identityOptions,
    DevelopmentCertificateStore developmentCertificateStore
) : ISigningKeySource
{
    public SigningKeySource Kind => SigningKeySource.Certificate;

    public async Task<SigningKeySourceResult> LoadAsync(CancellationToken cancellationToken)
    {
        using X509Certificate2 certificate = await LoadCertificateAsync(cancellationToken);
        using RSA? publicKey = certificate.GetRSAPublicKey();
        if (publicKey is null)
        {
            return new SigningKeySourceResult([], discardedCount: 1);
        }

        return new SigningKeySourceResult(
            [
                SigningKeyEntry.FromRsaPublicParameters(
                    certificate.Thumbprint,
                    publicKey.ExportParameters(false)
                ),
            ],
            discardedCount: 0
        );
    }

    private async Task<X509Certificate2> LoadCertificateAsync(CancellationToken cancellationToken)
    {
        IdentityOptions options = identityOptions.Value;
        if (options.UseDevelopmentCertificates)
        {
            return await developmentCertificateStore.GetAsync(cancellationToken);
        }

        if (string.IsNullOrEmpty(options.CertificatePath))
        {
            throw new InvalidOperationException(
                "CertificatePath must be set when not using development certificates."
            );
        }

        return string.IsNullOrEmpty(options.CertificatePassword)
            ? X509CertificateLoader.LoadCertificateFromFile(options.CertificatePath)
            : X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, options.CertificatePassword);
    }
}
