// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>
/// Builds the fixture's namespace, seed person and grant settings, so a case states the mapping it
/// wants instead of repeating configuration keys. Every context lists both route qualifiers, because
/// the fixture matches the complete set.
/// </summary>
internal sealed class IdentityFixtureSettings
{
    private readonly Dictionary<string, string> _settings = [];
    private int _namespaces;
    private int _grants;

    /// <summary>The settings built so far, for <see cref="IdentityPluginHost.Create"/>.</summary>
    public Dictionary<string, string> Settings => _settings;

    /// <summary>Maps a namespace from one or more contexts. Qualifier values are given as district and school year.</summary>
    public IdentityFixtureSettings Namespace(
        string name,
        params (string Tenant, string DistrictId)[] contexts
    ) =>
        NamespaceWithYear(
            name,
            [
                .. contexts.Select(context =>
                    (context.Tenant, context.DistrictId, IdentityTestClients.SchoolYear)
                ),
            ]
        );

    public IdentityFixtureSettings NamespaceWithYear(
        string name,
        params (string Tenant, string DistrictId, string SchoolYear)[] contexts
    ) =>
        NamespaceWithQualifiers(
            name,
            [
                .. contexts.Select(context =>
                    (
                        context.Tenant,
                        (IReadOnlyDictionary<string, string>)
                            new Dictionary<string, string>
                            {
                                ["districtId"] = context.DistrictId,
                                ["schoolYear"] = context.SchoolYear,
                            }
                    )
                ),
            ]
        );

    /// <summary>
    /// Maps a namespace from contexts whose qualifier set is stated exactly, so a case can map a
    /// context that is missing or adds a qualifier.
    /// </summary>
    public IdentityFixtureSettings NamespaceWithQualifiers(
        string name,
        params (string Tenant, IReadOnlyDictionary<string, string> Qualifiers)[] contexts
    )
    {
        string prefix = $"IdentityFixture:Namespaces:{_namespaces++}";
        _settings[$"{prefix}:Name"] = name;

        for (int index = 0; index < contexts.Length; index++)
        {
            string contextPrefix = $"{prefix}:Contexts:{index}";
            _settings[$"{contextPrefix}:Tenant"] = contexts[index].Tenant;

            foreach ((string qualifier, string value) in contexts[index].Qualifiers)
            {
                _settings[$"{contextPrefix}:Qualifiers:{qualifier}"] = value;
            }
        }

        return this;
    }

    /// <summary>Seeds a person with an explicit id into the namespace added by the most recent <c>Namespace</c> call.</summary>
    public IdentityFixtureSettings Seed(
        string uniqueId,
        string lastSurname,
        string firstName,
        int seedIndex = 0
    )
    {
        string prefix = $"IdentityFixture:Namespaces:{_namespaces - 1}:SeedPersons:{seedIndex}";
        _settings[$"{prefix}:UniqueId"] = uniqueId;
        _settings[$"{prefix}:Attributes:LastSurname"] = lastSurname;
        _settings[$"{prefix}:Attributes:FirstName"] = firstName;
        return this;
    }

    /// <summary>A configuration grant, which the fixture reads only when no control address is set.</summary>
    public IdentityFixtureSettings Grant(string clientId, string tenant, string namespaceName)
    {
        string prefix = $"IdentityFixture:Grants:{_grants++}";
        _settings[$"{prefix}:ClientId"] = clientId;
        _settings[$"{prefix}:Tenant"] = tenant;
        _settings[$"{prefix}:Namespace"] = namespaceName;
        return this;
    }

    public IdentityFixtureSettings With(string key, string value)
    {
        _settings[key] = value;
        return this;
    }

    /// <summary>Takes policy and job state from the control stub.</summary>
    public IdentityFixtureSettings WithControlStub(IdentityFixtureControlStub stub) =>
        With("IdentityFixture:ControlBaseAddress", stub.BaseAddress.ToString())
            .With("IdentityFixture:PolicyCacheSeconds", "0");
}
