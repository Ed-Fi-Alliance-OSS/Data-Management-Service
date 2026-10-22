// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// The example a developer copies to appsettings.Development.json points DMS at a Configuration Service
/// reached over plain http, so it must opt out of https metadata explicitly. Without the override, a
/// shipped default of true makes a direct Development run fail at startup.
/// </summary>
/// <remarks>
/// Read from the source tree rather than the build output, because the example is never copied there.
/// </remarks>
[TestFixture]
[Parallelizable]
public class Given_the_shipped_development_example
{
    private IConfigurationRoot _configuration = null!;

    [SetUp]
    public void Setup()
    {
        _configuration = new ConfigurationBuilder()
            .SetBasePath(
                Path.Combine(
                    FrontendStagingProbe.RepositoryRoot,
                    "dms",
                    "frontend",
                    "EdFi.DataManagementService.Frontend.AspNetCore"
                )
            )
            .AddJsonFile("appsettings.Development.json.example", optional: false)
            .Build();
    }

    [Test]
    public void It_points_metadata_at_a_plain_http_address()
    {
        new Uri(_configuration["JwtAuthentication:MetadataAddress"] ?? string.Empty)
            .Scheme.Should()
            .Be(Uri.UriSchemeHttp);
    }

    [Test]
    public void It_disables_https_metadata_for_the_http_metadata_address()
    {
        // The JSON provider stores booleans as "False"; read it the way the options binder does. The
        // fallback is the secure default, so an example without the key fails here.
        _configuration
            .GetValue("JwtAuthentication:RequireHttpsMetadata", defaultValue: true)
            .Should()
            .BeFalse();
    }
}
