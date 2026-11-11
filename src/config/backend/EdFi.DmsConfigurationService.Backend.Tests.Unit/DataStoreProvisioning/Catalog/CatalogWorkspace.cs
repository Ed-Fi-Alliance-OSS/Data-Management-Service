// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Trust;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Catalog;

/// <summary>
/// A throw-away deployment for one fixture: an artifact directory the builder publishes packages into (with
/// the trust policy beside them), an operations root the catalog claims operation directories under, one
/// shared signing producer so the Minimal and Sample packages verify against the same policy, and the
/// options that allowlist both packages with the compatibility expectations the sample manifest satisfies.
/// Disposal removes everything, restoring any directory mode a test changed first.
/// </summary>
internal sealed class CatalogWorkspace : IDisposable
{
    public const string MinimalPackageId = RestoreManifestSample.PostgresqlPackageId;

    public const string SamplePackageId = "EdFi.Api.Populated.Template.PostgreSql.5.2.0";

    /// <summary>The sample manifest's version; the builder publishes under it unless told otherwise.</summary>
    public const string PackageVersion = "1.0.123";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dms1438-catalog-" + Guid.NewGuid().ToString("N")
    );

    public CatalogWorkspace()
    {
        PrivateTestDirectory.Create(ArtifactDirectory);
        PrivateTestDirectory.Create(OperationsRoot);
    }

    public EphemeralAttestationSigner Signer { get; } = new(TestTemplatePackageBuilder.DefaultProducerName);

    public string ArtifactDirectory => Path.Combine(_root, "artifacts");

    public string OperationsRoot => Path.Combine(_root, "operations");

    public string TrustPolicyPath => Path.Combine(ArtifactDirectory, "template-trust-policy.json");

    public CapturingLogger<TemplateArtifactCatalog> Logger { get; } = new();

    /// <summary>Every entry under the operations root; empty when every operation released its directory.</summary>
    public string[] OperationEntries => Directory.GetFileSystemEntries(OperationsRoot);

    public string OperationDirectoryPathOf(DmsOperationId operationId) =>
        Path.Combine(OperationsRoot, PrivateOperationDirectory.NamePrefix + operationId.Value);

    public TestTemplatePackageBuilder MinimalBuilder(bool mssql = false) =>
        new TestTemplatePackageBuilder(mssql).WithSigner(Signer);

    /// <summary>The Populated package the Sample template maps to: the same sample manifest under its own id and kind.</summary>
    public TestTemplatePackageBuilder SampleBuilder() =>
        MinimalBuilder()
            .WithPackageId(SamplePackageId)
            .WithManifest(manifest => manifest["templateKind"] = "Populated");

    public TestTemplatePackage Build(TestTemplatePackageBuilder builder) => builder.Build(ArtifactDirectory);

    public (TestTemplatePackage Minimal, TestTemplatePackage Sample) BuildBoth() =>
        (Build(MinimalBuilder()), Build(SampleBuilder()));

    public void WriteTrustPolicy(string json) =>
        File.WriteAllText(TrustPolicyPath, json, new UTF8Encoding(false));

    public DmsDataStoreProvisioningOptions Options(
        string provider = DmsDataStoreProvisioningOptions.PostgresqlProvider,
        string minimalPackageId = MinimalPackageId,
        string minimalPackageVersion = PackageVersion,
        string samplePackageId = SamplePackageId
    ) =>
        new()
        {
            Enabled = true,
            Provider = provider,
            AdminConnectionString = "Host=db.internal;Username=cms_admin;Password=pw",
            TrustPolicyPath = TrustPolicyPath,
            ArtifactDirectory = ArtifactDirectory,
            OperationDirectory = OperationsRoot,
            Templates = new(StringComparer.Ordinal)
            {
                [DmsDatabaseTemplate.MinimalWireValue] = new()
                {
                    PackageId = minimalPackageId,
                    PackageVersion = minimalPackageVersion,
                },
                [DmsDatabaseTemplate.SampleWireValue] = new()
                {
                    PackageId = samplePackageId,
                    PackageVersion = PackageVersion,
                },
            },
            Compatibility = new()
            {
                DataStandardVersion = "5.2.0",
                EffectiveSchemaHash = RestoreManifestSample.EffectiveSchemaHash,
                Projects = ["edfi", "tpdm"],
            },
        };

    public TemplateArtifactCatalog Catalog(DmsDataStoreProvisioningOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? Options()), Logger);

    /// <summary>A catalog whose trust-policy reads go through <paramref name="readTrustPolicyText"/> (F-06 seam).</summary>
    public TemplateArtifactCatalog CatalogReadingPolicyThrough(
        TemplateArtifactCatalog.BoundedTextReader readTrustPolicyText,
        DmsDataStoreProvisioningOptions? options = null
    ) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? Options()), Logger)
        {
            ReadTrustPolicyText = readTrustPolicyText,
        };

    public void Dispose()
    {
        Signer.Dispose();
        if (!Directory.Exists(_root))
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            // A test may have removed write permission from an operation directory to trap its deletion.
            foreach (string directory in Directory.GetDirectories(OperationsRoot))
            {
                File.SetUnixFileMode(directory, PrivateOperationDirectory.PrivateDirectoryMode);
            }
        }

        Directory.Delete(_root, recursive: true);
    }
}
