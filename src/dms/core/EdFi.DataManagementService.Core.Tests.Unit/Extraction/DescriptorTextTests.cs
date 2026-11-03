// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Extraction;

[TestFixture]
[Parallelizable]
public class DescriptorTextTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_Well_Formed_Text_Without_Nul : DescriptorTextTests
    {
        [TestCase("")]
        [TestCase("uri://ed-fi.org/SexDescriptor#Female")]
        [TestCase("uri://ed-fi.org/AcademicSubjectDescriptor#École")]
        [TestCase("ΑΒΓΣ")]
        [TestCase("😀")]
        [TestCase("a😀b")]
        public void It_accepts_the_value_and_the_assert_returns_it(string value)
        {
            DescriptorText.IsWellFormedWithoutNul(value).Should().BeTrue();
            DescriptorText.AssertWellFormedWithoutNul(value).Should().BeSameAs(value);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Text_With_Nul_Or_An_Unpaired_Surrogate : DescriptorTextTests
    {
        // Supplied from code: attribute arguments are stored as UTF-8 metadata, which replaces a lone
        // surrogate with U+FFFD before the test ever sees it.
        private static readonly TestCaseData[] InvalidValues =
        [
            new TestCaseData("\0uri://ed-fi.org/SexDescriptor#Female").SetArgDisplayNames("NUL at the start"),
            new TestCaseData("uri://ed-fi.org/Sex\0Descriptor#Female").SetArgDisplayNames(
                "NUL in the middle"
            ),
            new TestCaseData("uri://ed-fi.org/SexDescriptor#Female\0").SetArgDisplayNames("NUL at the end"),
            new TestCaseData("abc\uD800").SetArgDisplayNames("lone high surrogate at the end"),
            new TestCaseData("abc\uD800def").SetArgDisplayNames("high surrogate before a non-surrogate"),
            new TestCaseData("\uDC00abc").SetArgDisplayNames("lone low surrogate"),
            new TestCaseData("\uDE00\uD83D").SetArgDisplayNames("reversed pair"),
        ];

        private static readonly TestCaseData[] SecretInvalidValues =
        [
            new TestCaseData("SECRET-\0-VALUE").SetArgDisplayNames("NUL"),
            new TestCaseData("SECRET-\uD800-VALUE").SetArgDisplayNames("lone surrogate"),
        ];

        [TestCaseSource(nameof(InvalidValues))]
        public void It_rejects_the_value(string value)
        {
            DescriptorText.IsWellFormedWithoutNul(value).Should().BeFalse();
        }

        [TestCaseSource(nameof(SecretInvalidValues))]
        public void It_asserts_without_echoing_the_value(string value)
        {
            Action act = () => DescriptorText.AssertWellFormedWithoutNul(value);

            var exception = act.Should().Throw<InvalidOperationException>().Which;
            exception.Message.Should().NotContain("SECRET");
        }
    }
}
