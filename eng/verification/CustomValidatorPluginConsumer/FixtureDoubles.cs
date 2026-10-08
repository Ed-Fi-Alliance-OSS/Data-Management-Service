// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Harness types, not samples. Nothing here is embedded in the readme, and an implementer writes
// none of it: a real plugin is handed the host's own IConfiguration.
//
// Only configuration is doubled, because only configuration needs to be. The service collection
// does not: Microsoft.Extensions.DependencyInjection.Abstractions carries the concrete
// ServiceCollection alongside IServiceCollection, so Program.cs passes a real one. What that
// package does NOT carry is BuildServiceProvider, which lives in the
// Microsoft.Extensions.DependencyInjection implementation package and is outside the five this
// project is allowed to declare.
//
// That is why the options assertions invoke the registered IConfigureOptions<T> instances directly
// instead of resolving IOptions<T> from a container. The framework's own options machinery runs
// exactly those objects in exactly that order, so running them is the same work a container would
// have done, with nothing added to the closure.
//
// Microsoft.Extensions.Primitives appears in one signature below. It arrives transitively with
// Microsoft.Extensions.Configuration.Abstractions and is named only because IConfiguration's own
// member returns it; no sample touches it.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace CustomValidatorPluginConsumer;

/// <summary>
/// A configuration root carrying one section of literal values, which is what the sample's
/// <c>configuration.GetSection("StudentIdentity")</c> call needs to bind against.
/// </summary>
internal sealed class FixtureConfiguration(string sectionName, IReadOnlyDictionary<string, string> values)
    : IConfiguration
{
    public string? this[string key]
    {
        get => null;
        set => throw new NotSupportedException("The fixture configuration is read-only.");
    }

    public IEnumerable<IConfigurationSection> GetChildren() =>
        [new FixtureConfigurationSection(sectionName, sectionName, value: null, values)];

    // Never called while binding. Named here because IConfiguration declares it.
    public IChangeToken GetReloadToken() =>
        throw new NotSupportedException("The fixture configuration does not reload.");

    public IConfigurationSection GetSection(string key) =>
        string.Equals(key, sectionName, StringComparison.OrdinalIgnoreCase)
            ? new FixtureConfigurationSection(key, key, value: null, values)
            : new FixtureConfigurationSection(
                key,
                key,
                value: null,
                values: new Dictionary<string, string>()
            );
}

/// <summary>
/// One configuration section. A parent section carries children and no value; a leaf carries a
/// value and no children. Both traversals the binder can take are served: reading the children of
/// a section, and asking a section for a named child directly.
/// </summary>
internal sealed class FixtureConfigurationSection(
    string key,
    string path,
    string? value,
    IReadOnlyDictionary<string, string> values
) : IConfigurationSection
{
    public string? this[string childKey]
    {
        get => values.TryGetValue(childKey, out string? found) ? found : null;
        set => throw new NotSupportedException("The fixture configuration is read-only.");
    }

    public string Key => key;

    public string Path => path;

    public string? Value
    {
        get => value;
        set => throw new NotSupportedException("The fixture configuration is read-only.");
    }

    public IEnumerable<IConfigurationSection> GetChildren() =>
        [
            .. values.Select(entry => new FixtureConfigurationSection(
                entry.Key,
                $"{path}:{entry.Key}",
                entry.Value,
                new Dictionary<string, string>()
            )),
        ];

    public IChangeToken GetReloadToken() =>
        throw new NotSupportedException("The fixture configuration does not reload.");

    public IConfigurationSection GetSection(string childKey) =>
        new FixtureConfigurationSection(
            childKey,
            $"{path}:{childKey}",
            values.TryGetValue(childKey, out string? found) ? found : null,
            new Dictionary<string, string>()
        );
}
