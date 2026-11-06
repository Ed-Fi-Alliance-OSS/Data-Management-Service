// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IO.Compression;
using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;

/// <summary>
/// An authenticated template package staged in its operation's private directory (D-5): the private copy of
/// the nupkg (<see cref="PackageCopyFileName"/>) whose SHA-256 the attestation was verified against, held open
/// through one handle that nothing else can share, with the archive's entry rules enforced and its manifest
/// read from that same handle. The catalog file is never touched again. <see cref="ExtractArtifactAsync"/>
/// streams the single declared artifact from the held archive into a generated file name
/// (<c>artifact.sql</c> or <c>artifact.bak</c>, mode <c>0600</c>) while hashing, and refuses the result when the
/// hash differs from the manifest's <c>artifactSha256</c> (6.2 step 8). Disposal closes the handle and deletes
/// the operation directory on a best-effort basis; bounded cleanup accounts for what remains through
/// <see cref="Directory"/>.
/// </summary>
public sealed class StagedTemplatePackage : IDisposable
{
    /// <summary>The generated name of the private package copy; never the catalog file's name.</summary>
    public const string PackageCopyFileName = "package.nupkg";

    /// <summary>The generated base name of the extracted artifact; the extension follows the manifest's engine.</summary>
    public const string ArtifactBaseName = "artifact";

    private const int CopyBufferSize = 1024 * 1024;

    private readonly ZipArchive _archive;
    private readonly ZipArchiveEntry _artifactEntry;
    private bool _artifactExtracted;
    private bool _disposed;

    internal StagedTemplatePackage(
        PrivateOperationDirectory directory,
        ZipArchive archive,
        ZipArchiveEntry artifactEntry,
        string packageSha256,
        string producer,
        RestoreManifest manifest
    )
    {
        Directory = directory;
        _archive = archive;
        _artifactEntry = artifactEntry;
        PackageSha256 = packageSha256;
        Producer = producer;
        Manifest = manifest;
    }

    /// <summary>The operation directory holding the private copy and, after extraction, the artifact.</summary>
    public PrivateOperationDirectory Directory { get; }

    /// <summary>The absolute path of the private package copy.</summary>
    public string PackageCopyPath => Directory.PathOf(PackageCopyFileName);

    /// <summary>Lowercase hex SHA-256 of the private copy, the hash the attestation was verified against.</summary>
    public string PackageSha256 { get; }

    /// <summary>The trust policy producer whose key verified the attestation, in the policy's spelling.</summary>
    public string Producer { get; }

    public RestoreManifest Manifest { get; }

    public DmsArtifactKind ArtifactKind =>
        string.Equals(Manifest.DatabaseEngine, RestoreManifest.MssqlEngine, StringComparison.Ordinal)
            ? DmsArtifactKind.MssqlBackup
            : DmsArtifactKind.PostgresqlSqlDump;

    /// <summary>The generated file name the artifact is extracted to: <c>artifact.sql</c> or <c>artifact.bak</c>.</summary>
    public string ArtifactFileName =>
        ArtifactBaseName + RestoreManifest.ArtifactExtensionFor(Manifest.DatabaseEngine);

    /// <summary>
    /// Extracts the declared artifact once. On any failure, including a hash mismatch
    /// (<see cref="TemplatePackageDefect.ArtifactTampered"/>), the partial file is deleted before the exception
    /// leaves; the operation directory and the package copy stay for the owner to dispose.
    /// </summary>
    public async Task<StagedArtifact> ExtractArtifactAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_artifactExtracted)
        {
            throw new InvalidOperationException("The artifact was already extracted for this operation.");
        }

        _artifactExtracted = true;
        string artifactPath = Directory.PathOf(ArtifactFileName);
        string sha256;
        try
        {
            sha256 = await WriteArtifactAsync(cancellationToken);
        }
        catch
        {
            TryDeletePartialArtifact(artifactPath);
            throw;
        }

        if (!string.Equals(sha256, Manifest.ArtifactSha256, StringComparison.Ordinal))
        {
            TryDeletePartialArtifact(artifactPath);
            throw new TemplatePackageException(
                TemplatePackageDefect.ArtifactTampered,
                "The extracted artifact's SHA-256 differs from the manifest's artifactSha256."
            );
        }

        return new StagedArtifact(artifactPath, sha256, ArtifactKind);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _archive.Dispose();
        Directory.Dispose();
    }

    private async Task<string> WriteArtifactAsync(CancellationToken cancellationToken)
    {
        await using FileStream destination = Directory.CreateFile(ArtifactFileName);
        await using Stream entry = await _artifactEntry.OpenAsync(cancellationToken);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[CopyBufferSize];
        int read;
        while ((read = await entry.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await destination.FlushAsync(cancellationToken);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void TryDeletePartialArtifact(string artifactPath)
    {
        try
        {
            File.Delete(artifactPath);
        }
        catch (IOException)
        {
            // The directory deletion at disposal, or bounded cleanup, removes what this could not.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }
}
