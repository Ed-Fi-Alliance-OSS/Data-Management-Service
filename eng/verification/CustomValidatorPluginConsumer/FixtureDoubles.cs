// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Harness types, not samples. Nothing here is embedded in the readme, and an implementer writes
// none of it: a real plugin is handed the host's own IServiceCollection and IConfiguration.
//
// They exist because of a closure constraint worth recording. The concrete ServiceCollection class
// and BuildServiceProvider live in Microsoft.Extensions.DependencyInjection, the implementation
// package, which is NOT one of the five this project is allowed to declare: the samples reach DI
// only through Microsoft.Extensions.DependencyInjection.Abstractions, and adding the implementation
// package to build a container would make this project's closure wider than the implementer's it
// is supposed to model.
//
// So instead of resolving through a container, Program.cs invokes the real ContributeServices
// against the minimal IServiceCollection below, then reads the IConfigureOptions<T> registrations
// back out and invokes them directly. That exercises the same objects the framework's own options
// machinery would invoke, in the same order, with nothing added to the closure.
//
// Microsoft.Extensions.Primitives appears in one signature below. It arrives transitively with
// Microsoft.Extensions.Configuration.Abstractions and is named only because IConfiguration's own
// member returns it; no sample touches it.

using System.Collections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace CustomValidatorPluginConsumer;

/// <summary>
/// The smallest thing that is honestly an <see cref="IServiceCollection"/>: the interface is
/// <see cref="IList{T}"/> of <see cref="ServiceDescriptor"/> and nothing more, so a list is a
/// complete implementation rather than a stub that pretends.
/// </summary>
internal sealed class FixtureServiceCollection : IServiceCollection
{
    private readonly List<ServiceDescriptor> _descriptors = [];

    public ServiceDescriptor this[int index]
    {
        get => _descriptors[index];
        set => _descriptors[index] = value;
    }

    public int Count => _descriptors.Count;

    public bool IsReadOnly => false;

    public void Add(ServiceDescriptor item) => _descriptors.Add(item);

    public void Clear() => _descriptors.Clear();

    public bool Contains(ServiceDescriptor item) => _descriptors.Contains(item);

    public void CopyTo(ServiceDescriptor[] array, int arrayIndex) => _descriptors.CopyTo(array, arrayIndex);

    public IEnumerator<ServiceDescriptor> GetEnumerator() => _descriptors.GetEnumerator();

    public int IndexOf(ServiceDescriptor item) => _descriptors.IndexOf(item);

    public void Insert(int index, ServiceDescriptor item) => _descriptors.Insert(index, item);

    public bool Remove(ServiceDescriptor item) => _descriptors.Remove(item);

    public void RemoveAt(int index) => _descriptors.RemoveAt(index);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

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
