// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;

/// <summary>
/// A throw-away file system for one fixture: a catalog directory the builder writes packages into and an
/// operations root the workspace claims operation directories under, as the catalog does. Disposal removes
/// everything.
/// </summary>
internal sealed class StagerWorkspace : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dms1438-stager-" + Guid.NewGuid().ToString("N")
    );

    public StagerWorkspace()
    {
        PrivateTestDirectory.Create(CatalogDirectory);
        PrivateTestDirectory.Create(OperationsRoot);
    }

    public string CatalogDirectory => Path.Combine(_root, "catalog");

    public string OperationsRoot => Path.Combine(_root, "operations");

    public DmsOperationId OperationId { get; } = DmsOperationId.New();

    public string OperationDirectoryPath =>
        Path.Combine(OperationsRoot, PrivateOperationDirectory.NamePrefix + OperationId.Value);

    public TestTemplatePackage Build(TestTemplatePackageBuilder builder) => builder.Build(CatalogDirectory);

    public TestTemplatePackage Build() => Build(new TestTemplatePackageBuilder());

    /// <summary>
    /// Claims the operation directory and stages <paramref name="package"/> into it with its own attestation and
    /// policy unless overridden. As the directory's owner, the workspace deletes it when staging fails or is
    /// cancelled; on success the returned package owns it.
    /// </summary>
    public async Task<StagedTemplatePackage> StageAsync(
        TestTemplatePackage package,
        string? attestationJson = null,
        string? expectedPackageId = null,
        string? expectedPackageVersion = null,
        TemplateTrustPolicy? policy = null,
        CancellationToken cancellationToken = default
    )
    {
        PrivateOperationDirectory directory = PrivateOperationDirectory.Create(OperationsRoot, OperationId);
        try
        {
            return await TemplatePackageStager.StageAsync(
                directory,
                package.PackagePath,
                attestationJson ?? package.AttestationJson,
                expectedPackageId ?? package.PackageId,
                expectedPackageVersion ?? package.PackageVersion,
                policy ?? package.Policy,
                cancellationToken
            );
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    /// <summary>The exception staging <paramref name="package"/> throws.</summary>
    public async Task<Exception> StagingFailureOf(
        TestTemplatePackage package,
        string? attestationJson = null,
        string? expectedPackageVersion = null
    )
    {
        try
        {
            using StagedTemplatePackage staged = await StageAsync(
                package,
                attestationJson,
                expectedPackageVersion: expectedPackageVersion
            );
            throw new InvalidOperationException("Staging was expected to fail but succeeded.");
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            return exception;
        }
    }

    public bool OperationDirectoryExists => Directory.Exists(OperationDirectoryPath);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
