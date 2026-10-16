// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Model;

[TestFixture]
[Parallelizable]
public class DocumentIdentityComparerTests
{
    private static DocumentIdentity Identity(params (string Path, string Value)[] elements) =>
        new([
            .. elements.Select(element => new DocumentIdentityElement(
                new JsonPath(element.Path),
                element.Value
            )),
        ]);

    private static readonly (string Path, string Value)[] _schoolPeriod =
    [
        ("$.classPeriodName", "01 - Traditional"),
        ("$.schoolId", "255901"),
    ];

    [TestFixture]
    [Parallelizable]
    public class Given_Equal_Identities_Built_On_Separate_Arrays : DocumentIdentityComparerTests
    {
        private DocumentIdentity _left = null!;
        private DocumentIdentity _right = null!;

        [SetUp]
        public void Setup()
        {
            _left = Identity(_schoolPeriod);
            _right = Identity(_schoolPeriod);
        }

        [Test]
        public void It_holds_them_on_different_arrays() =>
            _left.DocumentIdentityElements.Should().NotBeSameAs(_right.DocumentIdentityElements);

        [Test]
        public void It_compares_them_equal() =>
            DocumentIdentityComparer.Instance.Equals(_left, _right).Should().BeTrue();

        [Test]
        public void It_gives_them_the_same_hash_code() =>
            DocumentIdentityComparer
                .Instance.GetHashCode(_left)
                .Should()
                .Be(DocumentIdentityComparer.Instance.GetHashCode(_right));

        [Test]
        public void It_treats_them_as_one_key_in_a_set() =>
            new HashSet<DocumentIdentity>([_left, _right], DocumentIdentityComparer.Instance)
                .Should()
                .ContainSingle();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Identities_That_Differ_Structurally : DocumentIdentityComparerTests
    {
        private DocumentIdentity _baseline = null!;

        [SetUp]
        public void Setup() => _baseline = Identity(_schoolPeriod);

        [Test]
        public void It_distinguishes_a_different_value() =>
            DocumentIdentityComparer
                .Instance.Equals(
                    _baseline,
                    Identity(("$.classPeriodName", "02 - Block"), ("$.schoolId", "255901"))
                )
                .Should()
                .BeFalse();

        [Test]
        public void It_distinguishes_a_different_path() =>
            DocumentIdentityComparer
                .Instance.Equals(
                    _baseline,
                    Identity(
                        ("$.classPeriodName", "01 - Traditional"),
                        ("$.localEducationAgencyId", "255901")
                    )
                )
                .Should()
                .BeFalse();

        [Test]
        public void It_distinguishes_a_different_order() =>
            DocumentIdentityComparer
                .Instance.Equals(_baseline, Identity(_schoolPeriod[1], _schoolPeriod[0]))
                .Should()
                .BeFalse();

        [Test]
        public void It_distinguishes_a_different_length() =>
            DocumentIdentityComparer
                .Instance.Equals(_baseline, Identity(_schoolPeriod[0]))
                .Should()
                .BeFalse();

        [Test]
        public void It_distinguishes_a_value_that_differs_only_in_case() =>
            DocumentIdentityComparer
                .Instance.Equals(
                    _baseline,
                    Identity(("$.classPeriodName", "01 - TRADITIONAL"), ("$.schoolId", "255901"))
                )
                .Should()
                .BeFalse();
    }
}
