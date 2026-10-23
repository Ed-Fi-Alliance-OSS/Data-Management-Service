// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using System.Xml.Linq;
using System.Xml.XPath;
using EdFi.DmsConfigurationService.Backend.Claims;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

internal static class ValidationExport
{
    public static string Read(string name) =>
        File.ReadAllText(
            Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Validation", name)
        );

    public static XDocument Load() => XDocument.Parse(Read("valid-policy.xml"), LoadOptions.SetLineInfo);

    public static XElement Element(XDocument document, string path) =>
        document.XPathSelectElement("/SecurityMetadata/Claims/Claim" + path)!;
}

[TestFixture("/DefaultAuthorization/Action", "validationRuleSet")]
[TestFixture("/ClaimSets/ClaimSet/Actions/Action", "validationRuleSetOverride")]
public class Given_validation_rule_attributes(string path, string attribute)
{
    private XDocument _document = null!;

    [SetUp]
    public void Setup() => _document = ValidationExport.Load();

    [TestCase("Rules.For.Students")]
    [TestCase(" ")]
    public void It_rejects_any_populated_value_with_the_attribute_claim_action_and_location(string value)
    {
        ValidationExport.Element(_document, path).SetAttributeValue(attribute, value);
        Action convert = () => OdsXmlToClaimsConverter.Convert(_document);
        string message = convert.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain(attribute).And.Contain("unsupported");
        message.Should().Contain("Claim[@name=\"parent\"]").And.Contain("Action[@name=\"Read\"]");
        message.Should().Contain("line ").And.Contain("column ");
    }

    [Test]
    public void It_accepts_empty_and_absent_values_without_altering_policy()
    {
        JsonObject absent = OdsXmlToClaimsConverter.Convert(_document);
        ValidationExport.Element(_document, path).SetAttributeValue(attribute, "");
        JsonObject empty = OdsXmlToClaimsConverter.Convert(_document);
        JsonNode.DeepEquals(empty, absent).Should().BeTrue();
        new ClaimsValidator(NullLogger<ClaimsValidator>.Instance).Validate(empty).Should().BeEmpty();
    }
}

[TestFixture("", "element")]
[TestFixture("/Claims", "element")]
[TestFixture("/Claims/Claim", "element")]
[TestFixture("/DefaultAuthorization", "element")]
[TestFixture("/DefaultAuthorization/Action", "element")]
[TestFixture("/DefaultAuthorization/Action/AuthorizationStrategies", "element")]
[TestFixture("/DefaultAuthorization/Action/AuthorizationStrategies/AuthorizationStrategy", "element")]
[TestFixture("/ClaimSets", "element")]
[TestFixture("/ClaimSets/ClaimSet", "element")]
[TestFixture("/ClaimSets/ClaimSet/Actions", "element")]
[TestFixture("/ClaimSets/ClaimSet/Actions/Action", "element")]
[TestFixture("/ClaimSets/ClaimSet/Actions/Action/AuthorizationStrategyOverrides", "element")]
[TestFixture(
    "/ClaimSets/ClaimSet/Actions/Action/AuthorizationStrategyOverrides/AuthorizationStrategy",
    "element"
)]
[TestFixture("", "attribute")]
[TestFixture("/DefaultAuthorization/Action", "attribute")]
[TestFixture("/ClaimSets/ClaimSet/Actions/Action", "attribute")]
[TestFixture("/DefaultAuthorization/Action/AuthorizationStrategies/AuthorizationStrategy", "attribute")]
[TestFixture(
    "/ClaimSets/ClaimSet/Actions/Action/AuthorizationStrategyOverrides/AuthorizationStrategy",
    "attribute"
)]
public class Given_unknown_policy_content(string path, string kind)
{
    private Action _convert = null!;

    [SetUp]
    public void Setup()
    {
        XDocument document = ValidationExport.Load();
        XElement element = ValidationExport.Element(document, path);
        if (kind == "element")
        {
            element.Add(new XElement("UnknownPolicy"));
        }
        else
        {
            element.SetAttributeValue("UnknownPolicy", "Allow");
        }
        _convert = () => OdsXmlToClaimsConverter.Convert(document);
    }

    [Test]
    public void It_rejects_instead_of_silently_dropping_content()
    {
        _convert
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage($"*Unsupported {kind}*UnknownPolicy*parent*");
    }
}

[TestFixture("/SecurityMetadata", "Claims")]
[TestFixture("/SecurityMetadata/Claims/Claim", "DefaultAuthorization")]
[TestFixture("/SecurityMetadata/Claims/Claim", "ClaimSets")]
[TestFixture("/SecurityMetadata/Claims/Claim", "Claims")]
[TestFixture("/SecurityMetadata/Claims/Claim/ClaimSets/ClaimSet", "Actions")]
[TestFixture("/SecurityMetadata/Claims/Claim/DefaultAuthorization/Action", "AuthorizationStrategies")]
[TestFixture(
    "/SecurityMetadata/Claims/Claim/ClaimSets/ClaimSet/Actions/Action",
    "AuthorizationStrategyOverrides"
)]
public class Given_duplicate_policy_containers(string path, string child)
{
    private Action _convert = null!;

