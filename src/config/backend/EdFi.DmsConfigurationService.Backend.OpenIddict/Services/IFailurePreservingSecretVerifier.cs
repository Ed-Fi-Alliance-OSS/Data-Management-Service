// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Services;

/// <summary>
/// Optional capability of a client secret hasher: verify a secret without suppressing failures.
/// <para>
/// <see cref="EdFi.DmsConfigurationService.Secrets.IClientSecretHasher.VerifySecretAsync"/> answers
/// <c>false</c> both for a wrong secret and for a verification that could not run at all (a
/// misconfigured iteration count, an unreadable stored hash). Token revocation must tell those
/// apart: the first is an authentication outcome (<c>invalid_client</c>), the second an operational
/// one (<c>temporarily_unavailable</c>), so a caller is never told its credentials are wrong when
/// the service simply could not check them (DMS-1327 D-07, D-13).
/// </para>
/// <para>
/// Deliberately not part of the plugin replace contract: a replacement hasher that does not
/// implement this is used through <c>VerifySecretAsync</c>, with whatever failure semantics it has.
/// </para>
/// </summary>
public interface IFailurePreservingSecretVerifier
{
    /// <summary>
    /// Verifies <paramref name="plainTextSecret"/> against <paramref name="hashedSecret"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the secret matches; <c>false</c> for a genuine mismatch, including an empty
    /// presented secret or an application with no stored secret.
    /// </returns>
    /// <exception cref="Exception">
    /// Any failure that prevented the comparison from being made. Implementations must not log the
    /// exception or either secret; the caller classifies the failure and logs exception type names
    /// only.
    /// </exception>
    Task<bool> VerifySecretPreservingFailuresAsync(string plainTextSecret, string hashedSecret);
}
