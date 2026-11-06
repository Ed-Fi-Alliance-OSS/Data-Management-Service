// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Manifest;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Trust;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Catalog;

/// <summary>
/// The local directory catalog (D-3) and the artifact resolution of 6.2, with no server contact. For a
/// template it takes the allowlist entry from configuration, locates <c>&lt;PackageId&gt;.&lt;Version&gt;.nupkg</c>
/// and its sibling <c>.attestation.json</c> under <c>ArtifactDirectory</c> by those names alone, reads the trust
/// policy from <c>TrustPolicyPath</c> for this operation, and hands the package to
/// <see cref="TemplatePackageStager"/>, which copies it into the operation's private directory, hashes the
/// copy, verifies the attestation in the F-P6 order before opening the archive, and enforces the entry rules
/// and the manifest contract. The catalog then performs the remaining configuration comparisons of 6.2 step 6
/// in that order (engine, template kind, Data Standard version, effective schema hash, project set, the
/// optional format and mapping expectations, the <c>DocumentJson</c> baseline type) and builds the pin.
/// Every refusal is one failure code with a fixed operation name that says which check failed and never a
/// value from a file, and every path that releases the private directory reports what it could not remove
/// as residue. The directory root is <c>OperationDirectory</c> when configured, else the process temp path;
/// the catalog never creates the root and never touches an entry it did not claim.
/// </summary>
public sealed class TemplateArtifactCatalog : ITemplateArtifactCatalog
{
    public const string PackageExtension = ".nupkg";

    /// <summary>The detached attestation is the sibling <c>&lt;nupkg file name&gt;.attestation.json</c> (F-P3).</summary>
    public const string AttestationExtension = ".attestation.json";

    /// <summary>The largest attestation document read; real documents are well under a kilobyte.</summary>
    public const long MaxAttestationBytes = 1024 * 1024;

    /// <summary>The largest trust policy read.</summary>
    public const long MaxTrustPolicyBytes = 1024 * 1024;

    // Fixed step names carried in DmsProvisioningDiagnostic.Operation; a check name follows a dot.
    public const string LocateArtifactStep = "LocateArtifact";
    public const string TrustPolicyStep = "TrustPolicy";
    public const string StagePackageStep = "StagePackage";
    public const string VerifyAttestationStep = "VerifyAttestation";
    public const string ReadPackageStep = "ReadPackage";
    public const string MatchPackageIdentityStep = "MatchPackageIdentity";
    public const string MatchConfigurationStep = "MatchConfiguration";
    public const string ExtractArtifactStep = "ExtractArtifact";

    /// <summary>The check name reported when the trust policy names no detached-attestation producer (7.3 step 2).</summary>
    public const string NoProducersCheck = "NoProducers";

    /// <summary>The readiness probe name of 7.3 step 2.</summary>
    public const string TrustPolicyProbe = "TrustPolicy";

    /// <summary>The readiness probe name of 7.3 step 3 for one template: <c>Catalog.&lt;wire value&gt;</c>.</summary>
    public static string CatalogProbeFor(DmsDatabaseTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return "Catalog." + template.WireValue;
    }

    internal static readonly EventId ArtifactRefused = new(1438_20, nameof(ArtifactRefused));
    internal static readonly EventId ResidueLeft = new(1438_21, nameof(ResidueLeft));

    private static readonly string[] _extractedArtifactFileNames =
    [
        StagedTemplatePackage.ArtifactBaseName
            + RestoreManifest.ArtifactExtensionFor(RestoreManifest.PostgresqlEngine),
        StagedTemplatePackage.ArtifactBaseName
            + RestoreManifest.ArtifactExtensionFor(RestoreManifest.MssqlEngine),
    ];

    private readonly DmsDataStoreProvisioningOptions _options;
    private readonly TemplatePackageStager _stager;
    private readonly ILogger<TemplateArtifactCatalog> _logger;

