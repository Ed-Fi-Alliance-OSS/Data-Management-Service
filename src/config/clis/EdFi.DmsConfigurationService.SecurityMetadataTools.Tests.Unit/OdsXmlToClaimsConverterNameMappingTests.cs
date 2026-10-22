// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

internal static class NameMappingExport
{
    public static XElement Claim(string name, string claimSetName) =>
        new(
            "Claim",
            new XAttribute("name", name),
            new XElement(
                "ClaimSets",
                new XElement(
                    "ClaimSet",
                    new XAttribute("name", claimSetName),
                    new XElement("Actions", new XElement("Action", new XAttribute("name", "Read")))
                )
            )
        );

    public static XDocument Document(params XElement[] claims) =>
        new(new XElement("SecurityMetadata", new XElement("Claims", claims)));
}

[TestFixture(" District-Reader ", "DistrictReader")]
[TestFixture("École- Élèves_東京–Reader\u00a0X", "ÉcoleÉlèves_東京–Reader\u00a0X")]
[TestFixture("MiXeD.Case_Name", "MiXeD.Case_Name")]
[TestFixture("e\u0301-Reader", "e\u0301Reader")]
[TestFixture("School-😀 Reader", "School😀Reader")]
public class Given_repeated_claim_set_names_requiring_normalization(string sourceName, string expectedName)
{
    private JsonObject _result = null!;

    [SetUp]
    public void Setup()
    {
        XElement parent = NameMappingExport.Claim("parent", sourceName);
        parent.Add(new XElement("Claims", NameMappingExport.Claim("child", sourceName)));
        _result = OdsXmlToClaimsConverter.Convert(
            NameMappingExport.Document(parent, NameMappingExport.Claim("sibling", sourceName))
        );
    }

    [Test]
    public void It_uses_one_editable_catalog_entry_and_the_same_exact_name_at_every_reference()
    {
        // Serialize only the independent expected string to quote Unicode/JSON safely.
        string name = JsonSerializer.Serialize(expectedName);
        JsonNode expected = JsonNode.Parse(
            $$"""
            {
              "claimSets": [{"claimSetName": {{name}}, "isSystemReserved": false}],
              "claimsHierarchy": [
                {"name":"parent", "claimSets":[{"name":{{name}}, "actions":[{"name":"Read"}]}],
                  "claims":[{"name":"child", "claimSets":[{"name":{{name}}, "actions":[{"name":"Read"}]}]}]},
                {"name":"sibling", "claimSets":[{"name":{{name}}, "actions":[{"name":"Read"}]}]}
              ]
            }
            """
        )!;
        JsonNode.DeepEquals(_result, expected).Should().BeTrue("the complete JSON must match: {0}", _result);
    }
}

[TestFixture]
public class Given_a_claim_set_name_with_256_characters_after_normalization
{
    private JsonObject _result = null!;
    private readonly string _expectedName = new('É', 256);

    [SetUp]
    public void Setup()
    {
        _result = OdsXmlToClaimsConverter.Convert(
            NameMappingExport.Document(NameMappingExport.Claim("resource", "- " + _expectedName + " -"))
        );
    }

    [Test]
    public void It_accepts_the_boundary_after_removing_spaces_and_hyphens()
    {
        _result["claimSets"]![0]!["claimSetName"]!.GetValue<string>().Should().Be(_expectedName);
        _result["claimsHierarchy"]![0]!["claimSets"]![0]!["name"]!
            .GetValue<string>()
            .Should()
            .Be(_expectedName);
    }
}

[TestFixture("")]
[TestFixture(" -- ")]
[TestFixture("Reader\tName")]
[TestFixture("Reader\rName")]
[TestFixture("Reader\nName")]
[TestFixture("Reader\u007fName")]
[TestFixture("Reader\u0085Name")]
public class Given_an_invalid_normalized_claim_set_name(string sourceName)
{
    private Action _convert = null!;

    [SetUp]
    public void Setup()
    {
        // Construct attributes directly so XML whitespace normalization cannot hide controls.
        XDocument document = NameMappingExport.Document(NameMappingExport.Claim("resource", sourceName));
        _convert = () => OdsXmlToClaimsConverter.Convert(document);
    }

    [Test]
    public void It_rejects_the_name_with_an_escaped_source_and_the_name_rules()
    {
        string message = _convert.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain(JsonSerializer.Serialize(sourceName));
        message.Should().Contain("1 to 256").And.Contain("control characters");
    }
}

[TestFixture]
public class Given_a_normalized_claim_set_name_longer_than_256_characters
{
    private Action _convert = null!;

    [SetUp]
    public void Setup()
    {
        XDocument document = NameMappingExport.Document(
            NameMappingExport.Claim("resource", new string('a', 257))
        );
        _convert = () => OdsXmlToClaimsConverter.Convert(document);
    }

