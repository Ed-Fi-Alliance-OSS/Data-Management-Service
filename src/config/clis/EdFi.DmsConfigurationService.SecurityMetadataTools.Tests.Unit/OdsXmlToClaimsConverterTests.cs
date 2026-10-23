// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

[TestFixture]
public class Given_an_export_with_nested_claims_and_repeated_claim_sets
{
    private JsonObject _result = null!;

    [SetUp]
    public void Setup()
    {
        _result = OdsXmlToClaimsConverter.Convert(
            XDocument.Parse(
                """
                <SecurityMetadata>
                  <Claims>
                    <Claim name="http://ed-fi.org/identity/claims/domains">
                      <DefaultAuthorization>
                        <Action name="Read">
                          <AuthorizationStrategies>
                            <AuthorizationStrategy name="NamespaceBased" />
                            <AuthorizationStrategy name="RelationshipsWithEdOrgsOnly" />
                          </AuthorizationStrategies>
                        </Action>
                        <Action name="Create">
                          <AuthorizationStrategies><AuthorizationStrategy name="NoFurtherAuthorizationRequired" /></AuthorizationStrategies>
                        </Action>
                      </DefaultAuthorization>
                      <ClaimSets>
                        <ClaimSet name="EdFiSandbox">
                          <Actions>
                            <Action name="Read">
                              <AuthorizationStrategyOverrides>
                                <AuthorizationStrategy name="NamespaceBased" />
                                <AuthorizationStrategy name="Extension.CustomStrategy" />
                              </AuthorizationStrategyOverrides>
                            </Action>
                            <Action name="Create" />
                          </Actions>
                        </ClaimSet>
                      </ClaimSets>
                      <Claims>
                        <Claim claimId="17" name="http://ed-fi.org/identity/claims/domains/students">
                          <DefaultAuthorization />
                          <ClaimSets>
                            <ClaimSet name="EdFiSandbox"><Actions><Action name="Update" /></Actions></ClaimSet>
                            <ClaimSet name="DistrictReader"><Actions><Action name="Read"><AuthorizationStrategyOverrides /></Action></Actions></ClaimSet>
                          </ClaimSets>
                          <Claims>
                            <Claim claimId="18" name="http://example.org/claims/studentExtension">
                              <DefaultAuthorization><Action name="Delete"><AuthorizationStrategies /></Action></DefaultAuthorization>
                              <ClaimSets />
                              <Claims />
                            </Claim>
                          </Claims>
                        </Claim>
                      </Claims>
                    </Claim>
                    <Claim name="http://ed-fi.org/identity/claims/descriptors">
                      <ClaimSets>
                        <ClaimSet name="DistrictReader"><Actions><Action name="Read" /></Actions></ClaimSet>
                        <ClaimSet name="RosterWriter"><Actions><Action name="Create" /></Actions></ClaimSet>
                      </ClaimSets>
                    </Claim>
                  </Claims>
                </SecurityMetadata>
                """
            )
        );
    }

    [Test]
    public void It_emits_the_exact_complete_document_without_resolving_inheritance()
    {
        JsonNode expected = JsonNode.Parse(
            """
            {
              "claimSets": [
                { "claimSetName": "EdFiSandbox", "isSystemReserved": false },
                { "claimSetName": "DistrictReader", "isSystemReserved": false },
                { "claimSetName": "RosterWriter", "isSystemReserved": false }
              ],
              "claimsHierarchy": [
                {
                  "name": "http://ed-fi.org/identity/claims/domains",
                  "defaultAuthorization": {
                    "actions": [
                      { "name": "Read", "authorizationStrategies": [
                        { "name": "NamespaceBased" }, { "name": "RelationshipsWithEdOrgsOnly" }
                      ] },
                      { "name": "Create", "authorizationStrategies": [ { "name": "NoFurtherAuthorizationRequired" } ] }
                    ]
                  },
                  "claimSets": [
                    { "name": "EdFiSandbox", "actions": [
                      { "name": "Read", "authorizationStrategyOverrides": [
                        { "name": "NamespaceBased" }, { "name": "Extension.CustomStrategy" }
                      ] },
                      { "name": "Create" }
                    ] }
                  ],
                  "claims": [
                    {
                      "name": "http://ed-fi.org/identity/claims/domains/students",
                      "claimSets": [
                        { "name": "EdFiSandbox", "actions": [ { "name": "Update" } ] },
                        { "name": "DistrictReader", "actions": [ { "name": "Read", "authorizationStrategyOverrides": [] } ] }
                      ],
                      "claims": [
                        {
                          "name": "http://example.org/claims/studentExtension",
                          "defaultAuthorization": { "actions": [ { "name": "Delete", "authorizationStrategies": [] } ] },
                          "claimSets": [],
                          "claims": []
                        }
                      ]
                    }
                  ]
                },
                {
                  "name": "http://ed-fi.org/identity/claims/descriptors",
                  "claimSets": [
                    { "name": "DistrictReader", "actions": [ { "name": "Read" } ] },
                    { "name": "RosterWriter", "actions": [ { "name": "Create" } ] }
                  ]
                }
              ]
            }
            """
        )!;

        JsonNode.DeepEquals(_result, expected).Should().BeTrue("the complete JSON must match: {0}", _result);
    }
}