    public TemplateArtifactCatalog(
        IOptions<DmsDataStoreProvisioningOptions> options,
        ILogger<TemplateArtifactCatalog> logger
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
        OperationRootDirectory = OperationRootDirectoryFor(_options);
        _stager = new TemplatePackageStager(OperationRootDirectory);
    }

    /// <summary>The directory every operation's private directory is claimed under.</summary>
    public string OperationRootDirectory { get; }

    /// <summary>Test seam: runs immediately before an operation directory is released, where a test can make its deletion fail.</summary>
    internal Action<PrivateOperationDirectory>? BeforeRelease { get; set; }

    /// <summary><c>OperationDirectory</c> when configured, else the process temp path, without a trailing separator.</summary>
    public static string OperationRootDirectoryFor(DmsDataStoreProvisioningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string root = string.IsNullOrWhiteSpace(options.OperationDirectory)
            ? Path.GetTempPath()
            : options.OperationDirectory;
        return Path.TrimEndingDirectorySeparator(root);
    }

    public async Task<DmsArtifactResolution> ResolveAsync(
        DmsDatabaseTemplate template,
        CancellationToken cancellationToken
    )
    {
        TemplateArtifactStaging staging = await StageAsync(template, DmsOperationId.New(), cancellationToken);
        switch (staging)
        {
            case TemplateArtifactStaging.Failure failure:
                return new DmsArtifactResolution.Failure(failure.Detail, failure.Cleanup);
            case TemplateArtifactStaging.Staged staged:
                // 6.2 step 7: the pin is complete, nothing is extracted, and the private directory goes now.
                DmsTrustedArtifactPin pin = staged.Artifact.Pin;
                IReadOnlyList<DmsCleanupResidue> residue = staged.Artifact.Release();
                return new DmsArtifactResolution.Resolved(pin, new DmsCleanupReport(residue));
            default:
                throw new InvalidOperationException("Unknown artifact staging result.");
        }
    }

    public async Task<TemplateArtifactStaging> StageAsync(
        DmsDatabaseTemplate template,
        DmsOperationId operationId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(template);
        (string packageId, string packageVersion) = AllowlistEntryFor(template);

        // 6.2 step 2: both file names derive from the allowlist alone; the request contributes the template only.
        string packagePath = Path.Combine(
            ArtifactDirectory,
            packageId + "." + packageVersion + PackageExtension
        );
        string attestationPath = packagePath + AttestationExtension;
        if (!File.Exists(packagePath) || !File.Exists(attestationPath))
        {
            return Refused(template, Failure(DmsProvisioningFailureCode.ArtifactMissing, LocateArtifactStep));
        }

        (string? attestationJson, DmsProvisioningFailure? attestationFailure) = await ReadAttestationAsync(
            attestationPath,
            cancellationToken
        );
        if (attestationFailure is not null)
        {
            return Refused(template, attestationFailure);
        }

        // 6.2 step 4: the policy is re-read for every operation; a defect is the operator's, never the package's.
        (TemplateTrustPolicy? policy, DmsProvisioningFailure? policyFailure) = await LoadTrustPolicyAsync(
            cancellationToken
        );
        if (policyFailure is not null)
        {
            return Refused(template, policyFailure);
        }

        // 6.2 step 3 (D-5). The stager's claim refuses an entry already holding the generated name and leaves it
        // alone; checking first keeps such an entry out of this operation's residue report as well.
        string directoryPath = Path.Combine(
            OperationRootDirectory,
            PrivateOperationDirectory.NamePrefix + operationId.Value
        );
        if (Path.Exists(directoryPath))
        {
            return Refused(template, Failure(DmsProvisioningFailureCode.StagingFailed, StagePackageStep));
        }

        (
            StagedTemplatePackage? staged,
            DmsProvisioningFailure? stagingFailure,
            IReadOnlyList<DmsCleanupResidue> stagingResidue
        ) = await StagePrivatelyAsync(
            packagePath,
            attestationJson!,
            packageId,
            packageVersion,
            policy!,
            operationId,
            directoryPath,
            cancellationToken
        );
        if (staged is null)
        {
            return Refused(template, stagingFailure!, new DmsCleanupReport(stagingResidue));
        }

        // 6.2 step 6: the stager proved the manifest and the nuspec name the allowlisted identity; the rest
        // compares the manifest with this deployment's configuration.
        TrustedTemplateArtifact artifact = new(
            staged,
            PinFor(staged, packageId, packageVersion),
            BeforeRelease,
            _logger
        );
        string? mismatch = FirstConfigurationMismatch(staged.Manifest, template, packageId);
        if (mismatch is null)
        {
            return new TemplateArtifactStaging.Staged(artifact);
        }

        IReadOnlyList<DmsCleanupResidue> residue = artifact.Release();
        return Refused(
            template,
            Failure(DmsProvisioningFailureCode.ManifestMismatch, MatchConfigurationStep + "." + mismatch),
            new DmsCleanupReport(residue)
        );
    }

