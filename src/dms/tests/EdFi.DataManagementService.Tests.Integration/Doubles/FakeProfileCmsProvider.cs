// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Tests.Integration.Fixtures;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// Loads the profile catalog for the active <see cref="FixtureContext"/> from XML files
/// on disk and serves it as if it had come from the Configuration Management Service.
/// Profile XML is parsed eagerly through <see cref="ProfileDefinitionParser"/> so that
/// malformed fixture files surface as a clear load-time failure rather than a confusing
/// downstream parse error. Application-to-profile assignments are empty unless a scenario
/// names the profiles its application is assigned, which is what puts a request on the
/// implicit-selection path rather than the no-profiles-assigned one.
/// </summary>
internal sealed class FakeProfileCmsProvider : IProfileCmsProvider
{
    private readonly Lazy<IReadOnlyList<CmsProfileResponse>> _catalog;
    private readonly Lazy<IReadOnlyList<long>> _assignedProfileIds;

    public FakeProfileCmsProvider(FixtureContext fixture, IReadOnlyList<string>? assignedProfileNames = null)
    {
        _catalog = new Lazy<IReadOnlyList<CmsProfileResponse>>(() =>
            LoadCatalog(fixture.ProfileXmlDirectory)
        );

        IReadOnlyList<string> names = assignedProfileNames ?? [];
        _assignedProfileIds = new Lazy<IReadOnlyList<long>>(() => ResolveIds(_catalog.Value, names));
    }

    public static FakeProfileCmsProvider FromFixture(
        FixtureContext fixture,
        IReadOnlyList<string>? assignedProfileNames = null
    ) => new(fixture, assignedProfileNames);

    /// <summary>
    /// Reports the profiles assigned to the requesting application, or null when a scenario named none.
    /// </summary>
    /// <remarks>
    /// Null and an empty assignment are the same state to <c>CachedProfileService</c>, and both take the
    /// no-profiles-assigned branch. Returning the assignment for whichever application id is asked keeps
    /// scenarios from having to know the harness's application identity.
    /// </remarks>
    public Task<ApplicationProfileInfo?> GetApplicationProfileInfoAsync(
        long applicationId,
        string? tenantId
    ) =>
        Task.FromResult<ApplicationProfileInfo?>(
            _assignedProfileIds.Value.Count == 0
                ? null
                : new ApplicationProfileInfo(applicationId, _assignedProfileIds.Value)
        );

    public Task<CmsProfileResponse?> GetProfileAsync(long profileId, string? tenantId)
    {
        CmsProfileResponse? match = _catalog.Value.FirstOrDefault(p => p.Id == profileId);
        return Task.FromResult(match);
    }

    public Task<IReadOnlyList<CmsProfileResponse>> GetProfilesAsync(string? tenantId) =>
        Task.FromResult(_catalog.Value);

    /// <summary>
    /// Resolves assigned profile names against the loaded catalog.
    /// </summary>
    /// <remarks>
    /// By name rather than by id: the catalog numbers profiles by their sorted file order, so an id
    /// written into a scenario would silently point at a different profile the moment another XML file
    /// joins the fixture. An unmatched name throws instead of assigning nothing, because assigning
    /// nothing puts the request back on the no-profiles-assigned branch and would make a scenario that
    /// exists to exercise implicit selection pass without ever reaching it.
    /// </remarks>
    private static IReadOnlyList<long> ResolveIds(
        IReadOnlyList<CmsProfileResponse> catalog,
        IReadOnlyList<string> assignedProfileNames
    )
    {
        List<long> ids = new(assignedProfileNames.Count);

        foreach (string name in assignedProfileNames)
        {
            CmsProfileResponse? match = catalog.FirstOrDefault(profile =>
                string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)
            );

            if (match is null)
            {
                throw new InvalidOperationException(
                    $"Profile '{name}' was assigned to the test application but is not in the fixture "
                        + $"catalog. Available: [{string.Join(", ", catalog.Select(profile => profile.Name))}]."
                );
            }

            ids.Add(match.Id);
        }

        return ids;
    }

    private static IReadOnlyList<CmsProfileResponse> LoadCatalog(string profileXmlDirectory)
    {
        if (!Directory.Exists(profileXmlDirectory))
        {
            return [];
        }

        string[] files = Directory.GetFiles(profileXmlDirectory, "*.xml");
        Array.Sort(files, StringComparer.Ordinal);

        var profiles = new List<CmsProfileResponse>(files.Length);
        long nextId = 1;
        foreach (string path in files)
        {
            string xml = File.ReadAllText(path);
            ProfileDefinitionParseResult parsed = ProfileDefinitionParser.Parse(xml);
            if (!parsed.IsSuccess || parsed.Definition is null)
            {
                throw new InvalidOperationException(
                    $"Profile fixture '{path}' failed to parse: {parsed.ErrorMessage}"
                );
            }

            profiles.Add(new CmsProfileResponse(nextId, parsed.Definition.ProfileName, xml));
            nextId++;
        }

        return profiles;
    }
}
