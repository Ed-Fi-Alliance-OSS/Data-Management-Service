// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The read seam the host composes for this engine, built over the encryption service and tenant
/// provider a test's repository already uses. The host gives each repository its own reader, so a
/// test does the same; one reader's resolve allowance covers every read made through it. The cache is
/// off unless a test asks for one, so each read reaches the resolver.
/// </summary>
public static class TestConnectionStringReader
{
    public static ConnectionStringReader Create(
        IConnectionStringEncryptionService encryptionService,
        ITenantContextProvider tenantContextProvider,
        ISecretResolver? secretResolver = null,
        int cacheExpirationSeconds = 0,
        ILogger<ConnectionStringReader>? logger = null
    )
    {
        IOptions<SecretsOptions> options = Options.Create(
            new SecretsOptions { CacheExpirationSeconds = cacheExpirationSeconds, ResolveTimeoutSeconds = 10 }
        );

        return new ConnectionStringReader(
            encryptionService,
            new MssqlDataStoreConnectionStringValidator(),
            new SecretValueCache(options),
            options,
            tenantContextProvider,
            logger ?? NullLogger<ConnectionStringReader>.Instance,
            secretResolver
        );
    }
}
