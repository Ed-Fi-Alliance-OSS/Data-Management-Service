// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// Which 6.9c validation set applies and what the identity row must hold. The identity rule is the one
/// point where the delivered consumer's rule (the restored value must differ from the package's) and the
/// provisioner's rule (the target must carry the expected identity) both live, selected by the stage.
/// </summary>
public abstract record DmsTargetValidationRule
{
    private protected DmsTargetValidationRule() { }

    private static Guid Require(Guid identity, string parameterName) =>
        identity == Guid.Empty
            ? throw new ArgumentException("The identity must not be empty.", parameterName)
            : identity;

    /// <summary>
    /// The staging database before rename (6.3 step 6): the full set, with the identity any valid non-empty
    /// GUID that differs from <see cref="ExpectedSourceIdentity"/> (the package value is captured here; the
    /// marker is assigned later).
    /// </summary>
    public sealed record Staging : DmsTargetValidationRule
    {
        public Staging(Guid expectedSourceIdentity)
        {
            ExpectedSourceIdentity = Require(expectedSourceIdentity, nameof(expectedSourceIdentity));
        }

        public Guid ExpectedSourceIdentity { get; }
    }

    /// <summary>A present target under reconciliation (6.5 step 3): the full set, with the identity equal to <see cref="ExpectedSourceIdentity"/>.</summary>
    public sealed record Reconcile : DmsTargetValidationRule
    {
        public Reconcile(Guid expectedSourceIdentity)
        {
            ExpectedSourceIdentity = Require(expectedSourceIdentity, nameof(expectedSourceIdentity));
        }

        public Guid ExpectedSourceIdentity { get; }
    }

    /// <summary>
    /// The renamed target before the marker (6.3 step 9): items 1 and 2 only, with the identity still equal
    /// to <see cref="PackageSourceIdentity"/>, the value captured on the staging database. Any difference
    /// means the instance changed.
    /// </summary>
    public sealed record AfterRename : DmsTargetValidationRule
    {
        public AfterRename(Guid packageSourceIdentity)
        {
            PackageSourceIdentity = Require(packageSourceIdentity, nameof(packageSourceIdentity));
        }

        public Guid PackageSourceIdentity { get; }
    }
}

/// <summary>The verdict of <see cref="DmsTargetValidator.Validate"/>.</summary>
public abstract record DmsTargetValidation
{
    public sealed record Valid() : DmsTargetValidation;

    /// <summary>The first failing check in 6.9c order, as the failure code it maps to and the fixed check name for <c>Diagnostic.Operation</c>.</summary>
    public sealed record Invalid(DmsProvisioningFailureCode Code, string Check) : DmsTargetValidation;
}
