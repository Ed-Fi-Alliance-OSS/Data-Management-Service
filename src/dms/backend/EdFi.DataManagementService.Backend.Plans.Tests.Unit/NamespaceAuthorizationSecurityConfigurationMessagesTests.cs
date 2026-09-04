// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// The per-command parameter cap message. The four-argument form exists so GET-many can name the ownership
/// token list; it must collapse to the three-argument text whenever that list is empty, because the change
/// query, descriptor, and existing page paths report through the three-argument form and their expectations
/// pin its wording.
/// </summary>
[TestFixture]
public class Given_NamespaceAuthorizationSecurityConfigurationMessages_CommandParameterCapExceeded
{
    [Test]
    public void It_produces_the_three_argument_text_when_the_ownership_token_count_is_zero()
    {
        var withOwnershipArgument =
            NamespaceAuthorizationSecurityConfigurationMessages.CommandParameterCapExceeded(
                namespacePrefixCount: 1999,
                claimEducationOrganizationIdCount: 100,
                ownershipTokenCount: 0,
                nonAuthorizationParameterCount: 2
            );

        withOwnershipArgument
            .Should()
            .Be(
                NamespaceAuthorizationSecurityConfigurationMessages.CommandParameterCapExceeded(
                    namespacePrefixCount: 1999,
                    claimEducationOrganizationIdCount: 100,
                    nonAuthorizationParameterCount: 2
                )
            );
        withOwnershipArgument.Should().NotContain("ownership");
    }

    [Test]
    public void It_names_every_list_and_the_query_parameter_count_when_ownership_tokens_are_present()
    {
        var message = NamespaceAuthorizationSecurityConfigurationMessages.CommandParameterCapExceeded(
            namespacePrefixCount: 100,
            claimEducationOrganizationIdCount: 0,
            ownershipTokenCount: 1999,
            nonAuthorizationParameterCount: 2
        );

        message
            .Should()
            .Be(
                "The API client has 100 namespace prefixes, 0 authorization education organization ids, and 1999 ownership tokens, which together with 2 query and paging parameters exceed the SQL Server parameter limit for a single query. Configure fewer namespace prefixes, reduce the client's authorized education organizations or ownership tokens, or use fewer query parameters."
            );
    }

    [Test]
    public void It_keeps_the_three_argument_text_unchanged()
    {
        NamespaceAuthorizationSecurityConfigurationMessages
            .CommandParameterCapExceeded(
                namespacePrefixCount: 1500,
                claimEducationOrganizationIdCount: 1500,
                nonAuthorizationParameterCount: 2
            )
            .Should()
            .Be(
                "The API client has 1500 namespace prefixes and 1500 authorization education organization ids, which together with 2 query and paging parameters exceed the SQL Server parameter limit for a single query. Configure fewer namespace prefixes, reduce the client's authorized education organizations, or use fewer query parameters."
            );
    }
}