    /// <summary>
    /// The attestation document's text, or the refusal: a file that vanished is <c>ArtifactMissing</c>, one that
    /// cannot be read is <c>StagingFailed</c>, and one larger than any version-1 document is refused as malformed
    /// before any byte is parsed.
    /// </summary>
    private static async Task<(string? Json, DmsProvisioningFailure? Failure)> ReadAttestationAsync(
        string attestationPath,
        CancellationToken cancellationToken
    )
    {
        string? json;
        try
        {
            json = await ReadBoundedTextAsync(attestationPath, MaxAttestationBytes, cancellationToken);
        }
        catch (FileNotFoundException exception)
        {
            return (null, Failure(DmsProvisioningFailureCode.ArtifactMissing, LocateArtifactStep, exception));
        }
        catch (IOException exception)
        {
            return (null, Failure(DmsProvisioningFailureCode.StagingFailed, LocateArtifactStep, exception));
        }
        catch (UnauthorizedAccessException exception)
        {
            return (null, Failure(DmsProvisioningFailureCode.StagingFailed, LocateArtifactStep, exception));
        }

        return json is null
            ? (
                null,
                Failure(
                    DmsProvisioningFailureCode.ArtifactUntrusted,
                    VerifyAttestationStep + "." + TrustRefusalReason.Malformed
                )
            )
            : (json, null);
    }

