// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// Supplies both JwtBearer schemes with their validation keys from the shared snapshot (spec D-2, §4.3.7). It is a
/// <b>plain</b> <see cref="IConfigurationManager{T}"/>, not a <c>BaseConfigurationManager</c>. The handler therefore
/// awaits <see cref="GetConfigurationAsync"/> with the request's abort token and copies its issuer and keys into that
/// request's validation parameters. It does not hand the manager to IdentityModel, whose last-known-good fallback is
/// rejected for this design (V-2, V-3, I-5). Nothing here makes an HTTP request.
/// </summary>
public sealed class SigningKeyConfigurationManager(
    ISigningKeySnapshotProvider provider,
    IOptions<IdentityOptions> identityOptions
) : IConfigurationManager<OpenIdConnectConfiguration>
{
    private readonly Lock _sync = new();
    private VersionedConfiguration? _current;

    /// <summary>
    /// Returns the configuration for the usable snapshot. There is one configuration per snapshot version, built when
    /// that version is first seen: its issuer is the configured authority, the issuer the self-served discovery document
    /// declared before, and its keys come from one <see cref="SigningKeySnapshot.CreateSecurityKeys"/> call. The keys are
    /// detached, so the manager owns them and later versions are unaffected by anything done to them. A caller holding an
    /// older snapshot than the one already built gets the newer configuration.
    /// </summary>
    /// <param name="cancel">Cancels only this caller's wait for a load, never the shared load (§4.4).</param>
    /// <exception cref="SigningKeysUnavailableException">No usable snapshot exists.</exception>
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        SigningKeySnapshot snapshot = await provider.GetUsableAsync(cancel);

        VersionedConfiguration? current = Volatile.Read(ref _current);
        if (current is not null && current.Version >= snapshot.Version)
        {
            return current.Configuration;
        }

        lock (_sync)
        {
            current = _current;
            if (current is null || current.Version < snapshot.Version)
            {
                current = new VersionedConfiguration(snapshot.Version, Build(snapshot));
                Volatile.Write(ref _current, current);
            }

            return current.Configuration;
        }
    }

    /// <summary>
    /// Deliberately starts nothing. The handler calls this only when <c>RefreshOnIssuerKeyNotFound</c> is on, and both
    /// schemes turn it off (Q14): the request boundary has already made the one cooldown-limited unknown-key refresh the
    /// design allows, with the token's key id. A refresh started here would have no key id, so it would bypass the
    /// cooldown and could load once per request (I-6).
    /// </summary>
    public void RequestRefresh() { }

    private OpenIdConnectConfiguration Build(SigningKeySnapshot snapshot)
    {
        OpenIdConnectConfiguration configuration = new() { Issuer = identityOptions.Value.Authority };
        foreach (var key in snapshot.CreateSecurityKeys())
        {
            configuration.SigningKeys.Add(key);
        }

        return configuration;
    }

    private sealed record VersionedConfiguration(long Version, OpenIdConnectConfiguration Configuration);
}
