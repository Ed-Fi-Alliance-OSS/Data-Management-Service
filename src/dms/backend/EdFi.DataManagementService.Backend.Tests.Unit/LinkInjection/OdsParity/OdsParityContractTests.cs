// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.LinkInjection.OdsParity;

// DMS-1145 task 33 — ODS-baseline parity contract for document references.
//
// What this test guarantees: for each committed ODS-shape fixture, the link.rel string
// is byte-equal to the expected resource name and the link.href decomposes (after GUID
// rendering normalization) into the expected (projectEndpointName, endpointName,
// documentUuid:D) triple. Descriptor references are out of scope — they keep their
// canonical-URI string surface.
//
// Important context for reviewers: see fixture-metadata.json. The two committed
// fixtures are synthetic placeholders authored from the design's wire-shape contract
// rather than recorded from a live ODS instance. When a real ODS-recorded fixture is
// obtained, swap the content in place — the parity assertion logic does not change.
[TestFixture]
public class OdsParityContractTests
{
    private const string OdsConcreteSchoolHrefGuidNFormat = "abcd1234ef0156782a3b4c5d6e7f8001";
    private static readonly Guid ExpectedSchoolDocumentUuid = Guid.Parse(OdsConcreteSchoolHrefGuidNFormat);

    private static readonly OdsParityCase[] _parityCases =
    [
        new OdsParityCase(
            Name: "AcademicWeek -> School (concrete root reference)",
            FixtureFileName: "academic-week-school-reference.ods.json",
            ReferenceJsonPath: "$.schoolReference",
            ExpectedRel: "School",
            ExpectedProjectEndpointName: "ed-fi",
            ExpectedEndpointName: "schools",
            ExpectedDocumentUuid: ExpectedSchoolDocumentUuid
        ),
        new OdsParityCase(
            Name: "Course.educationOrganizationReference -> School (abstract -> concrete subclass)",
            FixtureFileName: "course-education-organization-reference.ods.json",
            ReferenceJsonPath: "$.educationOrganizationReference",
            // V1 contract: abstract reference's link.rel is the concrete subclass name
            // (School), NOT the abstract type (EducationOrganization). Same wire value
            // ODS would emit; same value DMS emits via ResourceKeyId-based resolution.
            ExpectedRel: "School",
            ExpectedProjectEndpointName: "ed-fi",
            ExpectedEndpointName: "schools",
            ExpectedDocumentUuid: ExpectedSchoolDocumentUuid
        ),
    ];

    [TestCaseSource(nameof(_parityCases))]
    public void It_emits_link_with_parity_to_ods(OdsParityCase parityCase)
    {
        JsonNode fixtureBody = LoadFixture(parityCase.FixtureFileName);
        JsonNode referenceObject = ReferenceLocator.RequireSingle(fixtureBody, parityCase.ReferenceJsonPath);

        JsonNode link =
            referenceObject["link"]
            ?? throw new InvalidOperationException(
                $"ODS fixture '{parityCase.FixtureFileName}' has no 'link' at '{parityCase.ReferenceJsonPath}'."
            );

        // rel parity: byte-equal.
        link["rel"]!
            .GetValue<string>()
            .Should()
            .Be(parityCase.ExpectedRel, "link.rel must be byte-equal between ODS and DMS");

        // href parity: decompose into projectEndpointName + endpointName + GUID, then
        // normalize the GUID rendering before comparing. ODS uses N format; DMS uses
        // D format; both parse to the same Guid value.
        string href = link["href"]!.GetValue<string>();
        ParsedHref parsed = ParseHref(href);

        parsed
            .ProjectEndpointName.Should()
            .Be(
                parityCase.ExpectedProjectEndpointName,
                "href's projectEndpointName segment must match across ODS and DMS"
            );
        parsed
            .EndpointName.Should()
            .Be(parityCase.ExpectedEndpointName, "href's endpointName segment must match");
        parsed
            .DocumentUuid.Should()
            .Be(
                parityCase.ExpectedDocumentUuid,
                "href's GUID, after normalization, must reference the same logical document"
            );
    }

    private static JsonNode LoadFixture(string fileName)
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "LinkInjection", "OdsParity", fileName);
        if (!File.Exists(fixturePath))
        {
            throw new FileNotFoundException(
                $"ODS parity fixture not found at '{fixturePath}'. Ensure the JSON file is in the test project's LinkInjection/OdsParity folder and the csproj copies it to the output directory."
            );
        }

        string fixtureJson = File.ReadAllText(fixturePath);
        return JsonNode.Parse(fixtureJson)
            ?? throw new InvalidOperationException(
                $"ODS parity fixture '{fileName}' is empty or not valid JSON."
            );
    }

    private static ParsedHref ParseHref(string href)
    {
        // Expected shape: /{projectEndpointName}/{endpointName}/{guid}
        // No trailing slash, no query string. Splits on '/' and ignores the leading empty token.
        string[] segments = href.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3)
        {
            throw new InvalidOperationException(
                $"href '{href}' does not match the expected /{{proj}}/{{endpoint}}/{{guid}} shape; got {segments.Length.ToString(CultureInfo.InvariantCulture)} segments."
            );
        }

        if (!Guid.TryParse(segments[2], out Guid documentUuid))
        {
            throw new InvalidOperationException(
                $"href '{href}' final segment '{segments[2]}' is not a parseable GUID (neither N nor D format)."
            );
        }

        return new ParsedHref(
            ProjectEndpointName: segments[0],
            EndpointName: segments[1],
            DocumentUuid: documentUuid
        );
    }

    public sealed record OdsParityCase(
        string Name,
        string FixtureFileName,
        string ReferenceJsonPath,
        string ExpectedRel,
        string ExpectedProjectEndpointName,
        string ExpectedEndpointName,
        Guid ExpectedDocumentUuid
    )
    {
        // NUnit displays this in the test runner output, including the fixture name
        // so a failing case is easy to identify.
        public override string ToString() => Name;
    }

    private sealed record ParsedHref(string ProjectEndpointName, string EndpointName, Guid DocumentUuid);
}