    [Test]
    public void It_rejects_the_name_with_the_length_limit()
    {
        _convert.Should().Throw<InvalidOperationException>().WithMessage("*1 to 256*");
    }
}

[TestFixture("District Reader", "District-Reader")]
[TestFixture("Reader", "reader")]
[TestFixture("District-Reader", "district reader")]
[TestFixture("École Reader", "écoleReader")]
public class Given_distinct_claim_set_names_that_collide(string firstName, string secondName)
{
    private Action _convert = null!;

    [SetUp]
    public void Setup()
    {
        XElement firstRoot = new(
            "Claim",
            new XAttribute("name", "container"),
            new XElement("Claims", NameMappingExport.Claim("child", firstName))
        );
        XDocument document = NameMappingExport.Document(
            firstRoot,
            NameMappingExport.Claim("secondRoot", secondName)
        );
        _convert = () => OdsXmlToClaimsConverter.Convert(document);
    }

    [Test]
    public void It_rejects_the_document_and_identifies_both_source_names()
    {
        string message = _convert.Should().Throw<InvalidOperationException>().Which.Message;
        message
            .Should()
            .Contain(JsonSerializer.Serialize(firstName))
            .And.Contain(JsonSerializer.Serialize(secondName));
        message.Should().Contain("collide").And.Contain("case-insensitive");
    }
}

[TestFixture(
    "http://ed-fi.org/ods/identity/claims/academicWeek",
    "http://ed-fi.org/identity/claims/ed-fi/academicWeek"
)]
[TestFixture(
    "http://ed-fi.org/ods/identity/claims/domains/education",
    "http://ed-fi.org/identity/claims/domains/education"
)]
[TestFixture(
    "http://ed-fi.org/ods/identity/claims/services/identity",
    "http://ed-fi.org/identity/claims/services/identity"
)]
[TestFixture(
    "http://ed-fi.org/identity/claims/academicWeek",
    "http://ed-fi.org/identity/claims/academicWeek"
)]
[TestFixture(
    "http://ed-fi.org/ods/identity/claims/ed-fi/students",
    "http://ed-fi.org/identity/claims/ed-fi/students"
)]
[TestFixture("http://ed-fi.org/ods/identity/claims/", "http://ed-fi.org/identity/claims/")]
[TestFixture(
    "http://ed-fi.org/ods/identity/claims/Ext/ods/École",
    "http://ed-fi.org/identity/claims/Ext/ods/École"
)]
[TestFixture(
    "http://ed-fi.org/identity/claims/ed-fi/students",
    "http://ed-fi.org/identity/claims/ed-fi/students"
)]
[TestFixture(
    "https://ed-fi.org/ods/identity/claims/students",
    "https://ed-fi.org/ods/identity/claims/students"
)]
[TestFixture(
    "http://ED-FI.org/ods/identity/claims/students",
    "http://ED-FI.org/ods/identity/claims/students"
)]
[TestFixture(
    "http://ed-fi.org/ods/identity/Claims/students",
    "http://ed-fi.org/ods/identity/Claims/students"
)]
[TestFixture("http://ed-fi.org/ods/identity/claims", "http://ed-fi.org/ods/identity/claims")]
[TestFixture(
    "http://ed-fi.org/ods/identity/claimsExtra/students",
    "http://ed-fi.org/ods/identity/claimsExtra/students"
)]
[TestFixture(
    "http://extension.org/ods/identity/claims/students",
    "http://extension.org/ods/identity/claims/students"
)]
[TestFixture(
    "http://ed-fi.org/identity/claims/ext/ods/students",
    "http://ed-fi.org/identity/claims/ext/ods/students"
)]
[TestFixture(
    "http://extension.org/http://ed-fi.org/ods/identity/claims/students",
    "http://extension.org/http://ed-fi.org/ods/identity/claims/students"
)]
public class Given_a_claim_uri_to_map(string sourceName, string expectedName)
{
    private JsonObject _result = null!;

    [SetUp]
    public void Setup()
    {
        _result = OdsXmlToClaimsConverter.Convert(
            NameMappingExport.Document(
                new XElement(
                    "Claim",
                    new XAttribute("name", "container"),
                    new XElement("Claims", NameMappingExport.Claim(sourceName, "Reader"))
                )
            )
        );
    }

    [Test]
    public void It_migrates_only_legacy_uris_and_qualifies_only_single_segment_resources()
    {
        _result["claimsHierarchy"]![0]!["claims"]![0]!["name"]!.GetValue<string>().Should().Be(expectedName);
    }
}

