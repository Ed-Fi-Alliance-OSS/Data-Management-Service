// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;

/// <summary>
/// An allowlisted template package that authenticated and matched the configuration, staged in its
/// operation's private directory with its pin. It stays open, holding the package copy through
/// the stager's exclusive handle, until <see cref="Release"/>: the create path extracts the artifact from it
/// and keeps it through the restore. <see cref="Release"/> closes the handle, deletes the
/// directory on a best-effort basis, and reports what remains as residue (<c>PackageCopy</c>,
/// <c>ExtractedArtifact</c>, <c>OperationDirectory</c>) for the cleanup report; it never throws for a
/// deletion failure, and a later call retries the deletion while residue remains, so bounded cleanup (D-19)
/// can call it again. Disposal is a release.
/// </summary>
public sealed class TrustedTemplateArtifact : IDisposable
{
    private readonly StagedTemplatePackage _staged;
    private readonly Action<PrivateOperationDirectory>? _beforeRelease;
    private readonly ILogger _logger;
    private IReadOnlyList<DmsCleanupResidue>? _residue;

    internal TrustedTemplateArtifact(
        StagedTemplatePackage staged,
        DmsTrustedArtifactPin pin,
        Action<PrivateOperationDirectory>? beforeRelease,
        ILogger logger
    )
    {
        _staged = staged;
        Pin = pin;
        _beforeRelease = beforeRelease;
        _logger = logger;
    }

    /// <summary>The pin the caller persists and presents on every retry (D-8).</summary>
    public DmsTrustedArtifactPin Pin { get; }

    public DmsOperationId OperationId => _staged.Directory.OperationId;

    public RestoreManifest Manifest => _staged.Manifest;

    /// <summary>The trust policy producer whose key verified the attestation, in the policy's spelling.</summary>
    public string Producer => _staged.Producer;

    /// <summary>The absolute path of the operation directory; for the engine adapters, never for a result or a log.</summary>
    public string OperationDirectoryPath => _staged.Directory.Path;

    public bool IsReleased => _residue is not null;

    /// <summary>
    /// Extracts the declared artifact once into the operation directory while verifying its hash.
    /// A refusal is raised as <see cref="DmsProvisioningFailedException"/> carrying <c>ArtifactTampered</c> or
    /// <c>ManifestInvalid</c> for the operation boundary, and a file system failure writing the artifact (a full
    /// disk, a refused write) as <c>StagingFailed</c> with only its type chain; cancellation passes through.
    /// </summary>
    public async Task<StagedArtifact> ExtractArtifactAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _staged.ExtractArtifactAsync(cancellationToken);
        }
        catch (TemplatePackageException exception)
        {
            throw new DmsProvisioningFailedException(
                TemplateArtifactCatalog.FailureFor(exception),
                exception
            );
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DmsProvisioningFailedException(
                DmsProvisioningFailure.Create(
                    DmsProvisioningFailureCode.StagingFailed,
                    DmsProvisioningDiagnostic.From(exception, TemplateArtifactCatalog.ExtractArtifactStep)
                ),
                exception
            );
        }
    }

    /// <summary>
    /// Closes the package handle and deletes the operation directory; returns what could not be removed. While
    /// residue remains, each call attempts the deletion again and reports what remains after it.
    /// </summary>
    public IReadOnlyList<DmsCleanupResidue> Release() => Release(logResidue: true);

    /// <summary>
    /// The release, with the residue log left to the caller's last attempt: bounded cleanup (D-19) retries
    /// while residue remains and logs only what the final attempt could not remove.
    /// </summary>
    internal IReadOnlyList<DmsCleanupResidue> Release(bool logResidue)
    {
        if (_residue is { Count: 0 })
        {
            return _residue;
        }

        string directoryPath = _staged.Directory.Path;
        if (_residue is null)
        {
            _beforeRelease?.Invoke(_staged.Directory);
            _staged.Dispose();
        }
        else
        {
            _staged.Directory.TryDelete();
        }

        _residue = TemplateArtifactCatalog.ResidueUnder(directoryPath);
        if (logResidue)
        {
            TemplateArtifactCatalog.LogResidue(_logger, _residue, OperationId);
        }

        return _residue;
    }

    public void Dispose() => Release();
}
