// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

/// <summary>
/// The example a developer copies to appsettings.Development.json must opt out of https metadata
/// explicitly, so switching a local run to the plain-http Keycloak keeps authenticating once the
/// shipped default is true.
/// </summary>
/// <remarks>
/// Read from the source tree rather than the build output, because the example is never copied there.
/// </remarks>
[TestFixture]
public class Given_the_shipped_development_example
{
    private const string TestProjectFileName =
        "EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj";

    private IConfigurationRoot _configuration = null!;

    [SetUp]
    public void Setup()
    {
        _configuration = new ConfigurationBuilder().AddJsonFile(FindExampleFile(), optional: false).Build();
    }

    [Test]
    public void It_disables_https_metadata()
    {
        // The JSON provider stores booleans as "False"; read it the way the options binder does. The
        // fallback is the secure default, so an example without the key fails here.
        _configuration
            .GetValue("IdentitySettings:RequireHttpsMetadata", defaultValue: true)
            .Should()
            .BeFalse();
    }

    private static string FindExampleFile()
    {
        string directory = AppContext.BaseDirectory;

        while (!File.Exists(Path.Combine(directory, TestProjectFileName)))
        {
            directory =
                Path.GetDirectoryName(directory)
                ?? throw new InvalidOperationException(
                    $"{TestProjectFileName} was not found above {AppContext.BaseDirectory}."
                );
        }

        return Path.GetFullPath(
            Path.Combine(
                directory,
                "..",
                "EdFi.DmsConfigurationService.Frontend.AspNetCore",
                "appsettings.Development.json.example"
            )
        );
    }
}