    [SetUp]
    public void Setup()
    {
        XDocument document = ValidationExport.Load();
        document.XPathSelectElement(path)!.Add(new XElement(child));
        _convert = () => OdsXmlToClaimsConverter.Convert(document);
    }

    [Test]
    public void It_rejects_instead_of_using_only_the_first_container() =>
        _convert
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage($"*at most one {child}*{path.Split('/')[^1]}*");
}

[TestFixture("")]
[TestFixture("/ClaimSets/ClaimSet")]
[TestFixture("/DefaultAuthorization/Action")]
[TestFixture("/ClaimSets/ClaimSet/Actions/Action")]
[TestFixture("/DefaultAuthorization/Action/AuthorizationStrategies/AuthorizationStrategy")]
[TestFixture("/ClaimSets/ClaimSet/Actions/Action/AuthorizationStrategyOverrides/AuthorizationStrategy")]
public class Given_a_missing_policy_name(string path)
{
    private Action _convert = null!;

    [SetUp]
    public void Setup()
    {
        XDocument document = ValidationExport.Load();
        ValidationExport.Element(document, path).Attribute("name")!.Remove();
        _convert = () => OdsXmlToClaimsConverter.Convert(document);
    }

    [Test]
    public void It_identifies_the_missing_name_and_xml_path() =>
        _convert
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*name attribute*At /SecurityMetadata/Claims/Claim*");
}

[TestFixture("")]
[TestFixture("/DefaultAuthorization/Action")]
[TestFixture("/ClaimSets/ClaimSet/Actions/Action")]
[TestFixture("/DefaultAuthorization/Action/AuthorizationStrategies/AuthorizationStrategy")]
[TestFixture("/ClaimSets/ClaimSet/Actions/Action/AuthorizationStrategyOverrides/AuthorizationStrategy")]
public class Given_an_empty_policy_name(string path)
{
    private XDocument _document = null!;

    [SetUp]
    public void Setup() => _document = ValidationExport.Load();

    [TestCase("")]
    [TestCase(" ")]
    public void It_rejects_empty_or_whitespace_names(string value)
    {
        ValidationExport.Element(_document, path).SetAttributeValue("name", value);
        Action convert = () => OdsXmlToClaimsConverter.Convert(_document);
        convert
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*nonempty name attribute*At /SecurityMetadata/Claims/Claim*");
    }
}

[TestFixture("<SecurityMetadata><Claims /></SecurityMetadata>", "no claims")]
[TestFixture(
    "<SecurityMetadata><Claims><Claim name='container' /></Claims></SecurityMetadata>",
    "no referenced claim sets"
)]
[TestFixture(
    "<SecurityMetadata><Claims><Claim name='resource'><ClaimSets><ClaimSet name='Reader'><Actions /></ClaimSet></ClaimSets></Claim></Claims></SecurityMetadata>",
    "no claim-set actions"
)]
[TestFixture("<WrongRoot><Claims /></WrongRoot>", "Expected a SecurityMetadata root")]
[TestFixture("<SecurityMetadata />", "Missing required Claims element")]
[TestFixture(
    "<SecurityMetadata><Claims><Claim name='resource'><ClaimSets><ClaimSet name='Reader' /></ClaimSets></Claim></Claims></SecurityMetadata>",
    "Missing required Actions element"
)]
[TestFixture("<SecurityMetadata><Unexpected /></SecurityMetadata>", "Unsupported element")]
[TestFixture("<SecurityMetadata enabled='false'><Claims /></SecurityMetadata>", "Unsupported attribute")]
[TestFixture("<SecurityMetadata><Claims enabled='false' /></SecurityMetadata>", "Unsupported attribute")]
[TestFixture(
    "<SecurityMetadata xmlns='urn:other'><Claims /></SecurityMetadata>",
    "Expected a SecurityMetadata root"
)]
[TestFixture(
    "<SecurityMetadata><Claims><Claim xmlns='urn:other' name='resource' /></Claims></SecurityMetadata>",
    "Unsupported element"
)]
[TestFixture("<SecurityMetadata><Claims>Allow all</Claims></SecurityMetadata>", "Unexpected text")]
[TestFixture(
    "<SecurityMetadata><Claims><?policy allow?></Claims></SecurityMetadata>",
    "processing instruction"
)]
public class Given_an_incomplete_or_unsupported_export(string xml, string diagnostic)
{
    private Action _convert = null!;

