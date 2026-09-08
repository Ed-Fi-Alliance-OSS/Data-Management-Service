// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Extraction;

[TestFixture]
[Parallelizable]
public class IdentityValueCanonicalizerTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_CanonicalizeDecimal_With_Integer_Value : IdentityValueCanonicalizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = IdentityValueCanonicalizer.CanonicalizeDecimal("1");
        }

        [Test]
        public void It_returns_the_integer_without_decimal_point()
        {
            _result.Should().Be("1");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_CanonicalizeDecimal_With_Simple_Decimal : IdentityValueCanonicalizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = IdentityValueCanonicalizer.CanonicalizeDecimal("1.5");
        }

        [Test]
        public void It_returns_the_decimal_as_is()
        {
            _result.Should().Be("1.5");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_CanonicalizeDecimal_With_Trailing_Fractional_Zero : IdentityValueCanonicalizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = IdentityValueCanonicalizer.CanonicalizeDecimal("1.50");
        }

        [Test]
        public void It_strips_the_trailing_fractional_zero()
        {
            _result.Should().Be("1.5");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_CanonicalizeDecimal_With_Only_Fractional_Zeros : IdentityValueCanonicalizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = IdentityValueCanonicalizer.CanonicalizeDecimal("2.00");
        }

        [Test]
        public void It_strips_the_decimal_point_and_fractional_zeros()
        {
            _result.Should().Be("2");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_CanonicalizeDecimal_With_Scientific_Notation : IdentityValueCanonicalizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = IdentityValueCanonicalizer.CanonicalizeDecimal("1e2");
        }

        [Test]
        public void It_expands_to_fixed_point()
        {
            _result.Should().Be("100");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_CanonicalizeDecimal_With_Scientific_Notation_Zero_Exponent
        : IdentityValueCanonicalizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = IdentityValueCanonicalizer.CanonicalizeDecimal("1.50e0");
        }

        [Test]
        public void It_strips_the_exponent_and_trailing_fractional_zero()
        {
            _result.Should().Be("1.5");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_CanonicalizeDecimal_With_Negative_Exponent : IdentityValueCanonicalizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = IdentityValueCanonicalizer.CanonicalizeDecimal("1.23e-1");
        }

        [Test]
        public void It_moves_decimal_point_left_with_leading_zero()
        {
            _result.Should().Be("0.123");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_CanonicalizeDecimal_With_Negative_Zero : IdentityValueCanonicalizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = IdentityValueCanonicalizer.CanonicalizeDecimal("-0.00");
        }

        [Test]
        public void It_collapses_signed_zero_to_zero()
        {
            _result.Should().Be("0");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_CanonicalizeDecimal_With_Negative_Decimal : IdentityValueCanonicalizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            _result = IdentityValueCanonicalizer.CanonicalizeDecimal("-1.50");
        }

        [Test]
        public void It_preserves_the_negative_sign_and_strips_trailing_zero()
        {
            _result.Should().Be("-1.5");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_CanonicalizeDecimal_With_Very_Small_Decimal : IdentityValueCanonicalizerTests
    {
        private string _result = string.Empty;

        [SetUp]
        public void Setup()
        {
            // Smallest representable positive System.Decimal — historically risky because
            // ToString("G29") would render this as "1E-28", diverging from the SQL trigger
            // and lookup-verification expressions that emit fixed-point text.
            _result = IdentityValueCanonicalizer.CanonicalizeDecimal("1e-28");
        }

        [Test]
        public void It_emits_fixed_point_form_without_scientific_notation()
        {
            _result.Should().Be("0.0000000000000000000000000001");
            _result.Should().NotContain("E");
            _result.Should().NotContain("e");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_CreateDocumentIdentityElement_With_Descriptor_Path : IdentityValueCanonicalizerTests
    {
        private DocumentIdentityElement _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = IdentityValueCanonicalizer.CreateDocumentIdentityElement(
                DocumentIdentity.DescriptorIdentityJsonPath,
                "uri://ed-fi.org/StaffClassificationDescriptor#Kindergarten Teacher"
            );
        }

        [Test]
        public void It_lower_cases_the_descriptor_value()
        {
            _result
                .IdentityValue.Should()
                .Be("uri://ed-fi.org/staffclassificationdescriptor#kindergarten teacher");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_CreateDocumentIdentityElement_With_Non_Numeric_Non_Descriptor_Path
        : IdentityValueCanonicalizerTests
    {
        private DocumentIdentityElement _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = IdentityValueCanonicalizer.CreateDocumentIdentityElement(
                new JsonPath("$.sectionIdentifier"),
                "MySection-ABC"
            );
        }

        [Test]
        public void It_passes_through_the_value_unchanged()
        {
            _result.IdentityValue.Should().Be("MySection-ABC");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_CreateDocumentIdentityElement_With_Numeric_Path_And_Scientific_Notation
        : IdentityValueCanonicalizerTests
    {
        private DocumentIdentityElement _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = IdentityValueCanonicalizer.CreateDocumentIdentityElement(
                new JsonPath("$.schoolId"),
                "1e3",
                isNumeric: true
            );
        }

        [Test]
        public void It_canonicalizes_the_numeric_value_to_fixed_point()
        {
            _result.IdentityValue.Should().Be("1000");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_CreateDocumentIdentityElement_With_Numeric_Path_And_Descriptor_Suffix
        : IdentityValueCanonicalizerTests
    {
        private DocumentIdentityElement _result = null!;

        [SetUp]
        public void Setup()
        {
            // A path ending in "Descriptor" is always treated as descriptor (lower-cased),
            // even when isNumeric is true — descriptor path wins.
            _result = IdentityValueCanonicalizer.CreateDocumentIdentityElement(
                new JsonPath("$.staffClassificationDescriptor"),
                "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                isNumeric: true
            );
        }

        [Test]
        public void It_lower_cases_the_descriptor_value_ignoring_isNumeric()
        {
            _result.IdentityValue.Should().Be("uri://ed-fi.org/staffclassificationdescriptor#teacher");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_CreateDocumentIdentityElement_Default_IsNumeric_False : IdentityValueCanonicalizerTests
    {
        private DocumentIdentityElement _withDefault = null!;
        private DocumentIdentityElement _withExplicitFalse = null!;

        [SetUp]
        public void Setup()
        {
            _withDefault = IdentityValueCanonicalizer.CreateDocumentIdentityElement(
                new JsonPath("$.schoolYear"),
                "2030"
            );
            _withExplicitFalse = IdentityValueCanonicalizer.CreateDocumentIdentityElement(
                new JsonPath("$.schoolYear"),
                "2030",
                isNumeric: false
            );
        }

        [Test]
        public void It_passes_through_without_numeric_canonicalization_when_default()
        {
            _withDefault.IdentityValue.Should().Be("2030");
        }

        [Test]
        public void It_passes_through_without_numeric_canonicalization_when_explicit_false()
        {
            _withExplicitFalse.IdentityValue.Should().Be("2030");
        }
    }
}
