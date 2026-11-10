// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;

/// <summary>
/// Immutable private staging of a template package (D-5; 6.2 steps 3 to 6), in this order: create the operation
/// directory (<c>0700</c>); copy the catalog nupkg into it (<c>0600</c>) and never read the catalog file again;
/// hash the private copy by reading it back through the one handle that stays open for the package's lifetime;
/// verify the attestation against that hash, the allowlisted identity and the trust policy exactly in the F-P6
/// order, before the archive is opened; then open the copy as a zip and enforce the delivered consumer's entry
/// rules (F-P2) in its order: unique entry names, exactly one <c>restore-manifest.json</c>, a manifest that
/// satisfies the version-1 contract and names the resolved package identity, exactly one <c>.nuspec</c> that
/// names the same identity, exactly one entry named by the manifest's <c>artifactFileName</c>, and no other
/// <c>.sql</c> or <c>.bak</c> entry. No artifact content is read here: <see cref="StagedTemplatePackage"/>
/// extracts and hashes it on the create path only (6.2 step 8). Every failure deletes the operation directory
/// before the exception leaves. Refusals are <see cref="TemplatePackageException"/>, including every malformed
/// container the zip reader reports (<see cref="InvalidDataException"/>) at any of its boundaries: opening the
/// archive, enumerating the central directory, which the reader defers until the entries are first asked for,
/// and opening or decompressing an entry. Cancellation and ordinary file system failures pass through unchanged;
/// a missing catalog file surfaces as the file system's <see cref="FileNotFoundException"/> for the catalog to
/// classify.
/// </summary>
public sealed class TemplatePackageStager
{
    /// <summary>The largest <c>restore-manifest.json</c> entry accepted; real manifests are far smaller.</summary>
    public const long MaxManifestEntryBytes = 16L * 1024 * 1024;

    /// <summary>The largest <c>.nuspec</c> entry accepted.</summary>
    public const long MaxNuspecEntryBytes = 1024 * 1024;

    private const int CopyBufferSize = 1024 * 1024;

    private const string NuspecExtension = ".nuspec";

    private static readonly string[] _artifactExtensions = [".sql", ".bak"];

    private readonly string _operationRootDirectory;

    /// <param name="operationRootDirectory">The fully qualified directory under which every operation directory is created.</param>
    public TemplatePackageStager(string operationRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationRootDirectory);
        if (!Path.IsPathFullyQualified(operationRootDirectory))
        {
            throw new ArgumentException(
                "The operation directory root must be a fully qualified path.",
                nameof(operationRootDirectory)
            );
        }