    [SetUp]
    public void Setup() => _convert = () => OdsXmlToClaimsConverter.Convert(xml);

    [Test]
    public void It_fails_with_an_actionable_diagnostic() =>
        _convert.Should().Throw<InvalidOperationException>().WithMessage($"*{diagnostic}*");
}

[TestFixture]
public class Given_malformed_security_metadata_xml
{
    private Action _convert = null!;

    [SetUp]
    public void Setup() =>
        _convert = () => OdsXmlToClaimsConverter.Convert(ValidationExport.Read("malformed.xml"));

    [Test]
    public void It_reports_the_parser_location() =>
        _convert
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Invalid security metadata XML at line 2, column *");
}

[TestFixture]
public class Given_security_metadata_with_a_document_type
{
    private const string Xml =
        "<!DOCTYPE SecurityMetadata [<!ENTITY policy 'Allow'>]><SecurityMetadata><Claims /></SecurityMetadata>";

    private Action _convertText = null!;
    private Action _convertDocument = null!;

    [SetUp]
    public void Setup()
    {
        _convertText = () => OdsXmlToClaimsConverter.Convert(Xml);
        XDocument document = XDocument.Parse(Xml);
        _convertDocument = () => OdsXmlToClaimsConverter.Convert(document);
    }

    [Test]
    public void It_rejects_document_types_during_parsing()
    {
        _convertText
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Invalid security metadata XML*DTD*");
    }

    [Test]
    public void It_also_rejects_a_preparsed_document_type()
    {
        _convertDocument
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*document type declaration*");
    }
}

[TestFixture("/DefaultAuthorization", "<Actions><Action name='Read' /></Actions>", "Actions")]
[TestFixture("/ClaimSets/ClaimSet", "<Action name='Read' />", "Action")]
[TestFixture(
    "/DefaultAuthorization/Action",
    "<AuthorizationStrategyOverrides />",
    "AuthorizationStrategyOverrides"
)]
[TestFixture("/ClaimSets/ClaimSet/Actions/Action", "<AuthorizationStrategies />", "AuthorizationStrategies")]
[TestFixture(
    "/DefaultAuthorization/Action",
    "<AuthorizationStrategy name='Custom' />",
    "AuthorizationStrategy"
)]
[TestFixture(
    "/ClaimSets/ClaimSet/Actions/Action",
    "<AuthorizationStrategy name='Custom' />",
    "AuthorizationStrategy"
)]
public class Given_malformed_action_structure(string path, string content, string elementName)
{
    private Action _convert = null!;

    [SetUp]
    public void Setup()
    {
        XDocument document = ValidationExport.Load();
        ValidationExport.Element(document, path).Add(XElement.Parse(content));
        _convert = () => OdsXmlToClaimsConverter.Convert(document);
    }

    [Test]
    public void It_rejects_misplaced_actions_and_strategy_declarations() =>
        _convert
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage($"*Unsupported element*{elementName}*parent*");
}

[TestFixture]
public class Given_an_export_that_generates_a_document_rejected_by_the_cms_schema
{
    private Action _convert = null!;
    private List<ClaimsValidationFailure> _failures = [];

    [SetUp]
    public void Setup()
    {
        // There is a real grant, so whole-document completeness passes. The second
        // reference's empty Actions is structurally recognized but violates CMS minItems.
        _convert = () =>
            OdsXmlToClaimsConverter.Convert(ValidationExport.Read("invalid-generated-actions.xml"));
        JsonNode invalidDocument = JsonNode.Parse(
            """
            {"claimSets":[{"claimSetName":"Reader","isSystemReserved":false},{"claimSetName":"Empty","isSystemReserved":false}],
             "claimsHierarchy":[{"name":"resource","claimSets":[
               {"name":"Reader","actions":[{"name":"Read"}]},{"name":"Empty","actions":[]}]}]}
            """
        )!;
        _failures = new ClaimsValidator(NullLogger<ClaimsValidator>.Instance).Validate(invalidDocument);
    }

    [Test]
    public void It_exercises_the_real_embedded_cms_schema() =>
        _failures
            .Should()
            .Contain(failure => failure.FailurePath.Value == "$.claimsHierarchy.0.claimSets.1.actions");

    [Test]
    public void It_cannot_return_the_invalid_generated_document()
    {
        string message = _convert.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().StartWith("Converted claims document failed CMS validation:");
        ClaimsValidationFailure failure = _failures.Single(failure =>
            failure.FailurePath.Value == "$.claimsHierarchy.0.claimSets.1.actions"
        );
        message.Should().Contain(failure.FailurePath.Value);
        foreach (string detail in failure.FailureMessages)
        {
            message.Should().Contain(detail);
        }
    }
}
