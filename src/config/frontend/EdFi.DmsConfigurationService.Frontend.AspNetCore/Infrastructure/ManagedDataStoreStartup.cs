// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Secrets;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>Static checks only. Environment/secret availability remains the provisioner's latched readiness verdict.</summary>
public static class ManagedDataStoreStartup
{
    public static void Validate(IServiceProvider services)
    {
        if (!services.GetRequiredService<DataStoreManagementOptions>().EnableDataStoreManagement)
        {
            return;
        }
        var provisioning = services.GetRequiredService<IOptions<DmsDataStoreProvisioningOptions>>().Value;
        if (!provisioning.Enabled)
        {
            Fail(
                "AppSettings:EnableDataStoreManagement requires DmsDataStoreProvisioning:Enabled=true. Set management explicitly false for an unconfigured deployment."
            );
        }
        // Also validates that the ordinary CMS catalog names a database we can protect.
        _ = services.GetRequiredService<ProtectedDatabaseNames>();
        var provider = services.GetRequiredService<IManagedDataStoreRuntimeConnectionProvider>();
        ValidateConnection(
            provisioning.AdminConnectionString,
            "DmsDataStoreProvisioning:AdminConnectionString",
            provider,
            services
        );
        ValidateConnection(
            services
                .GetRequiredService<IOptions<ManagedDataStoreRuntimeOptions>>()
                .Value.RuntimeConnectionString,
            "DataStoreManagement:RuntimeConnectionString",
            provider,
            services
        );
    }

    private static void ValidateConnection(
        string? value,
        string key,
        IManagedDataStoreRuntimeConnectionProvider provider,
        IServiceProvider services
    )
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Fail($"{key} is required when managed data stores are enabled.");
            return;
        }
        bool hasReferences = SecretReferenceTokens.Find(value).Count != 0;
        if (hasReferences && services.GetService<ISecretResolver>() is null)
        {
            Fail($"{key} requires an ISecretResolver for secret references.");
        }
        try
        {
            // References occur in individual values. Do not resolve them at this static boundary or
            // parse a token in a typed field (e.g. Port) with a driver before substitution.
            if (hasReferences)
            {
                var configured = new DbConnectionStringBuilder { ConnectionString = value };
                var literalFields = provider.CreateBuilder(string.Empty);
                foreach (string field in configured.Keys)
                {
                    if (SecretReferenceTokens.Find((string)configured[field]).Count == 0)
                    {
                        literalFields[field] = configured[field];
                    }
                    else
                    {
                        // Validate the keyword without asking a typed property to parse a reference.
                        _ = literalFields[field];
                    }
                }
            }
            else if (!provider.TryParse(value, out _))
            {
                Fail($"{key} must name one verifiable endpoint for the configured target provider.");
            }
        }
        catch (Exception exception)
            when (exception is ArgumentException or FormatException or OverflowException)
        {
            // Driver parser messages may repeat credentials; retain no exception or configured value.
            Fail($"{key} must be a valid connection string for the configured target provider.");
        }
    }

    private static void Fail(string message) =>
        throw new OptionsValidationException(
            nameof(DataStoreManagementOptions),
            typeof(DataStoreManagementOptions),
            [message]
        );
}
