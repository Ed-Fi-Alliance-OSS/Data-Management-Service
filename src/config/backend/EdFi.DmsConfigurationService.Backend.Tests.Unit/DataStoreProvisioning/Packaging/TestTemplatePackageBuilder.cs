// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Trust;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;

/// <summary>
/// Builds a template package the way the delivered tooling lays one out (F-P2): a zip with the artifact, the
/// version-1 <c>restore-manifest.json</c> (the Pester sample with the artifact hash filled in) and a
/// <c>.nuspec</c> at the root, plus the packaging boilerplate entries a real nupkg carries; writes it to a
/// catalog directory under the <c>&lt;PackageId&gt;.&lt;Version&gt;.nupkg</c> name with its sibling
/// <c>.attestation.json</c> signed by an ephemeral key, and a trust policy listing that key. Every
/// <c>With…</c> method alters exactly one thing so a test can violate one rule at a time. The builder is public
/// so the engine integration projects can reuse it through a project reference.
/// </summary>
public sealed class TestTemplatePackageBuilder
{
    public const string DefaultProducerName = "test-producer";

    private readonly JsonObject _manifest;
    private readonly List<(string Name, byte[] Bytes)> _entries = [];
    private readonly List<Action<JsonObject>> _manifestMutations = [];
    private readonly List<Action<byte[]>> _zipMutations = [];
    private byte[] _artifactBytes;
    private string? _manifestText;
    private string? _nuspecId;
    private string? _nuspecVersion;
    private bool _omitManifest;
    private bool _omitNuspec;
    private bool _omitArtifact;
    private byte[]? _rawPackageBytes;
    private string? _attestedSha256;
    private bool _policyListsAnotherKey;
    private string _producerName = DefaultProducerName;
    private EphemeralAttestationSigner? _sharedSigner;

    public TestTemplatePackageBuilder(bool mssql = false)
    {
        _manifest = mssql
            ? RestoreManifestSample.MssqlManifest()
            : RestoreManifestSample.PostgresqlManifest();
        _artifactBytes = Encoding.UTF8.GetBytes(
            mssql ? "TAPE\0fake backup bytes\n" : "--\n-- PostgreSQL database dump (fake)\n--\n"
        );
        PackageId = (string)_manifest["packageId"]!;
        PackageVersion = (string)_manifest["packageVersion"]!;
    }

    /// <summary>The identity the package is published under: its file name, attestation and nuspec. A manifest mutation does not change it.</summary>
    public string PackageId { get; private set; }

    public string PackageVersion { get; private set; }

    public string ArtifactFileName => (string)_manifest["artifactFileName"]!;

    public string ProducerName => _sharedSigner?.ProducerName ?? _producerName;

    public TestTemplatePackageBuilder WithArtifact(byte[] artifactBytes)
    {
        _artifactBytes = artifactBytes;
        return this;
    }

    /// <summary>Publishes the package under this version everywhere: file name, attestation, nuspec and manifest.</summary>
    public TestTemplatePackageBuilder WithPackageVersion(string version)
    {
        PackageVersion = version;
        _manifest["packageVersion"] = version;
        return this;
    }

    /// <summary>Publishes the package under this id everywhere: file name, attestation, nuspec and manifest.</summary>
    public TestTemplatePackageBuilder WithPackageId(string id)
    {
        PackageId = id;
        _manifest["packageId"] = id;
        return this;
    }

    /// <summary>
    /// Signs with this producer instead of a fresh ephemeral one, so several packages built into one catalog
    /// directory share one key and one policy. The builder does not dispose a shared signer.
    /// </summary>
    public TestTemplatePackageBuilder WithSigner(EphemeralAttestationSigner signer)
    {
        _sharedSigner = signer;
        return this;
    }

    /// <summary>Alters the manifest after the artifact hash was filled in, so a test can also break that hash.</summary>
    public TestTemplatePackageBuilder WithManifest(Action<JsonObject> mutate)
    {
        _manifestMutations.Add(mutate);
        return this;
    }

    /// <summary>Writes this text as the manifest entry instead of a document.</summary>
    public TestTemplatePackageBuilder WithManifestText(string text)
    {
        _manifestText = text;
        return this;
    }

    /// <summary>Adds an entry; an existing name produces a duplicate entry, which the zip format permits.</summary>
    public TestTemplatePackageBuilder WithEntry(string name, byte[] bytes)
    {
        _entries.Add((name, bytes));
        return this;
    }

    public TestTemplatePackageBuilder WithEntry(string name, string text) =>
        WithEntry(name, Encoding.UTF8.GetBytes(text));

    public TestTemplatePackageBuilder WithoutManifest()
    {
        _omitManifest = true;
        return this;
    }

    public TestTemplatePackageBuilder WithoutNuspec()
    {
        _omitNuspec = true;
        return this;
    }

    public TestTemplatePackageBuilder WithoutArtifact()
    {
        _omitArtifact = true;
        return this;
    }

    public TestTemplatePackageBuilder WithNuspecIdentity(string id, string version)
    {
        _nuspecId = id;
        _nuspecVersion = version;
        return this;
    }

