// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// Why no usable snapshot exists.
/// </summary>
public enum SigningKeysUnavailableReason
{
    /// <summary>No snapshot has been published.</summary>
    NoSnapshot,

    /// <summary>The current snapshot is older than the maximum staleness.</summary>
    SnapshotExpired,
}

/// <summary>
/// No usable signing-key snapshot exists and the load attempt that would provide one failed or was refused by the
/// retry gate. A <see cref="AuthenticationDependencyCategory.SigningKeyStore"/> dependency failure: the request
/// boundary answers it with 503.
/// </summary>
public sealed class SigningKeysUnavailableException : AuthenticationDependencyUnavailableException
{
    /// <exception cref="ArgumentException"><paramref name="outcome"/> is a success.</exception>
    public SigningKeysUnavailableException(
        SigningKeysUnavailableReason reason,
        SigningKeyRefreshOutcome outcome
    )
        : base(
            AuthenticationDependencyCategory.SigningKeyStore,
            MessageFor(reason, outcome),
            (outcome as SigningKeyRefreshOutcome.Failed)?.Exception
        )
    {
        Reason = reason;
        Outcome = outcome;
    }

    public SigningKeysUnavailableReason Reason { get; }

    /// <summary>The load outcome that left the keys unavailable: a failure or a refusal, never a success.</summary>
    public SigningKeyRefreshOutcome Outcome { get; }

    private static string MessageFor(SigningKeysUnavailableReason reason, SigningKeyRefreshOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        string state = reason switch
        {
            SigningKeysUnavailableReason.NoSnapshot => "No signing keys have been loaded",
            SigningKeysUnavailableReason.SnapshotExpired =>
                "The signing keys are older than the maximum staleness",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown reason."),
        };

        string cause = outcome switch
        {
            SigningKeyRefreshOutcome.Failed { Kind: SigningKeyFailureKind.Retrieval } =>
                "the key store could not be read",
            SigningKeyRefreshOutcome.Failed { Kind: SigningKeyFailureKind.Processing } =>
                "no key record could be used",
            SigningKeyRefreshOutcome.Refused => "a new load is not allowed before the retry deadline",
            _ => throw new ArgumentException(
                "A successful load cannot leave the signing keys unavailable.",
                nameof(outcome)
            ),
        };

        return $"{state}, and {cause}.";
    }
}
