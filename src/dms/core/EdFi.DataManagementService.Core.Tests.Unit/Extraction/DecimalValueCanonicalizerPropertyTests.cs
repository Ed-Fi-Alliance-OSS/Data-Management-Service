// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.Extraction.NumericIdentityPropertyScenarios;

namespace EdFi.DataManagementService.Core.Tests.Unit.Extraction;

[TestFixture]
[Category("NumericIdentityProperty")]
[SetCulture("fr-FR")]
public class Given_Generated_Decimal_Scales
{
    [Test]
    public void It_removes_insignificant_scale_without_changing_value(
        [Values("integer", "fractional", "scale28", "maximum", "minimumUnit", "zero", "trailingZero")]
            string shape,
        [Values] bool negative
    )
    {
        Check(
            Generate(shape, negative),
            scenario =>
            {
                var normalized = DecimalValueCanonicalizer.NormalizeScale(scenario.Value);
                normalized.Should().Be(scenario.Value);
                // Decimal equality ignores scale and signed zero; inspect representation as well.
                var point = scenario.Expected.IndexOf('.');
                var expectedScale = point < 0 ? 0 : scenario.Expected.Length - point - 1;
                var expectedFlags =
                    (expectedScale << 16) | (scenario.Expected.StartsWith('-') ? int.MinValue : 0);
                decimal.GetBits(normalized)[3].Should().Be(expectedFlags);
                decimal.GetBits(DecimalValueCanonicalizer.NormalizeScale(normalized))
                    .Should()
                    .Equal(decimal.GetBits(normalized));
            },
            $"shape={shape}, negative={negative}, normalized decimal bits"
        );
    }
}
