// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Secrets;

/// <summary>
/// Resolves a named secret for a tenant, for example by reading it from a vault.
/// <para>
/// <b>Cardinality.</b> This is a replace contract with zero or one implementation, and the
/// Configuration Service registers no default: the absence of a registration is how the host knows
/// no resolver is installed. Register an implementation with a plain <c>Add</c>, such as
/// <c>services.AddSingleton&lt;ISecretResolver, MyResolver&gt;()</c>, and never with a
/// <c>TryAdd</c>. A replace contract has one claimant, so there is nothing to try.
/// </para>
/// <para>
/// <b>Lifetime and keying.</b> The implementation must be registered as a singleton and unkeyed.
/// The host resolves this contract unkeyed from its root provider, so a keyed registration would
/// replace nothing and a scoped or transient one does not match how the host consumes it.
/// </para>
/// <para>
/// <b>Tenant.</b> The tenant is an argument, carried on <see cref="SecretReference.Tenant"/>,
/// because a plugin instance is constructed once per process and outlives every tenant. Read the
/// tenant from the reference on each call and never from static or injected ambient state.
/// </para>
/// <para>
/// <b>Caching.</b> An implementation may cache the vault client, its connection, and its
/// ambient-credential token. It must not return a secret value it did not just fetch, because the
/// host caches values in front of the resolver and the rotation window an operator configures is
/// the host's.
/// </para>
/// </summary>
public interface ISecretResolver
{
    /// <summary>
    /// Fetches the current value of the named secret for the reference's tenant.
    /// </summary>
    /// <param name="reference">The secret's name and the tenant it is being resolved for.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The secret value, fetched by this call.</returns>
    ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken);
}