[TestFixture("")]
[TestFixture("<DefaultAuthorization />")]
public class Given_a_child_without_local_defaults(string childDefaults)
{
    private JsonObject _result = null!;

    [SetUp]
    public void Setup()
    {
        _result = OdsXmlToClaimsConverter.Convert(
            XDocument.Parse(
                $$"""
                <SecurityMetadata><Claims>
                  <Claim name="container">
                    <Claims>
                      <Claim name="parent">
                        <DefaultAuthorization><Action name="Read"><AuthorizationStrategies><AuthorizationStrategy name="NamespaceBased" /></AuthorizationStrategies></Action></DefaultAuthorization>
                        <ClaimSets><ClaimSet name="InheritedReader"><Actions><Action name="Read" /></Actions></ClaimSet></ClaimSets>
                        <Claims><Claim name="child">{{childDefaults}}</Claim></Claims>
                      </Claim>
                    </Claims>
                  </Claim>
                </Claims></SecurityMetadata>
                """
            )
        );
    }

    [Test]
    public void It_preserves_the_parent_policy_and_leaves_the_child_to_inherit()
    {
        JsonNode expected = JsonNode.Parse(
            """
            {
              "claimSets": [ { "claimSetName": "InheritedReader", "isSystemReserved": false } ],
              "claimsHierarchy": [ {
                "name": "container",
                "claims": [ {
                  "name": "parent",
                  "defaultAuthorization": { "actions": [ {
                    "name": "Read", "authorizationStrategies": [ { "name": "NamespaceBased" } ]
                  } ] },
                  "claimSets": [ { "name": "InheritedReader", "actions": [ { "name": "Read" } ] } ],
                  "claims": [ { "name": "child" } ]
                } ]
              } ]
            }
            """
        )!;

        JsonNode.DeepEquals(_result, expected).Should().BeTrue("the complete JSON must match: {0}", _result);
    }
}

[TestFixture("")]
[TestFixture("<AuthorizationStrategies />")]
public class Given_a_default_action_with_no_strategies(string strategies)
{
    private JsonObject _result = null!;

    [SetUp]
    public void Setup()
    {
        _result = OdsXmlToClaimsConverter.Convert(
            XDocument.Parse(
                $$"""
                <SecurityMetadata><Claims><Claim name="resource">
                  <DefaultAuthorization><Action name="Read">{{strategies}}</Action></DefaultAuthorization>
                  <ClaimSets><ClaimSet name="Reader"><Actions><Action name="Read"><AuthorizationStrategyOverrides /></Action></Actions></ClaimSet></ClaimSets>
                </Claim></Claims></SecurityMetadata>
                """
            )
        );
    }

    [Test]
    public void It_retains_the_action_with_an_empty_strategy_list()
    {
        JsonNode expected = JsonNode.Parse("""{"actions":[{"name":"Read","authorizationStrategies":[]}]}""")!;
        JsonNode
            .DeepEquals(_result["claimsHierarchy"]![0]!["defaultAuthorization"], expected)
            .Should()
            .BeTrue();
    }

    [Test]
    public void It_retains_the_explicitly_empty_override_list()
    {
        JsonNode expected = JsonNode.Parse("""[{"name":"Read","authorizationStrategyOverrides":[]}]""")!;
        JsonNode
            .DeepEquals(_result["claimsHierarchy"]![0]!["claimSets"]![0]!["actions"], expected)
            .Should()
            .BeTrue();
    }
}

[TestFixture]
public class Given_successive_exports_with_different_claim_sets
{
    private JsonObject _result = null!;

    [SetUp]
    public void Setup()
    {
        OdsXmlToClaimsConverter.Convert(
            XDocument.Parse(
                """
                <SecurityMetadata><Claims><Claim name="first">
                  <ClaimSets><ClaimSet name="FirstReader"><Actions><Action name="Read" /></Actions></ClaimSet></ClaimSets>
                </Claim></Claims></SecurityMetadata>
                """
            )
        );
        _result = OdsXmlToClaimsConverter.Convert(
            XDocument.Parse(
                """
                <SecurityMetadata><Claims><Claim name="second">
                  <ClaimSets><ClaimSet name="SecondReader"><Actions><Action name="Read" /></Actions></ClaimSet></ClaimSets>
                </Claim></Claims></SecurityMetadata>
                """
            )
        );
    }

    [Test]
    public void It_catalogs_only_the_current_document_references()
    {
        JsonNode expected = JsonNode.Parse("""[{"claimSetName":"SecondReader","isSystemReserved":false}]""")!;
        JsonNode.DeepEquals(_result["claimSets"], expected).Should().BeTrue();
    }
}