    public TestTemplatePackageBuilder WithProducer(string producerName)
    {
        _producerName = producerName;
        return this;
    }

    /// <summary>Writes these bytes as the package instead of a zip; the attestation still covers them.</summary>
    public TestTemplatePackageBuilder WithRawPackageBytes(byte[] bytes)
    {
        _rawPackageBytes = bytes;
        return this;
    }

    /// <summary>
    /// Alters the finished zip bytes in place before the attestation is signed over them, so the package
    /// authenticates while its container is malformed (see <see cref="ZipLayout"/>).
    /// </summary>
    public TestTemplatePackageBuilder WithZipMutation(Action<byte[]> mutate)
    {
        _zipMutations.Add(mutate);
        return this;
    }

    /// <summary>Signs an attestation over this hash instead of the package's.</summary>
    public TestTemplatePackageBuilder WithAttestedSha256(string sha256)
    {
        _attestedSha256 = sha256;
        return this;
    }

    /// <summary>Writes a policy whose only key is not the signing key, so the attestation's key is unknown.</summary>
    public TestTemplatePackageBuilder WithPolicyListingAnotherKey()
    {
        _policyListsAnotherKey = true;
        return this;
    }

    /// <summary>Writes the package, the attestation and the policy into <paramref name="catalogDirectory"/>.</summary>
    public TestTemplatePackage Build(string catalogDirectory)
    {
        Directory.CreateDirectory(catalogDirectory);
        byte[] packageBytes = _rawPackageBytes ?? BuildZip();
        foreach (Action<byte[]> mutate in _zipMutations)
        {
            mutate(packageBytes);
        }

        string packageSha256 = Sha256Hex(packageBytes);

        string packagePath = Path.Combine(catalogDirectory, $"{PackageId}.{PackageVersion}.nupkg");
        File.WriteAllBytes(packagePath, packageBytes);

        EphemeralAttestationSigner? ownedSigner = _sharedSigner is null ? new(_producerName) : null;
        EphemeralAttestationSigner signer = _sharedSigner ?? ownedSigner!;
        try
        {
            return Publish(packageSha256, packagePath, signer);
        }
        finally
        {
            ownedSigner?.Dispose();
        }
    }

    private TestTemplatePackage Publish(
        string packageSha256,
        string packagePath,
        EphemeralAttestationSigner signer
    )
    {
        string catalogDirectory = Path.GetDirectoryName(packagePath)!;
        string attestationJson = signer.Attest(PackageId, PackageVersion, _attestedSha256 ?? packageSha256);
        string attestationPath = packagePath + ".attestation.json";
        File.WriteAllText(attestationPath, attestationJson, new UTF8Encoding(false));

        string policyJson = _policyListsAnotherKey ? AnotherKeyPolicyJson() : signer.PolicyJson();
        string policyPath = Path.Combine(catalogDirectory, "template-trust-policy.json");
        File.WriteAllText(policyPath, policyJson, new UTF8Encoding(false));

        return new TestTemplatePackage(
            packagePath,
            attestationPath,
            attestationJson,
            policyPath,
            policyJson,
            TemplateTrustPolicyReader.Read(policyJson),
            PackageId,
            PackageVersion,
            packageSha256,
            ProducerName,
            _artifactBytes,
            Sha256Hex(_artifactBytes),
            ArtifactFileName
        );
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string AnotherKeyPolicyJson()
    {
        using EphemeralAttestationSigner other = new(ProducerName);
        return other.PolicyJson();
    }

    private byte[] BuildZip()
    {
        _manifest["artifactSha256"] = Sha256Hex(_artifactBytes);
        foreach (Action<JsonObject> mutate in _manifestMutations)
        {
            mutate(_manifest);
        }

        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "_rels/.rels", Encoding.UTF8.GetBytes(Rels));
            Add(archive, "[Content_Types].xml", Encoding.UTF8.GetBytes(ContentTypes));
            if (!_omitNuspec)
            {
                Add(
                    archive,
                    $"{PackageId}.nuspec",
                    Encoding.UTF8.GetBytes(Nuspec(_nuspecId ?? PackageId, _nuspecVersion ?? PackageVersion))
                );
            }

            if (!_omitManifest)
            {
                Add(
                    archive,
                    "restore-manifest.json",
                    Encoding.UTF8.GetBytes(_manifestText ?? _manifest.ToJsonString())
                );
            }

            if (!_omitArtifact)
            {
                Add(archive, ArtifactFileName, _artifactBytes);
            }

            foreach ((string name, byte[] bytes) in _entries)
            {
                Add(archive, name, bytes);
            }
        }

        return stream.ToArray();
    }

    private static void Add(ZipArchive archive, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using Stream target = entry.Open();
        target.Write(bytes);
    }

    private static string Nuspec(string id, string version) =>
        $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <authors>Ed-Fi Alliance</authors>
                <description>Test template package.</description>
              </metadata>
            </package>
            """;

    private const string Rels = """
        <?xml version="1.0" encoding="utf-8"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships" />
        """;

    private const string ContentTypes = """
        <?xml version="1.0" encoding="utf-8"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types" />
        """;
}
