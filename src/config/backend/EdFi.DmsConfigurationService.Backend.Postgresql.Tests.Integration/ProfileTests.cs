// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Profile;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public class ProfileTests : DatabaseTest
{
    private readonly IProfileRepository _repository = new ProfileRepository(
        Configuration.DatabaseOptions,
        NullLogger<ProfileRepository>.Instance,
        new TestAuditContext()
    );

    private async Task ResetProfiles(params string[] names)
    {
        foreach (var name in names)
        {
            await Connection!.ExecuteAsync(
                @"DELETE FROM ""dmscs"".""Profile"" WHERE ""ProfileName"" = @Name;",
                new { Name = name }
            );
        }
    }

    private static readonly string[] EarlySortedProfileNames =
    [
        "000-DMS1074-Alpha",
        "000-DMS1074-Bravo",
        "000-DMS1074-Charlie",
    ];

    private static readonly string[] LateSortedProfileNames =
    [
        "~~~DMS1074-Charlie",
        "~~~DMS1074-Alpha",
        "~~~DMS1074-Bravo",
    ];

    private static string CreateDefinition(string name) =>
        $"""
            <Profile name="{name}">
              <Resource name="School">
                <ReadContentType memberSelection="IncludeOnly">
                  <Property name="NameOfInstitution" />
                </ReadContentType>
              </Resource>
            </Profile>
            """;

    [TestFixture]
    public class QueryPagingTests : ProfileTests
    {
        [SetUp]
        public async Task Setup()
        {
            await ResetProfiles(EarlySortedProfileNames);
            foreach (var name in EarlySortedProfileNames)
            {
                var result = await _repository.InsertProfile(
                    new ProfileInsertCommand { Name = name, Definition = CreateDefinition(name) }
                );
                result.Should().BeOfType<ProfileInsertResult.Success>();
            }
        }

        [Test]
        public async Task Should_return_all_results_when_no_paging_params_provided()
        {
            var result = await _repository.QueryProfiles(
                new ProfileQuery { OrderBy = "name", Direction = "ASC" }
            );
            var names = result
                .OfType<ProfileGetResult.Success>()
                .Select(r => r.Profile.Name)
                .Where(name => name.StartsWith("000-DMS1074-"))
                .ToList();
            names.Should().ContainInOrder(EarlySortedProfileNames);
        }

        [Test]
        public async Task Should_apply_limit_when_limit_is_provided()
        {
            var result = await _repository.QueryProfiles(
                new ProfileQuery
                {
                    OrderBy = "name",
                    Direction = "ASC",
                    Limit = 2,
                }
            );
            var names = result
                .OfType<ProfileGetResult.Success>()
                .Select(r => r.Profile.Name)
                .Where(name => name.StartsWith("000-DMS1074-"))
                .ToList();
            names.Should().ContainInOrder(EarlySortedProfileNames[..2]);
        }

        [Test]
        public async Task Should_apply_offset_when_offset_is_provided()
        {
            var result = await _repository.QueryProfiles(
                new ProfileQuery
                {
                    OrderBy = "name",
                    Direction = "ASC",
                    Limit = 2,
                    Offset = 1,
                }
            );
            var names = result
                .OfType<ProfileGetResult.Success>()
                .Select(r => r.Profile.Name)
                .Where(name => name.StartsWith("000-DMS1074-"))
                .ToList();
            names.Should().ContainInOrder(EarlySortedProfileNames[1..]);
        }

        [Test]
        public async Task Should_apply_offset_after_excluding_invalid_profiles()
        {
            await ResetProfiles([
                .. EarlySortedProfileNames,
                "000-DMS1074-Invalid",
                "001-DMS1074-Valid",
                "002-DMS1074-Valid",
            ]);

            var invalidInsert = await _repository.InsertProfile(
                new ProfileInsertCommand
                {
                    Name = "000-DMS1074-Invalid",
                    Definition = @"<Profile><Resource name=""School""></Resource></Profile>",
                }
            );
            invalidInsert.Should().BeOfType<ProfileInsertResult.Success>();

            var validAInsert = await _repository.InsertProfile(
                new ProfileInsertCommand
                {
                    Name = "001-DMS1074-Valid",
                    Definition = CreateDefinition("001-DMS1074-Valid"),
                }
            );
            validAInsert.Should().BeOfType<ProfileInsertResult.Success>();

            var validBInsert = await _repository.InsertProfile(
                new ProfileInsertCommand
                {
                    Name = "002-DMS1074-Valid",
                    Definition = CreateDefinition("002-DMS1074-Valid"),
                }
            );
            validBInsert.Should().BeOfType<ProfileInsertResult.Success>();

            var result = await _repository.QueryProfiles(
                new ProfileQuery
                {
                    OrderBy = "name",
                    Direction = "ASC",
                    Limit = 1,
                    Offset = 1,
                }
            );

            var names = result
                .OfType<ProfileGetResult.Success>()
                .Select(r => r.Profile.Name)
                .Where(name => name.StartsWith("00"))
                .ToList();

            names.Should().ContainSingle().Which.Should().Be("002-DMS1074-Valid");
        }
    }

    [TestFixture]
    public class QuerySortTests : ProfileTests
    {
        [SetUp]
        public async Task Setup()
        {
            await ResetProfiles(LateSortedProfileNames);
            foreach (var name in LateSortedProfileNames)
            {
                var result = await _repository.InsertProfile(
                    new ProfileInsertCommand { Name = name, Definition = CreateDefinition(name) }
                );
                result.Should().BeOfType<ProfileInsertResult.Success>();
            }
        }

        [Test]
        public async Task Should_return_ascending_order_by_name()
        {
            var result = await _repository.QueryProfiles(
                new ProfileQuery { OrderBy = "name", Direction = "ASC" }
            );
            var names = result
                .OfType<ProfileGetResult.Success>()
                .Select(r => r.Profile.Name)
                .Where(name => name.StartsWith("~~~DMS1074-"))
                .ToList();
            names.Should().ContainInOrder(LateSortedProfileNames.OrderBy(name => name).ToArray());
        }

        [Test]
        public async Task Should_return_descending_order_by_name()
        {
            var result = await _repository.QueryProfiles(
                new ProfileQuery { OrderBy = "name", Direction = "DESC" }
            );
            var names = result
                .OfType<ProfileGetResult.Success>()
                .Select(r => r.Profile.Name)
                .Where(name => name.StartsWith("~~~DMS1074-"))
                .ToList();
            names.Should().ContainInOrder(LateSortedProfileNames.OrderByDescending(name => name).ToArray());
        }
    }

    [TestFixture]
    public class QueryFilterTests : ProfileTests
    {
        private long _profileId;

        [SetUp]
        public async Task Setup()
        {
            await ResetProfiles("DMS1074-FilteredProfile", "DMS1074-OtherProfile");
            var profileResult = await _repository.InsertProfile(
                new ProfileInsertCommand
                {
                    Name = "DMS1074-FilteredProfile",
                    Definition = CreateDefinition("DMS1074-FilteredProfile"),
                }
            );
            _profileId = ((ProfileInsertResult.Success)profileResult).Id;

            var otherResult = await _repository.InsertProfile(
                new ProfileInsertCommand
                {
                    Name = "DMS1074-OtherProfile",
                    Definition = CreateDefinition("DMS1074-OtherProfile"),
                }
            );
            otherResult.Should().BeOfType<ProfileInsertResult.Success>();
        }

        [Test]
        public async Task Should_filter_by_id()
        {
            var result = await _repository.QueryProfiles(new ProfileQuery { Id = _profileId });
            var profiles = result.OfType<ProfileGetResult.Success>().ToList();
            profiles.Should().ContainSingle();
            profiles[0].Profile.Id.Should().Be(_profileId);
        }

        [Test]
        public async Task Should_filter_by_name()
        {
            var result = await _repository.QueryProfiles(
                new ProfileQuery { Name = "DMS1074-FilteredProfile" }
            );
            var profiles = result.OfType<ProfileGetResult.Success>().ToList();
            profiles.Should().ContainSingle();
            profiles[0].Profile.Name.Should().Be("DMS1074-FilteredProfile");
        }

        [Test]
        public async Task Should_filter_by_id_and_name()
        {
            var result = await _repository.QueryProfiles(
                new ProfileQuery { Id = _profileId, Name = "DMS1074-FilteredProfile" }
            );
            var profiles = result.OfType<ProfileGetResult.Success>().ToList();
            profiles.Should().ContainSingle();
            profiles[0].Profile.Id.Should().Be(_profileId);
            profiles[0].Profile.Name.Should().Be("DMS1074-FilteredProfile");
        }
    }
}
