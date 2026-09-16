// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// The other half of what a third party writes: the plugin that carries the validator into a host.
// The region below is mirrored verbatim into the packed readme.
//
// This project is a compile-and-assert check and is never loaded by a host, so its assembly name
// is CustomValidatorPluginConsumer and deliberately does not equal the sample's Name. A real
// plugin must make the directory name, the assembly name and Name one string; PLUGINS.md shows the
// project settings that do that.

// embed-region: plugin
using EdFi.Api.Plugins;
using EdFi.DataManagementService.CustomValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Acme.Dms.StudentIdentity;

public sealed class StudentIdentityPlugin : EdFiApiPlugin
{
    // Must equal the name of the directory this plugin is published into and named by in
    // Plugins:Allowed. The host treats a mismatch as fatal.
    public override string Name => "Acme.Dms.StudentIdentity";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        // The Action<TOptions> overload, from Microsoft.Extensions.Options. Registered first, so
        // it supplies the deployment-independent default.
        services.Configure<StudentIdentityOptions>(options => options.RequiredPrefix = "S");

        // The section-binding overload, from Microsoft.Extensions.Options.ConfigurationExtensions.
        // Registered second, so a deployment that sets StudentIdentity:RequiredPrefix in its own
        // configuration overrides the default above; one that sets nothing keeps it. Both forms
        // register an IConfigureOptions<StudentIdentityOptions> and they run in registration
        // order.
        services.Configure<StudentIdentityOptions>(configuration.GetSection("StudentIdentity"));

        // The registration shape DMS's startup guard accepts: TryAddEnumerable, Transient,
        // unkeyed, and an implementation type rather than a shared instance or a factory delegate.
        // TryAddEnumerable is what makes this fan-in, so every registered validator runs and any
        // number of plugins may contribute one.
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<ICustomResourceValidator, StudentIdentityValidator>()
        );
    }
}
// embed-region-end: plugin