        _operationRootDirectory = operationRootDirectory;
    }

    /// <summary>
    /// Claims the operation directory under the configured root and stages into it; every failure, cancellation
    /// included, deletes that directory before the exception leaves.
    /// </summary>
    /// <param name="packagePath">The catalog nupkg, located by the catalog from allowlist-derived names.</param>
    /// <param name="attestationJson">The sibling attestation document's text; null or blank is an untrusted verdict.</param>
    /// <param name="expectedPackageId">The allowlisted package id.</param>
    /// <param name="expectedPackageVersion">The allowlisted package version.</param>
    /// <param name="policy">The operator trust policy.</param>
    /// <param name="operationId">The operation whose private directory is created.</param>
    /// <param name="cancellationToken">Cancels the copy and the reads; the directory is deleted on cancellation too.</param>
    public async Task<StagedTemplatePackage> StageAsync(
        string packagePath,
        string? attestationJson,
        string expectedPackageId,
        string expectedPackageVersion,
        TemplateTrustPolicy policy,
        DmsOperationId operationId,
        CancellationToken cancellationToken
    )
    {
        PrivateOperationDirectory directory = PrivateOperationDirectory.Create(
            _operationRootDirectory,
            operationId
        );
        try
        {
            return await StageAsync(
                directory,
                packagePath,
                attestationJson,
                expectedPackageId,
                expectedPackageVersion,
                policy,
                cancellationToken
            );
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Stages into a directory the caller claimed and owns. On failure, cancellation included, every handle the
    /// stager opened is closed before the exception leaves, and the directory, with whatever the stager wrote
    /// into it, is left to its owner to delete and to account for as residue: the owner, not the stager, knows
    /// the claim was its own. On success the returned package takes the directory over. Static: nothing about
    /// the configured root applies once the caller holds the directory.
    /// </summary>
    public static async Task<StagedTemplatePackage> StageAsync(
        PrivateOperationDirectory directory,
        string packagePath,
        string? attestationJson,
        string expectedPackageId,
        string expectedPackageVersion,
        TemplateTrustPolicy policy,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        if (!Path.IsPathFullyQualified(packagePath))
        {
            throw new ArgumentException("The package path must be fully qualified.", nameof(packagePath));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPackageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPackageVersion);
        ArgumentNullException.ThrowIfNull(policy);

        FileStream? copy = null;
        ZipArchive? archive = null;
        try
        {
            copy = await CopyPackageAsync(packagePath, directory, cancellationToken);
            string packageSha256 = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(copy, cancellationToken)
            );

            TrustVerdict verdict = TemplateAttestationVerifier.Verify(
                attestationJson,
                packageSha256,
                expectedPackageId,
                expectedPackageVersion,
                policy
            );
            if (verdict is TrustVerdict.Untrusted untrusted)
            {
                throw new TemplatePackageException(untrusted.Reason);
            }

            string producer = ((TrustVerdict.Trusted)verdict).Producer;

            copy.Position = 0;
            archive = OpenArchive(copy);
            (RestoreManifest manifest, ZipArchiveEntry artifactEntry) = await ValidateEntriesAsync(
                archive,
                expectedPackageId,
                expectedPackageVersion,
                cancellationToken
            );

            return new StagedTemplatePackage(
                directory,
                archive,
                artifactEntry,
                packageSha256,
                producer,
                manifest
            );
        }
        catch
        {
            if (archive is not null)
            {
                await archive.DisposeAsync();
            }

            if (copy is not null)
            {
                await copy.DisposeAsync();
            }

            throw;
        }
    }

    /// <summary>Copies the catalog file into the private directory and returns the copy's handle positioned at the start.</summary>
    private static async Task<FileStream> CopyPackageAsync(
        string packagePath,
        PrivateOperationDirectory directory,
        CancellationToken cancellationToken
    )
    {
        FileStream copy = directory.CreateFile(StagedTemplatePackage.PackageCopyFileName);
        try
        {
            FileStreamOptions sourceOptions = new()
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.SequentialScan,
            };
            await using (FileStream source = new(packagePath, sourceOptions))
            {
                await source.CopyToAsync(copy, CopyBufferSize, cancellationToken);
            }

            await copy.FlushAsync(cancellationToken);
            copy.Position = 0;
            return copy;
        }
        catch
        {
            await copy.DisposeAsync();
            throw;
        }
    }

    private static ZipArchive OpenArchive(FileStream copy)
    {
        try
        {
            return new ZipArchive(copy, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException exception)
        {
            throw PackageInvalid("The package is not a readable zip archive.", exception);
        }
    }

    /// <summary>The file entries; the reader parses the central directory here, not when the archive is opened.</summary>
    private static ZipArchiveEntry[] ReadEntries(ZipArchive archive)
    {
        try
        {
            return [.. archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name))];
        }
        catch (InvalidDataException exception)
        {
            throw PackageInvalid("The package's central directory is not readable.", exception);
        }
    }

    /// <summary>The delivered consumer's archive checks, in its order; no artifact content is read.</summary>
    private static async Task<(RestoreManifest Manifest, ZipArchiveEntry ArtifactEntry)> ValidateEntriesAsync(
        ZipArchive archive,
        string expectedPackageId,
        string expectedPackageVersion,
        CancellationToken cancellationToken
    )
    {
        ZipArchiveEntry[] files = ReadEntries(archive);
        RequireUniqueNames(files);

        ZipArchiveEntry manifestEntry = RequireSingle(
            files,
            entry => NameIs(entry, RestoreManifest.FileName),
            $"The package must contain exactly one {RestoreManifest.FileName}"
        );
        RestoreManifest manifest = ReadManifest(
            await ReadEntryAsync(manifestEntry, MaxManifestEntryBytes, cancellationToken)
        );
        RequireManifestIdentity(manifest, expectedPackageId, expectedPackageVersion);

        ZipArchiveEntry nuspecEntry = RequireSingle(
            files,
            entry => HasExtension(entry, NuspecExtension),
            "The package must contain exactly one .nuspec"
        );
        RequireNuspecIdentity(
            await ReadEntryAsync(nuspecEntry, MaxNuspecEntryBytes, cancellationToken),
            expectedPackageId,
            expectedPackageVersion
        );

        ZipArchiveEntry artifactEntry = RequireSingle(
            files,
            entry => NameIs(entry, manifest.ArtifactFileName),
            "The package must contain exactly one entry named by the manifest's artifactFileName"
        );
        RequireNoUndeclaredArtifact(files, artifactEntry);

        return (manifest, artifactEntry);
    }

    /// <summary>Entry names are unique after separator normalization, ignoring case.</summary>
    private static void RequireUniqueNames(ZipArchiveEntry[] files)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in files)
        {
            if (!names.Add(entry.FullName.Replace('\\', '/').ToLowerInvariant()))
            {
                throw PackageInvalid("The package holds more than one archive entry with the same name.");
            }
        }
    }

    /// <summary>Exactly one entry satisfies <paramref name="match"/>; the refusal names the requirement and the count.</summary>
    private static ZipArchiveEntry RequireSingle(
        ZipArchiveEntry[] files,
        Func<ZipArchiveEntry, bool> match,
        string requirement
    )
    {
        ZipArchiveEntry[] matches = [.. files.Where(match)];
        return matches.Length == 1
            ? matches[0]
            : throw PackageInvalid($"{requirement}; found {matches.Length}.");
    }

    /// <summary>
    /// The manifest names the resolved identity, both members compared ignoring case as the delivered consumer
    /// (<c>bootstrap-restore.psm1</c>), the attestation, and the nuspec do (6.2 step 6). The pin keeps the
    /// allowlisted spelling, so this comparison never changes what a retry compares.
    /// </summary>
    private static void RequireManifestIdentity(
        RestoreManifest manifest,
        string expectedPackageId,
        string expectedPackageVersion
    )
    {
        if (
            !SameIgnoringCase(manifest.PackageId, expectedPackageId)
            || !SameIgnoringCase(manifest.PackageVersion, expectedPackageVersion)
        )
        {
            throw new TemplatePackageException(
                TemplatePackageDefect.IdentityMismatch,
                "The restore manifest declares a package identity different from the resolved one."
            );
        }
    }

    /// <summary>The nuspec names the resolved identity, both members compared ignoring case as the delivered consumer does.</summary>
    private static void RequireNuspecIdentity(
        ReadOnlyMemory<byte> nuspecXml,
        string expectedPackageId,
        string expectedPackageVersion
    )
    {
        (string? nuspecId, string? nuspecVersion) = ReadNuspecIdentity(nuspecXml);
        if (
            nuspecId is null
            || nuspecVersion is null
            || !SameIgnoringCase(nuspecId, expectedPackageId)
            || !SameIgnoringCase(nuspecVersion, expectedPackageVersion)
        )
        {
            throw new TemplatePackageException(
                TemplatePackageDefect.IdentityMismatch,
                "The .nuspec declares a package identity different from the resolved one."
            );
        }
    }

    /// <summary>No <c>.sql</c> or <c>.bak</c> entry exists beside the manifest-declared artifact.</summary>
    private static void RequireNoUndeclaredArtifact(ZipArchiveEntry[] files, ZipArchiveEntry artifactEntry)
    {
        int undeclared = files.Count(entry =>
            !ReferenceEquals(entry, artifactEntry)
            && Array.Exists(_artifactExtensions, extension => HasExtension(entry, extension))
        );
        if (undeclared > 0)
        {
            throw PackageInvalid(
                $"The package contains {undeclared} database artifact entries beyond the manifest-declared one."
            );
        }
    }

    private static RestoreManifest ReadManifest(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            return RestoreManifestReader.Read(utf8Json);
        }
        catch (RestoreManifestException exception)
        {
            throw new TemplatePackageException(
                TemplatePackageDefect.ManifestInvalid,
                exception.Message,
                exception
            );
        }
    }

    /// <summary>The <c>package/metadata/id</c> and <c>version</c> texts, matched by local name in any namespace; null when absent.</summary>
    private static (string? Id, string? Version) ReadNuspecIdentity(ReadOnlyMemory<byte> utf8Xml)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersFromEntities = 0,
        };

        XDocument document;
        try
        {
            using MemoryStream stream = new(utf8Xml.ToArray(), writable: false);
            using XmlReader reader = XmlReader.Create(stream, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new TemplatePackageException(
                TemplatePackageDefect.PackageInvalid,
                "The .nuspec is not well-formed XML.",
                exception
            );
        }

        XElement? metadata =
            document.Root is { } root && root.Name.LocalName == "package"
                ? root.Elements().FirstOrDefault(element => element.Name.LocalName == "metadata")
                : null;
        if (metadata is null)
        {
            throw PackageInvalid("The .nuspec has no package/metadata element.");
        }

        return (ElementText(metadata, "id"), ElementText(metadata, "version"));
    }

    private static string? ElementText(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName)?.Value;

    /// <summary>
    /// Reads one entry fully, refusing one whose declared or actual size exceeds <paramref name="maxBytes"/> and
    /// one the reader cannot open or decompress (a corrupt local header, an unsupported compression method,
    /// a corrupt compressed stream).
    /// </summary>
    private static async Task<ReadOnlyMemory<byte>> ReadEntryAsync(
        ZipArchiveEntry entry,
        long maxBytes,
        CancellationToken cancellationToken
    )
    {
        if (entry.Length > maxBytes)
        {
            throw PackageInvalid("A package entry is larger than the contract allows.");
        }

        try
        {
            await using Stream source = await entry.OpenAsync(cancellationToken);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw PackageInvalid("A package entry is larger than the contract allows.");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }

            return buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
        }
        catch (InvalidDataException exception)
        {
            throw PackageInvalid("A package entry could not be read from the archive.", exception);
        }
    }

    private static TemplatePackageException PackageInvalid(
        string message,
        Exception? innerException = null
    ) => new(TemplatePackageDefect.PackageInvalid, message, innerException);

    private static bool NameIs(ZipArchiveEntry entry, string fileName) =>
        SameIgnoringCase(entry.Name, fileName);

    private static bool HasExtension(ZipArchiveEntry entry, string extension) =>
        entry.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static bool SameIgnoringCase(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}
