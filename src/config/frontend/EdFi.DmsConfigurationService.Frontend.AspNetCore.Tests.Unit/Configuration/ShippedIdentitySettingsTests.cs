// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Extensions;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.DataModel.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Configuration;

/// <summary>
/// A host started from the shipped appsettings.json must refuse to fetch OIDC metadata over plain
/// http unless an environment-specific override says otherwise. Reading the file from the build
/// output, rather than through the host, is what makes the shipped value visible: the running host
/// under test reads appsettings.Test.json.
/// </summary>
[TestFixture]
public class Given_the_shipped_identity_settings
{
    private IConfigurationRoot _shippedConfiguration = null!;

    [SetUp]
    public void Setup()
    {
        _shippedConfiguration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();
    }

    [Test]
    public void It_requires_https_metadata()
    {
        // The JSON provider stores booleans as "True"; read it the way the options binder does. The
        // fallback is the insecure value, so a file without the key fails here too.
        _shippedConfiguration
            .GetValue("IdentitySettings:RequireHttpsMetadata", defaultValue: false)
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_binds_https_metadata_as_required()
    {
        // The call ConfigureIdentityProvider makes to build the settings authentication uses.
        IdentitySettings settings = _shippedConfiguration
            .GetSection("IdentitySettings")
            .Get<IdentitySettings>()!;

        settings.RequireHttpsMetadata.Should().BeTrue();
    }
}

/// <summary>
/// Every IdentitySettings key in the shipped appsettings.json must be read by the code. "Read" is
/// defined by the readers themselves, not by a list of key names: the configuration provider records
/// each key a reader asks for, so a key the file carries that no reader asks for fails here, and so
/// does a reader whose key string is renamed while the file keeps the old name, unless another
/// reader still asks for that key: Authority and Audience are read by two binders, so renaming one
/// of them in a single binder passes here. The readers run here are the section's three binders: the
/// OpenIddict identity options binder, the frontend IdentitySettings class, and the client secret
/// validation options. Code elsewhere that reads a single key directly, such as the Authority, reads
/// a key one of these binders also reads.
/// </summary>
[TestFixture]
public class Given_the_shipped_identity_settings_keys
{
    private const string SectionPrefix = "IdentitySettings:";

    private List<string> _unreadKeys = null!;

    [SetUp]
    public void Setup()
    {
        var readKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .Add(
                new RecordingJsonConfigurationSource(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    readKeys
                )
            )
            .Build();

        List<string> shippedKeys =
        [
            .. configuration
                .AsEnumerable()
                .Where(entry =>
                    entry.Key.StartsWith(SectionPrefix, StringComparison.OrdinalIgnoreCase)
                    && entry.Value is not null
                )
                .Select(entry => entry.Key),
        ];

        // Enumerating the configuration reads every key, so forget those reads before the readers run.
        readKeys.Clear();

        using ServiceProvider provider = new ServiceCollection()
            .AddOpenIddictIdentityOptions(configuration)
            .BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<IdentityOptions>>().Value;

        // The calls ConfigureIdentityProvider and the client secret validation registration make.
        _ = configuration.GetSection("IdentitySettings").Get<IdentitySettings>();
        _ = configuration
            .GetSection(ClientSecretValidationOptions.SectionName)
            .Get<ClientSecretValidationOptions>();

        _unreadKeys = [.. shippedKeys.Where(key => !readKeys.Contains(key))];
    }

    [Test]
    public void It_reads_every_shipped_identity_settings_key()
    {
        // BeEmpty names only the first item, so list every unread key in the reason.
        _unreadKeys
            .Should()
            .BeEmpty("no reader reads these shipped keys: {0}", string.Join(", ", _unreadKeys));
    }

    private sealed class RecordingJsonConfigurationSource(string path, HashSet<string> readKeys)
        : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            var source = new JsonConfigurationSource { Path = path, Optional = false };
            source.ResolveFileProvider();
            return new RecordingJsonConfigurationProvider(source, readKeys);
        }
    }

    private sealed class RecordingJsonConfigurationProvider(
        JsonConfigurationSource source,
        HashSet<string> readKeys
    ) : JsonConfigurationProvider(source)
    {
        public override bool TryGet(string key, out string? value)
        {
            readKeys.Add(key);
            return base.TryGet(key, out value);
        }
    }
}
