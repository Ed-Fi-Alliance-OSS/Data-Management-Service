// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// The other half of what an implementer writes: the plugin that carries the validator into a host.
// The region below is mirrored verbatim onto the how-to page, and the same published bytes are
// loaded by this repository's own integration suite over real HTTP.

// embed-region: plugin
using EdFi.Api.Plugins;
using EdFi.DataManagementService.CustomValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Acme.UniqueIdValidation;

public sealed class UniqueIdValidationPlugin : EdFiApiPlugin
{
    // Must equal the name of the directory this plugin is published into and named by in
    // Plugins:Allowed. The host treats a mismatch as fatal.
    public override string Name => "Acme.UniqueIdValidation";

    public override void ContributeServices(
        IServiceCollection services,
        IConfiguration configuration
    )
    {
        // The section-binding overload, from Microsoft.Extensions.Options.ConfigurationExtensions.
        // There is no Action<TOptions> default registered ahead of it, unlike the sample in
        // CUSTOM-VALIDATION.md: every default here already lives on the options type itself
        // (Timeout), and BaseAddress deliberately has none, so a deployment that configures nothing
        // fails clearly rather than silently calling an address nobody chose.
        services.Configure<UniqueIdValidationOptions>(
            configuration.GetSection("UniqueIdValidation")
        );

        services
            .AddHttpClient(UniqueIdValidator.HttpClientName)
            .ConfigureHttpClient(
                (serviceProvider, client) =>
                {
                    UniqueIdValidationOptions options = serviceProvider
                        .GetRequiredService<IOptions<UniqueIdValidationOptions>>()
                        .Value;

                    // This action runs each time the validator asks the factory for a client,
                    // which is once per matching write. So a deployment that has not set the
                    // address answers each matching write with a logged 500 naming the setting,
                    // and every other write keeps working. A deployment that would rather refuse
                    // to start can validate the options at startup instead.
                    if (options.BaseAddress is not { IsAbsoluteUri: true } baseAddress)
                    {
                        throw new InvalidOperationException(
                            "UniqueIdValidation:BaseAddress must be configured as an absolute URI."
                        );
                    }

                    // A base address without a trailing slash would have HttpClient replace its
                    // last path segment instead of appending to it, following the ordinary rules
                    // for combining a base URI with a relative one. Normalizing here, rather than
                    // asking every deployment to remember the trailing slash, is what makes both
                    // forms of a configured address work.
                    string raw = baseAddress.OriginalString;
                    client.BaseAddress = new Uri(
                        raw.EndsWith('/') ? raw : raw + "/",
                        UriKind.Absolute
                    );
                    client.Timeout = options.Timeout;
                }
            )
            // Redirects are not followed. The default handler follows them, and a lookup that
            // is redirected to a sign-in or landing page answering 200 would then read as "this
            // UniqueId exists". With this off, a redirect is an answer outside the contract and
            // faults like any other.
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler { AllowAutoRedirect = false }
            )
            // The factory's default logging writes each request URI at Information, and the
            // UniqueId is in that URI's path, which its redaction leaves intact. A UniqueId is
            // student or staff data taken from the request body, so it must not reach the host's
            // logs; removing this client's loggers keeps it out while other clients keep theirs.
            .RemoveAllLoggers();

        // The registration shape DMS's startup guard accepts: TryAddEnumerable, Transient,
        // unkeyed, and an implementation type. TryAddEnumerable is required because it adds to the
        // collection rather than replacing it, so an earlier plugin's validator survives this call
        // and this one survives a later plugin's.
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<ICustomResourceValidator, UniqueIdValidator>()
        );
    }
}
// embed-region-end: plugin
