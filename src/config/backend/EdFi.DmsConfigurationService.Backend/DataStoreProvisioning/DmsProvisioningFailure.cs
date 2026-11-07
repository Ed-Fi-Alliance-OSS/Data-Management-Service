// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// A typed, retry-classifiable provisioning failure (spec section 8). The summary is fixed text per code;
/// the diagnostic is the only variable part and carries no values. The category is the code's default
/// unless the code is one of the four context-dependent codes, whose allowed categories are pinned in
/// <see cref="CategoriesOf"/>; any other combination is a programming error and throws.
/// </summary>
public sealed record DmsProvisioningFailure
{
    private static readonly FrozenDictionary<DmsProvisioningFailureCode, Entry> _taxonomy = BuildTaxonomy();

    private DmsProvisioningFailure(
        DmsProvisioningFailureCategory category,
        DmsProvisioningFailureCode code,
        string summary,
        DmsProvisioningDiagnostic diagnostic
    )
    {
        Category = category;
        Code = code;
        Summary = summary;
        Diagnostic = diagnostic;
    }

    public DmsProvisioningFailureCategory Category { get; }

    public DmsProvisioningFailureCode Code { get; }

    /// <summary>Fixed text per code; never a provider message.</summary>
    public string Summary { get; }

    public DmsProvisioningDiagnostic Diagnostic { get; }

    /// <summary>A failure in the code's default category.</summary>
    public static DmsProvisioningFailure Create(
        DmsProvisioningFailureCode code,
        DmsProvisioningDiagnostic diagnostic
    ) => Create(code, DefaultCategoryOf(code), diagnostic);

    /// <summary>A failure in an explicitly chosen category, which must be one the code allows.</summary>
    public static DmsProvisioningFailure Create(
        DmsProvisioningFailureCode code,
        DmsProvisioningFailureCategory category,
        DmsProvisioningDiagnostic diagnostic
    )
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        Entry entry = EntryOf(code);
        if (!entry.Categories.Contains(category))
        {
            throw new ArgumentException(
                $"Failure code {code} does not allow category {category}.",
                nameof(category)
            );
        }