[TestFixture(
    "http://ed-fi.org/ods/identity/claims/academicWeek",
    "http://ed-fi.org/identity/claims/ed-fi/academicWeek"
)]
[TestFixture(
    "http://ed-fi.org/identity/claims/ed-fi/academicWeek",
    "http://ed-fi.org/ods/identity/claims/academicWeek"
)]
[TestFixture(
    "http://ed-fi.org/ods/identity/claims/academicWeek",
    "http://ed-fi.org/ods/identity/claims/ed-fi/academicWeek"
)]
[TestFixture(
    "http://ed-fi.org/ods/identity/claims/ed-fi/academicWeek",
    "http://ed-fi.org/ods/identity/claims/academicWeek"
)]
[TestFixture(
    "http://ed-fi.org/ods/identity/claims/domains/education",
    "http://ed-fi.org/identity/claims/domains/education"
)]
[TestFixture(
    "http://ed-fi.org/identity/claims/domains/education",
    "http://ed-fi.org/ods/identity/claims/domains/education"
)]
public class Given_claim_uris_that_collide_across_hierarchy_locations(string firstName, string secondName)
{
    private Action _convert = null!;

    [SetUp]
    public void Setup()
    {
        XDocument document = NameMappingExport.Document(
            new XElement(
                "Claim",
                new XAttribute("name", "container"),
                new XElement("Claims", NameMappingExport.Claim(firstName, "Reader"))
            ),
            NameMappingExport.Claim(secondName, "Reader")
        );
        _convert = () => OdsXmlToClaimsConverter.Convert(document);
    }

    [Test]
    public void It_rejects_migration_in_either_order_with_both_source_uris()
    {
        string message = _convert.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain(firstName).And.Contain(secondName).And.Contain("collide");
    }
}

[TestFixture]
public class Given_successive_conversions_with_independently_colliding_names
{
    private JsonObject _result = null!;

    [SetUp]
    public void Setup()
    {
        OdsXmlToClaimsConverter.Convert(
            NameMappingExport.Document(
                NameMappingExport.Claim("http://ed-fi.org/ods/identity/claims/students", "District-Reader")
            )
        );
        _result = OdsXmlToClaimsConverter.Convert(
            NameMappingExport.Document(
                NameMappingExport.Claim("http://ed-fi.org/identity/claims/ed-fi/students", "District Reader")
            )
        );
    }

    [Test]
    public void It_scopes_both_collision_indexes_to_one_document()
    {
        JsonNode expected = JsonNode.Parse(
            """
            {"claimSets":[{"claimSetName":"DistrictReader","isSystemReserved":false}],
             "claimsHierarchy":[{"name":"http://ed-fi.org/identity/claims/ed-fi/students",
               "claimSets":[{"name":"DistrictReader","actions":[{"name":"Read"}]}]}]}
            """
        )!;
        JsonNode.DeepEquals(_result, expected).Should().BeTrue();
    }
}

[TestFixture]
public class Given_the_representative_ods_security_metadata_export
{
    private JsonObject _result = null!;
    private JsonNode _expected = null!;

    [SetUp]
    public void Setup()
    {
        string fixtureDirectory = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "OdsSecurityMetadata"
        );
        // This oracle was prepared independently from database rows, not converter output.
        _expected = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtureDirectory, "expected-claims.json")))!;
        _result = OdsXmlToClaimsConverter.Convert(
            XDocument.Load(Path.Combine(fixtureDirectory, "representative.xml"))
        );
    }

    [Test]
    public void It_matches_the_complete_reviewed_claims_document()
    {
        JsonNode
            .DeepEquals(_result, _expected)
            .Should()
            .BeTrue("all catalog, hierarchy, policy, and ordering values must match the independent fixture");
    }
}

[TestFixture]
public class Given_a_legacy_core_resource_used_by_dms
{
    private string _convertedName = null!;
    private string[] _standardNames = null!;

    [SetUp]
    public void Setup()
    {
        JsonObject converted = OdsXmlToClaimsConverter.Convert(
            NameMappingExport.Document(
                NameMappingExport.Claim("http://ed-fi.org/ods/identity/claims/academicWeek", "Reader")
            )
        );
        _convertedName = converted["claimsHierarchy"]![0]!["name"]!.GetValue<string>();
        JsonNode standard = JsonNode.Parse(
            File.ReadAllText(
                Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "ds52-claims.json")
            )
        )!;
        _standardNames = ClaimNames(standard["claimsHierarchy"]!.AsArray()).ToArray();
    }

    private static IEnumerable<string> ClaimNames(JsonArray claims)
    {
        foreach (JsonNode claim in claims.OfType<JsonNode>())
        {
            yield return claim["name"]!.GetValue<string>();
            if (claim["claims"] is JsonArray children)
            {
                foreach (string name in ClaimNames(children))
                {
                    yield return name;
                }
            }
        }
    }

    [Test]
    public void It_matches_the_project_qualified_resource_uri_in_the_existing_ds52_policy()
    {
        // The DMS resource naming extension includes the project between base and resource.
        // Claim-set authorization then matches the complete URI, as used in this standard policy.
        _convertedName.Should().Be("http://ed-fi.org/identity/claims/ed-fi/academicWeek");
        _standardNames.Should().ContainSingle(name => name == _convertedName);
    }
}
