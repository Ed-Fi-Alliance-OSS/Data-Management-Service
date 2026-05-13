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
/// downstream parse error. Application-to-profile assignments are intentionally empty;
/// scenarios that need an assigned profile wire it in separately.
/// </summary>
internal sealed class FakeProfileCmsProvider : IProfileCmsProvider
{
    private readonly Lazy<IReadOnlyList<CmsProfileResponse>> _catalog;

    public FakeProfileCmsProvider(FixtureContext fixture)
    {
        _catalog = new Lazy<IReadOnlyList<CmsProfileResponse>>(() =>
            LoadCatalog(fixture.ProfileXmlDirectory)
        );
    }

    public static FakeProfileCmsProvider FromFixture(FixtureContext fixture) => new(fixture);

    public Task<ApplicationProfileInfo?> GetApplicationProfileInfoAsync(
        long applicationId,
        string? tenantId
    ) => Task.FromResult<ApplicationProfileInfo?>(null);

    public Task<CmsProfileResponse?> GetProfileAsync(long profileId, string? tenantId)
    {
        CmsProfileResponse? match = _catalog.Value.FirstOrDefault(p => p.Id == profileId);
        return Task.FromResult(match);
    }

    public Task<IReadOnlyList<CmsProfileResponse>> GetProfilesAsync(string? tenantId) =>
        Task.FromResult(_catalog.Value);

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