        return new DmsProvisioningFailure(category, code, entry.Summary, diagnostic);
    }

    /// <summary>The categories section 8 allows for the code; the first is the default.</summary>
    public static IReadOnlyList<DmsProvisioningFailureCategory> CategoriesOf(
        DmsProvisioningFailureCode code
    ) => EntryOf(code).Categories;

    public static DmsProvisioningFailureCategory DefaultCategoryOf(DmsProvisioningFailureCode code) =>
        EntryOf(code).Categories[0];

    public static string SummaryOf(DmsProvisioningFailureCode code) => EntryOf(code).Summary;

    private static Entry EntryOf(DmsProvisioningFailureCode code) =>
        _taxonomy.TryGetValue(code, out Entry? entry)
            ? entry
            : throw new ArgumentOutOfRangeException(
                nameof(code),
                $"Failure code {code} has no taxonomy entry."
            );

    private sealed record Entry(IReadOnlyList<DmsProvisioningFailureCategory> Categories, string Summary);

    private static FrozenDictionary<DmsProvisioningFailureCode, Entry> BuildTaxonomy()
    {
        const DmsProvisioningFailureCategory InvalidRequest = DmsProvisioningFailureCategory.InvalidRequest;
        const DmsProvisioningFailureCategory NotReady = DmsProvisioningFailureCategory.NotReady;
        const DmsProvisioningFailureCategory Transient = DmsProvisioningFailureCategory.Transient;
        const DmsProvisioningFailureCategory Permanent = DmsProvisioningFailureCategory.Permanent;
        const DmsProvisioningFailureCategory Inspection =
            DmsProvisioningFailureCategory.OperatorInspectionRequired;

        Dictionary<DmsProvisioningFailureCode, Entry> entries = new()
        {
            [DmsProvisioningFailureCode.FeatureDisabled] = new(
                [NotReady],
                "DMS data store provisioning is disabled."
            ),
            [DmsProvisioningFailureCode.StartupValidationFailed] = new(
                [NotReady],
                "DMS data store provisioning failed startup validation; restart after remediation."
            ),
            [DmsProvisioningFailureCode.AdapterUnavailable] = new(
                [NotReady],
                "No DMS target engine adapter exists for the configured provider."
            ),
            [DmsProvisioningFailureCode.TemplateNotAllowlisted] = new(
                [InvalidRequest],
                "The requested template is not an allowlisted DMS template."
            ),
            [DmsProvisioningFailureCode.InvalidTargetName] = new(
                [InvalidRequest],
                "The target database name is not a valid managed data store name."
            ),
            [DmsProvisioningFailureCode.EmptySourceIdentity] = new(
                [InvalidRequest],
                "The expected source identity must not be empty."
            ),
            [DmsProvisioningFailureCode.ArtifactPinMissing] = new(
                [InvalidRequest],
                "The request carries no trusted artifact pin."
            ),
            [DmsProvisioningFailureCode.ProtectedTarget] = new(
                [Permanent],
                "The target name is a protected or system database on the target server."
            ),
            [DmsProvisioningFailureCode.AdminPrivilegeMissing] = new(
                [Permanent],
                "The administrative connection lacks a privilege the operation requires."
            ),
            [DmsProvisioningFailureCode.RoleStateUnsafe] = new(
                [Permanent],
                "A required server role exists but is not in the locked-down state the template expects."
            ),
            [DmsProvisioningFailureCode.EngineIncompatible] = new(
                [Permanent],
                "The target server does not satisfy the engine requirements of the template."
            ),
            [DmsProvisioningFailureCode.StagingNotVisible] = new(
                [Permanent],
                "The SQL Server host cannot read the configured staging directory."
            ),
            [DmsProvisioningFailureCode.ServerRejected] = new(
                [Permanent],
                "The target server rejected an administrative command."
            ),
            [DmsProvisioningFailureCode.SecretResolutionFailed] = new(
                [Transient],
                "A secret referenced by the administrative connection string could not be resolved."
            ),
            [DmsProvisioningFailureCode.ServerUnavailable] = new(
                [Transient],
                "The target server could not be reached."
            ),
            [DmsProvisioningFailureCode.LockTimeout] = new(
                [Transient],
                "Another session holds the target lock; the operation will be retried later."
            ),
            [DmsProvisioningFailureCode.LockUnavailable] = new(
                [Transient],
                "The target lock could not be acquired because of a lock infrastructure failure."
            ),
            [DmsProvisioningFailureCode.TimedOut] = new(
                [Transient],
                "An operation step exceeded its timeout."
            ),
            [DmsProvisioningFailureCode.StagingFailed] = new(
                [Transient],
                "A file could not be staged privately for the operation."
            ),
            [DmsProvisioningFailureCode.StagingNameCollision] = new(
                [Transient],
                "A generated staging database name was already in use."
            ),
            [DmsProvisioningFailureCode.Unknown] = new(
                [Transient],
                "An unclassified transient failure occurred."
            ),
            [DmsProvisioningFailureCode.ExclusiveAccessNotEstablished] = new(
                [Transient, Inspection],
                "Exclusive access to the target could not be established."
            ),
            [DmsProvisioningFailureCode.LockLost] = new(
                [Transient, Inspection],
                "The target lock holder session was lost during the operation."
            ),
            [DmsProvisioningFailureCode.ArtifactMissing] = new(
                [Inspection],
                "The allowlisted package or its attestation is missing from the artifact directory."
            ),
            [DmsProvisioningFailureCode.ArtifactUntrusted] = new(
                [Inspection],
                "The package attestation did not verify against the trust policy."
            ),
            [DmsProvisioningFailureCode.ArtifactTampered] = new(
                [Inspection],
                "The extracted artifact does not match the hash the manifest declares."
            ),
            [DmsProvisioningFailureCode.ManifestInvalid] = new(
                [Inspection],
                "The package restore manifest is malformed."
            ),
            [DmsProvisioningFailureCode.ManifestMismatch] = new(
                [Inspection],
                "The package restore manifest does not match the configured expectations."
            ),
            [DmsProvisioningFailureCode.BackupInvalid] = new(
                [Inspection],
                "The SQL Server backup did not pass header or file-list validation."
            ),
            [DmsProvisioningFailureCode.PackageIdentityInvalid] = new(
                [Inspection],
                "The restored template carries an invalid source identity."
            ),
            [DmsProvisioningFailureCode.ArtifactChangedSinceCreate] = new(
                [Inspection],
                "The trusted artifact no longer matches the pin recorded at create time."
            ),
            [DmsProvisioningFailureCode.RestoreFailed] = new(
                [Permanent, Transient],
                "The template restore into the staging database failed."
            ),
            [DmsProvisioningFailureCode.ContentProfileViolation] = new(
                [Inspection],
                "The restored database violates the DMS data store content profile."
            ),
            [DmsProvisioningFailureCode.TargetIncompatible] = new(
                [Inspection],
                "The database is incompatible with the pinned artifact or the deployment expectations."
            ),
            [DmsProvisioningFailureCode.SecurityStateViolation] = new(
                [Inspection],
                "The database's ownership, privileges, or security state violate the template rules."
            ),
            [DmsProvisioningFailureCode.StagingIntegrityViolation] = new(
                [Inspection],
                "The staged backup changed between staging and restore."
            ),
            [DmsProvisioningFailureCode.IdentityAssignmentFailed] = new(
                [Inspection],
                "The source identity marker could not be assigned."
            ),
            [DmsProvisioningFailureCode.TargetUnowned] = new(
                [Inspection],
                "A database holds the target name but is not owned by this record."
            ),
            [DmsProvisioningFailureCode.TargetUnverifiable] = new(
                [Inspection],
                "A database holds the target name but its ownership cannot be verified."
            ),
            [DmsProvisioningFailureCode.TargetFenced] = new(
                [Inspection],
                "The target database is fenced and was not touched."
            ),
            [DmsProvisioningFailureCode.InstanceChanged] = new(
                [Inspection],
                "The database behind the target name changed during the operation."
            ),
            [DmsProvisioningFailureCode.OutcomeUnknown] = new(
                [Inspection],
                "The outcome of a mutating command could not be resolved."
            ),
            [DmsProvisioningFailureCode.CompensationFailed] = new(
                [Inspection],
                "The renamed target could not be removed after a failed marker assignment."
            ),
            [DmsProvisioningFailureCode.DropFailed] = new(
                [Transient, Inspection],
                "The target database could not be dropped."
            ),
        };

        return entries.ToFrozenDictionary();
    }
}
