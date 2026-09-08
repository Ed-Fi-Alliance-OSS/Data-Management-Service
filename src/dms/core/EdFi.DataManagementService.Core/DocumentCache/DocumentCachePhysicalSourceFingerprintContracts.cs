// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.Core.DocumentCache;

public enum DocumentCachePhysicalSourceFingerprintReadStatus
{
    Succeeded,
    DataStoreIdentityMissing,
    DataStoreIdentitySingletonMissing,
    SourceIdentityMalformed,
    SourceIdentityAllZero,
    SourceIdentityUnreadable,
}

public sealed record DocumentCachePhysicalSourceFingerprintReadResult
{
    private DocumentCachePhysicalSourceFingerprintReadResult(
        DocumentCachePhysicalSourceFingerprintReadStatus status,
        DocumentCachePhysicalSourceFingerprint? fingerprint,
        string message
    )
    {
        if (status == DocumentCachePhysicalSourceFingerprintReadStatus.Succeeded && fingerprint is null)
        {
            throw new ArgumentException("Successful fingerprint reads require a fingerprint.");
        }

        if (status != DocumentCachePhysicalSourceFingerprintReadStatus.Succeeded && fingerprint is not null)
        {
            throw new ArgumentException("Failed fingerprint reads must not carry a fingerprint.");
        }

        Status = status;
        Fingerprint = fingerprint;
        Message = DocumentCacheDiagnosticText.Sanitize(message);
    }

    public DocumentCachePhysicalSourceFingerprintReadStatus Status { get; }

    public DocumentCachePhysicalSourceFingerprint? Fingerprint { get; }

    public string Message { get; }

    public bool Succeeded => Status == DocumentCachePhysicalSourceFingerprintReadStatus.Succeeded;

    public static DocumentCachePhysicalSourceFingerprintReadResult Success(
        DocumentCachePhysicalSourceFingerprint fingerprint
    )
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        return new(
            DocumentCachePhysicalSourceFingerprintReadStatus.Succeeded,
            fingerprint,
            "Physical source fingerprint read."
        );
    }

    public static DocumentCachePhysicalSourceFingerprintReadResult Failure(
        DocumentCachePhysicalSourceFingerprintReadStatus status,
        string message
    )
    {
        if (status == DocumentCachePhysicalSourceFingerprintReadStatus.Succeeded)
        {
            throw new ArgumentException("Use Success for successful fingerprint reads.", nameof(status));
        }

        return new(status, fingerprint: null, message);
    }

    public DocumentCacheInventoryValidationResult ToInventoryValidationResult() =>
        Status switch
        {
            DocumentCachePhysicalSourceFingerprintReadStatus.Succeeded => new(
                DocumentCacheInventoryStatus.Satisfied,
                Message
            ),
            DocumentCachePhysicalSourceFingerprintReadStatus.DataStoreIdentityMissing
            or DocumentCachePhysicalSourceFingerprintReadStatus.DataStoreIdentitySingletonMissing => new(
                DocumentCacheInventoryStatus.Missing,
                Message
            ),
            DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityMalformed
            or DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityAllZero => new(
                DocumentCacheInventoryStatus.Invalid,
                Message
            ),
            DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityUnreadable => new(
                DocumentCacheInventoryStatus.Unreadable,
                Message
            ),
            _ => throw new InvalidOperationException($"Unsupported read status '{Status}'."),
        };
}

public interface IDocumentCachePhysicalSourceFingerprintReader
{
    RelationalProviderToken ProviderToken { get; }

    Task<DocumentCachePhysicalSourceFingerprintReadResult> ReadFingerprintAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    );
}

public static class DocumentCachePhysicalSourceFingerprintCalculator
{
    private const string PayloadVersion = "ed-fi-dms-source-v1";

    public static DocumentCachePhysicalSourceFingerprint Compute(
        RelationalProviderToken providerToken,
        Guid sourceIdentity
    )
    {
        ArgumentNullException.ThrowIfNull(providerToken);

        if (sourceIdentity == Guid.Empty)
        {
            throw new ArgumentException("Source identity must not be the zero UUID.", nameof(sourceIdentity));
        }

        string payload =
            $"{PayloadVersion}\0{providerToken.Value}\0{sourceIdentity.ToString("D").ToLowerInvariant()}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));

        return new DocumentCachePhysicalSourceFingerprint(
            $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}"
        );
    }
}