    /// <summary>
    /// 6.2 steps 3 to 5 through the stager: the staged package, or the classified refusal together with whatever
    /// the stager could not delete of the directory it claimed, which is this operation's residue (reported in
    /// the result and, on cancellation, in the log alone).
    /// </summary>
    private async Task<(
        StagedTemplatePackage? Staged,
        DmsProvisioningFailure? Failure,
        IReadOnlyList<DmsCleanupResidue> Residue
    )> StagePrivatelyAsync(
        string packagePath,
        string attestationJson,
        string packageId,
        string packageVersion,
        TemplateTrustPolicy policy,
        DmsOperationId operationId,
        string directoryPath,
        CancellationToken cancellationToken
    )
    {
        StagedTemplatePackage? staged = null;
        DmsProvisioningFailure? failure = null;
        IReadOnlyList<DmsCleanupResidue> residue = [];
        try
        {
            staged = await _stager.StageAsync(
                packagePath,
                attestationJson,
                packageId,
                packageVersion,
                policy,
                operationId,
                cancellationToken
            );
        }
        catch (TemplatePackageException exception)
        {
            failure = FailureFor(exception);
        }
        catch (FileNotFoundException exception)
        {
            failure = Failure(DmsProvisioningFailureCode.ArtifactMissing, StagePackageStep, exception);
        }
        catch (IOException exception)
        {
            failure = Failure(DmsProvisioningFailureCode.StagingFailed, StagePackageStep, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            failure = Failure(DmsProvisioningFailureCode.StagingFailed, StagePackageStep, exception);
        }
        finally
        {
            if (staged is null)
            {
                residue = ResidueUnder(directoryPath);
                LogResidue(_logger, residue, operationId);
            }
        }

        return (staged, failure, residue);
    }

    public async Task<IReadOnlyList<DmsReadinessReason>> ValidateAsync(CancellationToken cancellationToken)
    {
        (TemplateTrustPolicy? policy, DmsProvisioningFailure? policyFailure) = await LoadTrustPolicyAsync(
            cancellationToken
        );
        if (policyFailure is not null)
        {
            return [new DmsReadinessReason(policyFailure.Code, TrustPolicyProbe, policyFailure.Diagnostic)];
        }

        if (!policy!.DetachedAttestationProducers.Any())
        {
            return
            [
                new DmsReadinessReason(
                    DmsProvisioningFailureCode.StartupValidationFailed,
                    TrustPolicyProbe,
                    DmsProvisioningDiagnostic.ForOperation(TrustPolicyStep + "." + NoProducersCheck)
                ),
            ];
        }

        List<DmsReadinessReason> reasons = [];
        foreach (DmsDatabaseTemplate template in DmsDatabaseTemplate.All)
        {
            if (await ResolveAsync(template, cancellationToken) is DmsArtifactResolution.Failure failure)
            {
                reasons.Add(
                    new DmsReadinessReason(
                        failure.Detail.Code,
                        CatalogProbeFor(template),
                        failure.Detail.Diagnostic
                    )
                );
            }
        }

        return reasons;
    }

    /// <summary>The failure code and fixed operation a stager or extraction refusal maps to (spec row 5h).</summary>
    internal static DmsProvisioningFailure FailureFor(TemplatePackageException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Defect switch
        {
            TemplatePackageDefect.Untrusted => Failure(
                DmsProvisioningFailureCode.ArtifactUntrusted,
                VerifyAttestationStep + "." + exception.TrustRefusal,
                exception
            ),
            TemplatePackageDefect.PackageInvalid or TemplatePackageDefect.ManifestInvalid => Failure(
                DmsProvisioningFailureCode.ManifestInvalid,
                ReadPackageStep,
                exception
            ),
            TemplatePackageDefect.IdentityMismatch => Failure(
                DmsProvisioningFailureCode.ManifestMismatch,
                MatchPackageIdentityStep,
                exception
            ),
            TemplatePackageDefect.ArtifactTampered => Failure(
                DmsProvisioningFailureCode.ArtifactTampered,
                ExtractArtifactStep,
                exception
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(exception), "Unknown template package defect."),
        };
    }

    /// <summary>
    /// What remains of an operation directory after its deletion was attempted, as residue with generated
    /// resource ids (6.11): the package copy, an extracted artifact, and the directory itself. Empty when the
    /// directory is gone. Nothing is deleted here.
    /// </summary>
    internal static IReadOnlyList<DmsCleanupResidue> ResidueUnder(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return [];
        }

        string directoryName = Path.GetFileName(directoryPath);
        List<DmsCleanupResidue> residue = [];
        if (File.Exists(Path.Combine(directoryPath, StagedTemplatePackage.PackageCopyFileName)))
        {
            residue.Add(
                new DmsCleanupResidue(
                    DmsResidueKind.PackageCopy,
                    directoryName + "/" + StagedTemplatePackage.PackageCopyFileName
                )
            );
        }

        residue.AddRange(
            _extractedArtifactFileNames
                .Where(artifactFileName => File.Exists(Path.Combine(directoryPath, artifactFileName)))
                .Select(artifactFileName => new DmsCleanupResidue(
                    DmsResidueKind.ExtractedArtifact,
                    directoryName + "/" + artifactFileName
                ))
        );

        residue.Add(new DmsCleanupResidue(DmsResidueKind.OperationDirectory, directoryName));
        return residue;
    }

