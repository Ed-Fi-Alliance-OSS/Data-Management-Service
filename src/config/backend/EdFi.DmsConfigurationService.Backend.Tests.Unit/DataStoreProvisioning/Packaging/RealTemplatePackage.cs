// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Trust;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;

/// <summary>
/// A template package from the public feed, placed in <c>DMS_TEMPLATE_PACKAGE_DIRECTORY</c> exactly as downloaded
/// (Step 5.1). Feed packages carry no attestation until signing is live (F-J4), so a lane signs its own copy with an
/// ephemeral key and writes a trust policy listing that key: the path an operator takes with their own key, through
/// the real catalog, with no product bypass. The deployment's <c>Compatibility</c> values come from the package's
/// manifest; when <c>DMS_EXPECTED_EFFECTIVE_SCHEMA_HASH</c> is set, <see cref="Find"/> refuses a package whose manifest
/// declares another hash, so a lane never runs against a package built for another schema.
/// </summary>
public sealed class RealTemplatePackage
{
    public const string DirectoryVariable = "DMS_TEMPLATE_PACKAGE_DIRECTORY";

    public const string ExpectedEffectiveSchemaHashVariable = "DMS_EXPECTED_EFFECTIVE_SCHEMA_HASH";

    public const string ProducerName = "dms-template-lane";

    private RealTemplatePackage(string sourcePath, RestoreManifest manifest)
    {
        SourcePath = sourcePath;
        Manifest = manifest;
    }

    public static string? PackageDirectory => Environment.GetEnvironmentVariable(DirectoryVariable);

    public static string? ExpectedEffectiveSchemaHash =>
        Environment.GetEnvironmentVariable(ExpectedEffectiveSchemaHashVariable);

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(PackageDirectory);

    /// <summary>The downloaded package, never written to.</summary>
    public string SourcePath { get; }

    public RestoreManifest Manifest { get; }

    public string PackageId => Manifest.PackageId;

    public string PackageVersion => Manifest.PackageVersion;

    /// <summary>
    /// The one package in the directory whose manifest declares <paramref name="databaseEngine"/> and
    /// <paramref name="templateKind"/>. Packages without a restore manifest are passed over; none, or more than one,
    /// is a lane set-up error.
    /// </summary>
    public static RealTemplatePackage Find(string databaseEngine, string templateKind)
    {
        string directory = IsConfigured
            ? PackageDirectory!
            : throw new InvalidOperationException($"{DirectoryVariable} is not set.");
        RealTemplatePackage[] matches =
        [
            .. Directory
                .EnumerateFiles(directory, "*.nupkg")
                .Select(Open)
                .OfType<RealTemplatePackage>()
                .Where(package =>
                    package.Manifest.DatabaseEngine == databaseEngine
                    && package.Manifest.TemplateKind == templateKind
                ),
        ];
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"{DirectoryVariable} must hold exactly one {databaseEngine} {templateKind} package; it holds {matches.Length}."
            );
        }

        string? expectedHash = ExpectedEffectiveSchemaHash;
        if (
            !string.IsNullOrWhiteSpace(expectedHash)
            && !string.Equals(matches[0].Manifest.EffectiveSchemaHash, expectedHash, StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException(
                $"The {databaseEngine} {templateKind} package declares effective schema hash {matches[0].Manifest.EffectiveSchemaHash}; {ExpectedEffectiveSchemaHashVariable} is {expectedHash}."
            );
        }

        return matches[0];
    }

    /// <summary>The package with its manifest, or null when it carries none.</summary>
    private static RealTemplatePackage? Open(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        ZipArchiveEntry? entry = archive.GetEntry(RestoreManifest.FileName);
        if (entry is null)
        {
            return null;
        }

        using Stream stream = entry.Open();
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return new RealTemplatePackage(path, RestoreManifestReader.Read(buffer.ToArray()));
    }

    /// <summary>Every <c>Compatibility</c> value a deployment of this package declares, taken from its manifest.</summary>
    public DmsDataStoreProvisioningOptions.CompatibilityExpectations Compatibility() =>
        new()
        {
            DataStandardVersion = Manifest.DataStandardVersion,
            EffectiveSchemaHash = Manifest.EffectiveSchemaHash,
            Projects = [.. Manifest.Projects],
            ApiSchemaFormatVersion = Manifest.ApiSchemaFormatVersion,
            RelationalMappingVersion = Manifest.RelationalMappingVersion,
        };

    /// <summary>
    /// Copies the package into <paramref name="catalogDirectory"/> under the name the catalog derives from the
    /// allowlist, writes the sibling attestation <paramref name="signer"/> signs over the copy's SHA-256, and the trust
    /// policy listing that key.
    /// </summary>
    public RealTemplatePublication PublishTo(string catalogDirectory, EphemeralAttestationSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);
        Directory.CreateDirectory(catalogDirectory);
        string packagePath = Path.Combine(catalogDirectory, $"{PackageId}.{PackageVersion}.nupkg");
        File.Copy(SourcePath, packagePath);

        string packageSha256;
        using (FileStream package = File.OpenRead(packagePath))
        {
            packageSha256 = Convert.ToHexStringLower(SHA256.HashData(package));
        }

        UTF8Encoding utf8 = new(false);
        File.WriteAllText(
            packagePath + ".attestation.json",
            signer.Attest(PackageId, PackageVersion, packageSha256),
            utf8
        );
        string policyPath = Path.Combine(catalogDirectory, "template-trust-policy.json");
        File.WriteAllText(policyPath, signer.PolicyJson(), utf8);
        return new RealTemplatePublication(packagePath, policyPath, packageSha256);
    }

    /// <summary>Writes the package's artifact entry to <paramref name="path"/>.</summary>
    public void ExtractArtifactTo(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(SourcePath);
        ZipArchiveEntry entry =
            archive.GetEntry(Manifest.ArtifactFileName)
            ?? throw new InvalidOperationException(
                "The package does not carry the artifact its manifest names."
            );
        entry.ExtractToFile(path);
    }

    /// <summary>Inverts one byte in the middle of a published copy, so the attestation no longer covers it.</summary>
    public static void Tamper(string packagePath)
    {
        using FileStream package = new(packagePath, FileMode.Open, FileAccess.ReadWrite);
        long middle = package.Length / 2;
        package.Position = middle;
        int value = package.ReadByte();
        package.Position = middle;
        package.WriteByte((byte)(value ^ 0xFF));
    }
}

/// <summary>A signed copy in a catalog directory: the package, the trust policy listing its key, and the copy's hash.</summary>
public sealed record RealTemplatePublication(
    string PackagePath,
    string TrustPolicyPath,
    string PackageSha256
);
