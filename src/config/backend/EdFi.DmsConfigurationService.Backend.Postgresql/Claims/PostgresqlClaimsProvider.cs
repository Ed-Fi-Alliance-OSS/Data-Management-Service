// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DmsConfigurationService.Backend.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Claims;

/// <summary>
/// PostgreSQL-specific ClaimsProvider that loads Claims.json from the PostgreSQL assembly
/// </summary>
public class PostgresqlClaimsProvider : ClaimsProvider
{
    private readonly ILogger<PostgresqlClaimsProvider> _logger;

    public PostgresqlClaimsProvider(
        ILogger<PostgresqlClaimsProvider> logger,
        IOptions<ClaimsOptions> claimsOptions,
        IClaimsValidator claimsValidator,
        IClaimsFragmentComposer claimsFragmentComposer
    )
        : base(logger, claimsOptions, claimsValidator, claimsFragmentComposer)
    {
        _logger = logger;
    }

    /// <summary>
    /// Overrides the assembly loading to use the PostgreSQL assembly
    /// </summary>
    protected override Assembly GetAssemblyForEmbeddedResource()
    {
        // Get the PostgreSQL assembly instead of the executing assembly
        var assembly = typeof(PostgresqlClaimsProvider).Assembly;
        _logger.LogInformation(
            "Loading Claims.json from PostgreSQL assembly: {AssemblyName}",
            assembly.GetName().Name
        );
        return assembly;
    }
}
