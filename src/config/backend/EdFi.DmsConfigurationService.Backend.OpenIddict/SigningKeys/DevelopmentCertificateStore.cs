// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// The single owner of the development certificate file (spec D-10). Check-and-create runs under one lock, and a new
/// certificate is written to a temporary file in the same directory and then moved into place, so no caller can read a
/// partially written file. Validation (<see cref="CertificateSigningKeySource"/>) and issuance obtain the certificate
/// here, so an issued token's key id is always the thumbprint validation publishes (I-10). The certificate itself is
/// the one issuance created before: a 2048-bit RSA, <c>CN=DevCert</c>, SHA-256 with PKCS#1 padding, valid from one day
/// ago for one year, exported as PFX with the configured password.
/// </summary>
public sealed class DevelopmentCertificateStore(
    IOptions<IdentityOptions> identityOptions,
    ILogger<DevelopmentCertificateStore> logger
) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Returns the development certificate, with its private key, creating the file first when it is absent. Each call
    /// returns a new instance that the caller disposes.
    /// </summary>
    public async Task<X509Certificate2> GetAsync(CancellationToken cancellationToken)
    {
        string path = identityOptions.Value.DevCertificatePath;
        string password = identityOptions.Value.DevCertificatePassword;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path))
            {
                await CreateAsync(path, password, cancellationToken);
            }

            return X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();

    private async Task CreateAsync(string path, string password, CancellationToken cancellationToken)
    {
        using var rsa = RSA.Create(2048);
        var certRequest = new CertificateRequest(
            "CN=DevCert",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        using X509Certificate2 created = certRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1)
        );
        byte[] bytes = created.Export(X509ContentType.Pfx, password);

        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string temporaryPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        logger.LogInformation("Development certificate created at {CertificatePath}", path);
    }
}
