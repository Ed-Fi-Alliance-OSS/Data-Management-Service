// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.Extraction.NumericIdentityPropertyScenarios;

namespace EdFi.DataManagementService.Core.Tests.Unit.Extraction;

[TestFixture]
[Category("NumericIdentityProperty")]
[SetCulture("fr-FR")]
public class Given_Generated_Numeric_Identity_Spellings
{
    [Test]
    public void It_preserves_exact_values_in_canonical_fixed_point_across_layers(
        [Values("integer", "fractional", "scale28", "maximum", "minimumUnit", "zero", "trailingZero")]
            string shape,
        [Values] bool negative
    )
    {
        Check(
            Generate(shape, negative),
            scenario =>
            {
                var path = new JsonPath("$.schoolReference.schoolId");
                foreach (var spelling in scenario.Spellings())
                {
                    // Guard the generator contract: equivalent inputs must not rely on rounding.
                    decimal.Parse(spelling, NumberStyles.Float, CultureInfo.InvariantCulture)
                        .Should()
                        .Be(scenario.Value);
                    var core = IdentityValueCanonicalizer.CanonicalizeDecimal(spelling);
                    var backend = DecimalValueCanonicalizer.CanonicalizeText(spelling);
                    core.Should().Be(scenario.Expected, $"input was {spelling}");
                    backend.Should().Be(scenario.Expected, $"input was {spelling}");
                    core.Should().Be(backend);
                    core.Should().MatchRegex(@"^(0|-?[1-9][0-9]*|-?(0|[1-9][0-9]*)\.[0-9]*[1-9])$");
                    decimal.Parse(core, CultureInfo.InvariantCulture).Should().Be(scenario.Value);
                    IdentityValueCanonicalizer.CanonicalizeDecimal(core).Should().Be(core);
                    DecimalValueCanonicalizer.CanonicalizeText(backend).Should().Be(backend);
                    IdentityValueCanonicalizer
                        .CreateDocumentIdentityElement(path, spelling, isNumeric: true)
                        .Should()
                        .Be(new DocumentIdentityElement(path, scenario.Expected));
                }
            },
            $"shape={shape}, negative={negative}, five equivalent spellings"
        );
    }

    [Test]
    public void It_preserves_descriptor_and_nonnumeric_paths_verbatim([Values] bool numeric)
    {
        Check(
            Generate("fractional", negative: false),
            scenario =>
            {
                // Numeric-looking text detects accidental numeric normalization even on descriptor paths.
                var values = scenario
                    .Spellings()
                    .Concat(
                        new[]
                        {
                            $"uri://Ed-Fi.org/TypeDescriptor#École-{scenario.Digits} Å/中",
                            $"  MixedCase-{scenario.Digits}\t",
                            "",
                        }
                    );
                foreach (var value in values)
                {
                    foreach (
                        var path in new[]
                        {
                            DocumentIdentity.DescriptorIdentityJsonPath,
                            new JsonPath("$.staffClassificationDescriptor"),
                            new JsonPath("$.items[*].typeDescriptor"),
                            new JsonPath("$[typeDescriptor]"),
                        }
                    )
                    {
                        IdentityValueCanonicalizer
                            .CreateDocumentIdentityElement(path, value, numeric)
                            .Should()
                            .Be(new DocumentIdentityElement(path, value));
                    }
                    var nonnumericPath = new JsonPath("$.sectionIdentifier");
                    IdentityValueCanonicalizer
                        .CreateDocumentIdentityElement(nonnumericPath, value, isNumeric: false)
                        .Should()
                        .Be(new DocumentIdentityElement(nonnumericPath, value));
                    IdentityValueCanonicalizer
                        .CreateDocumentIdentityElement(nonnumericPath, value)
                        .Should()
                        .Be(new DocumentIdentityElement(nonnumericPath, value));
                }
            },
            $"descriptor numeric={numeric}, numeric-looking text, mixed-case Unicode, empty text"
        );
    }

    [Test]
    public void It_preserves_unparseable_numeric_text()
    {
        Check(
            Generate("fractional", negative: true),
            scenario =>
            {
                foreach (
                    var value in new[]
                    {
                        scenario.Expected + "x",
                        scenario.Expected + "e+",
                        "1,5",
                        "NaN",
                        "Infinity",
                        "",
                        "79228162514264337593543950336",
                        "-79228162514264337593543950336",
                    }
                )
                {
                    decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        .Should()
                        .BeFalse();
                    IdentityValueCanonicalizer.CanonicalizeDecimal(value).Should().Be(value);
                    DecimalValueCanonicalizer.CanonicalizeText(value).Should().Be(value);
                    var path = new JsonPath("$.schoolId");
                    IdentityValueCanonicalizer
                        .CreateDocumentIdentityElement(path, value, isNumeric: true)
                        .Should()
                        .Be(new DocumentIdentityElement(path, value));
                }
            },
            "malformed, locale decimal separator, nonfinite, empty, and decimal overflow"
        );
    }
}