    /// <summary>D-19: every residue is also a fixed-code Error log entry; the resource id is a generated name.</summary>
    internal static void LogResidue(
        ILogger logger,
        IReadOnlyList<DmsCleanupResidue> residue,
        DmsOperationId operationId
    )
    {
        foreach (DmsCleanupResidue item in residue)
        {
            logger.LogError(
                ResidueLeft,
                "DMS data store provisioning operation {OperationId} could not remove {ResidueKind} {ResourceId}; remove it by the operator residue procedure",
                operationId.Value,
                item.Kind,
                item.ResourceId
            );
        }
    }

    private string ArtifactDirectory =>
        _options.ArtifactDirectory
        ?? throw new InvalidOperationException(
            "DmsDataStoreProvisioning:ArtifactDirectory is not configured; static validation admits no such state."
        );

    private (string PackageId, string PackageVersion) AllowlistEntryFor(DmsDatabaseTemplate template)
    {
        if (
            _options.Templates is not null
            && _options.Templates.TryGetValue(
                template.WireValue,
                out DmsDataStoreProvisioningOptions.TemplateAllowlistEntry? entry
            )
            && entry is { PackageId: not null, PackageVersion: not null }
        )
        {
            return (entry.PackageId, entry.PackageVersion);
        }

        throw new InvalidOperationException(
            $"DmsDataStoreProvisioning:Templates:{template.WireValue} is not configured; static validation admits no such state."
        );
    }

    private async Task<(TemplateTrustPolicy? Policy, DmsProvisioningFailure? Failure)> LoadTrustPolicyAsync(
        CancellationToken cancellationToken
    )
    {
        string path =
            _options.TrustPolicyPath
            ?? throw new InvalidOperationException(
                "DmsDataStoreProvisioning:TrustPolicyPath is not configured; static validation admits no such state."
            );
        string? json;
        try
        {
            json = await ReadBoundedTextAsync(path, MaxTrustPolicyBytes, cancellationToken);
        }
        catch (IOException exception)
        {
            return (
                null,
                Failure(DmsProvisioningFailureCode.StartupValidationFailed, TrustPolicyStep, exception)
            );
        }
        catch (UnauthorizedAccessException exception)
        {
            return (
                null,
                Failure(DmsProvisioningFailureCode.StartupValidationFailed, TrustPolicyStep, exception)
            );
        }

        try
        {
            if (json is null)
            {
                throw new TemplateTrustPolicyException("The trust policy is larger than the catalog reads.");
            }

            return (TemplateTrustPolicyReader.Read(json), null);
        }
        catch (TemplateTrustPolicyException exception)
        {
            return (
                null,
                Failure(DmsProvisioningFailureCode.StartupValidationFailed, TrustPolicyStep, exception)
            );
        }
    }

