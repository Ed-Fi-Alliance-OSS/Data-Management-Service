// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.Api.Plugins;

/// <summary>
/// The production contract's 1.0.0 public surface, plus the one added no-op virtual that makes this
/// 1.1.0.
/// </summary>
/// <remarks>
/// Every member below other than <see cref="DescribeCapabilities"/> is the production declaration, and
/// a reflection test asserts that: the production surface must be a subset of this one, member for
/// member and signature for signature. Change the production contract and that test fails here rather
/// than letting the compatibility proof pass against a surface that is no longer the contract's.
/// </remarks>
public abstract class EdFiApiPlugin
{
    /// <summary>
    /// The plugin's name, which must equal the name of the directory the plugin was loaded from.
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Contributes service registrations to the host's container before it is built.
    /// </summary>
    /// <param name="services">The host's service collection, as it stands before the container is built.</param>
    /// <param name="configuration">The host's own configuration, fully layered.</param>
    public virtual void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        // Intentionally does nothing, exactly as the production 1.0.0 body does.
    }

    /// <summary>
    /// The 1.1.0 addition: a new virtual with a no-op body, which is the only evolution the contract's
    /// additive-only policy permits.
    /// </summary>
    /// <remarks>
    /// A plugin compiled against 1.0.0 has never heard of it and does not override it, so it runs
    /// unchanged on a 1.1.0 host. Its presence on the type the host loaded is what a test reads to show
    /// the plugin really was served the newer contract rather than its own copy.
    /// </remarks>
    public virtual void DescribeCapabilities()
    {
        // Intentionally does nothing. A new virtual with a no-op body is binary-compatible with every
        // plugin already published against 1.0.0, which is the whole reason the policy allows it.
    }
}