    /// <summary>The file's text, or null when it is larger than <paramref name="maxBytes"/>; file system failures propagate.</summary>
    private static async Task<string?> ReadBoundedTextAsync(
        string path,
        long maxBytes,
        CancellationToken cancellationToken
    )
    {
        if (new FileInfo(path).Length > maxBytes)
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    /// <summary>The first 6.2 step 6 comparison the manifest fails, by name, or null when it passes every one.</summary>
    private string? FirstConfigurationMismatch(
        RestoreManifest manifest,
        DmsDatabaseTemplate template,
        string packageId
    )
    {
        DmsDataStoreProvisioningOptions.CompatibilityExpectations compatibility =
            _options.Compatibility
            ?? throw new InvalidOperationException(
                "DmsDataStoreProvisioning:Compatibility is not configured; static validation admits no such state."
            );
        HashSet<string> projects = new(manifest.Projects, StringComparer.OrdinalIgnoreCase);

        // In the 6.2 step 6 order; the first comparison that fails names the refusal.
        (string Check, bool Matches)[] comparisons =
        [
            (nameof(manifest.DatabaseEngine), Same(manifest.DatabaseEngine, _options.Provider)),
            (nameof(manifest.TemplateKind), Same(manifest.TemplateKind, template.TemplateKind)),
            (
                nameof(manifest.DataStandardVersion),
                Same(manifest.DataStandardVersion, compatibility.DataStandardVersion)
                    && packageId.EndsWith("." + manifest.DataStandardVersion, StringComparison.Ordinal)
            ),
            (
                nameof(manifest.EffectiveSchemaHash),
                Same(manifest.EffectiveSchemaHash, compatibility.EffectiveSchemaHash)
            ),
            (nameof(manifest.Projects), projects.SetEquals(compatibility.Projects ?? [])),
            (
                nameof(manifest.ApiSchemaFormatVersion),
                MeetsOptional(manifest.ApiSchemaFormatVersion, compatibility.ApiSchemaFormatVersion)
            ),
            (
                nameof(manifest.RelationalMappingVersion),
                MeetsOptional(manifest.RelationalMappingVersion, compatibility.RelationalMappingVersion)
            ),
            (
                nameof(manifest.DocumentJsonColumnType),
                Same(
                    manifest.DocumentJsonColumnType,
                    RestoreManifest.DocumentJsonColumnTypeFor(manifest.DatabaseEngine)
                )
            ),
        ];
        return Array.Find(comparisons, comparison => !comparison.Matches).Check;
    }

    /// <summary>An optional expectation (4.3) is met when it is omitted or equal.</summary>
    private static bool MeetsOptional(string actual, string? expected) =>
        string.IsNullOrWhiteSpace(expected) || Same(actual, expected);

    /// <summary>The pin (4.2): identity in the allowlist's spelling, hashes and versions from the verified copy and its manifest.</summary>
    private static DmsTrustedArtifactPin PinFor(
        StagedTemplatePackage staged,
        string packageId,
        string packageVersion
    ) =>
        new()
        {
            ContractVersion = DmsTrustedArtifactPin.CurrentContractVersion,
            // Equal to the configured provider once the engine comparison has passed.
            Provider = staged.Manifest.DatabaseEngine,
            PackageId = packageId,
            PackageVersion = packageVersion,
            PackageSha256 = staged.PackageSha256,
            ArtifactSha256 = staged.Manifest.ArtifactSha256,
            TemplateKind = staged.Manifest.TemplateKind,
            DataStandardVersion = staged.Manifest.DataStandardVersion,
            EffectiveSchemaHash = staged.Manifest.EffectiveSchemaHash,
            InventorySha256 = staged.Manifest.InventorySha256,
            ApiSchemaFormatVersion = staged.Manifest.ApiSchemaFormatVersion,
            RelationalMappingVersion = staged.Manifest.RelationalMappingVersion,
        };

    private TemplateArtifactStaging.Failure Refused(
        DmsDatabaseTemplate template,
        DmsProvisioningFailure failure,
        DmsCleanupReport? cleanup = null
    )
    {
        _logger.LogWarning(
            ArtifactRefused,
            "DMS template artifact for template {Template} was refused at {Step} with {Code} ({Category}); exception types {ExceptionTypeChain}",
            template.WireValue,
            failure.Diagnostic.Operation,
            failure.Code,
            failure.Category,
            failure.Diagnostic.ExceptionTypeChain.Length == 0 ? "none" : failure.Diagnostic.ExceptionTypeChain
        );
        return new TemplateArtifactStaging.Failure(failure, cleanup ?? DmsCleanupReport.Clean);
    }

    private static DmsProvisioningFailure Failure(
        DmsProvisioningFailureCode code,
        string operation,
        Exception? exception = null
    ) =>
        DmsProvisioningFailure.Create(
            code,
            exception is null
                ? DmsProvisioningDiagnostic.ForOperation(operation)
                : DmsProvisioningDiagnostic.From(exception, operation)
        );

    private static bool Same(string? actual, string? expected) =>
        string.Equals(actual, expected, StringComparison.Ordinal);
}
