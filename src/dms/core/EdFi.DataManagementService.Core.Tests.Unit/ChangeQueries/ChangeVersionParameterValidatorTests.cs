// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ChangeQueries;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ChangeQueries;

[TestFixture]
[Parallelizable]
public class ChangeVersionParameterValidatorTests
{
    private const string MinError = "MinChangeVersion must be a numeric value greater than or equal to 0.";
    private const string MaxError = "MaxChangeVersion must be a numeric value greater than or equal to 0.";
    private const string InvertedError = "MinChangeVersion must be less than or equal to MaxChangeVersion.";

    [TestFixture]
    [Parallelizable]
    public class Given_No_Change_Version_Parameters : ChangeVersionParameterValidatorTests
    {
        private ChangeVersionValidationResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ChangeVersionParameterValidator.Validate(new Dictionary<string, string>());

        [Test]
        public void It_returns_no_errors() => _result.Errors.Should().BeEmpty();

        [Test]
        public void It_returns_the_none_range() => _result.Range.Should().Be(ChangeVersionRange.None);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Only_Min_Is_Valid : ChangeVersionParameterValidatorTests
    {
        private ChangeVersionValidationResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ChangeVersionParameterValidator.Validate(
                new Dictionary<string, string> { { "minChangeVersion", "5" } }
            );

        [Test]
        public void It_returns_no_errors() => _result.Errors.Should().BeEmpty();

        [Test]
        public void It_sets_only_the_min_bound() =>
            _result.Range.Should().Be(new ChangeVersionRange(5, null));
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Only_Max_Is_Valid : ChangeVersionParameterValidatorTests
    {
        private ChangeVersionValidationResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ChangeVersionParameterValidator.Validate(
                new Dictionary<string, string> { { "maxChangeVersion", "9" } }
            );

        [Test]
        public void It_returns_no_errors() => _result.Errors.Should().BeEmpty();

        [Test]
        public void It_sets_only_the_max_bound() =>
            _result.Range.Should().Be(new ChangeVersionRange(null, 9));
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Both_Valid_With_Min_Less_Than_Max : ChangeVersionParameterValidatorTests
    {
        private ChangeVersionValidationResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ChangeVersionParameterValidator.Validate(
                new Dictionary<string, string> { { "minChangeVersion", "1" }, { "maxChangeVersion", "2" } }
            );

        [Test]
        public void It_returns_no_errors() => _result.Errors.Should().BeEmpty();

        [Test]
        public void It_sets_both_bounds() => _result.Range.Should().Be(new ChangeVersionRange(1, 2));
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Both_Valid_And_Equal : ChangeVersionParameterValidatorTests
    {
        private ChangeVersionValidationResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ChangeVersionParameterValidator.Validate(
                new Dictionary<string, string> { { "minChangeVersion", "7" }, { "maxChangeVersion", "7" } }
            );

        [Test]
        public void It_returns_no_errors() => _result.Errors.Should().BeEmpty();

        [Test]
        public void It_sets_both_bounds() => _result.Range.Should().Be(new ChangeVersionRange(7, 7));
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Min_Is_Not_Numeric : ChangeVersionParameterValidatorTests
    {
        private ChangeVersionValidationResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ChangeVersionParameterValidator.Validate(
                new Dictionary<string, string> { { "minChangeVersion", "abc" } }
            );

        [Test]
        public void It_reports_the_min_error() =>
            _result.Errors.Should().ContainSingle().Which.Should().Be(MinError);

        [Test]
        public void It_leaves_min_unset() => _result.Range.MinChangeVersion.Should().BeNull();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Min_Is_Negative : ChangeVersionParameterValidatorTests
    {
        private ChangeVersionValidationResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ChangeVersionParameterValidator.Validate(
                new Dictionary<string, string> { { "minChangeVersion", "-1" } }
            );

        [Test]
        public void It_reports_the_min_error() =>
            _result.Errors.Should().ContainSingle().Which.Should().Be(MinError);

        [Test]
        public void It_leaves_min_unset() => _result.Range.MinChangeVersion.Should().BeNull();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Max_Is_Not_Numeric : ChangeVersionParameterValidatorTests
    {
        private ChangeVersionValidationResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ChangeVersionParameterValidator.Validate(
                new Dictionary<string, string> { { "maxChangeVersion", "xyz" } }
            );

        [Test]
        public void It_reports_the_max_error() =>
            _result.Errors.Should().ContainSingle().Which.Should().Be(MaxError);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Max_Is_Negative : ChangeVersionParameterValidatorTests
    {
        private ChangeVersionValidationResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ChangeVersionParameterValidator.Validate(
                new Dictionary<string, string> { { "maxChangeVersion", "-3" } }
            );

        [Test]
        public void It_reports_the_max_error() =>
            _result.Errors.Should().ContainSingle().Which.Should().Be(MaxError);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Inverted_Range : ChangeVersionParameterValidatorTests
    {
        private ChangeVersionValidationResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ChangeVersionParameterValidator.Validate(
                new Dictionary<string, string> { { "minChangeVersion", "10" }, { "maxChangeVersion", "5" } }
            );

        [Test]
        public void It_reports_only_the_inverted_error() =>
            _result.Errors.Should().ContainSingle().Which.Should().Be(InvertedError);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Both_Are_Not_Numeric : ChangeVersionParameterValidatorTests
    {
        private ChangeVersionValidationResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ChangeVersionParameterValidator.Validate(
                new Dictionary<string, string> { { "minChangeVersion", "a" }, { "maxChangeVersion", "b" } }
            );

        [Test]
        public void It_reports_min_before_max_and_no_inverted_error() =>
            _result.Errors.Should().Equal(MinError, MaxError);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Min_Invalid_And_Max_Valid : ChangeVersionParameterValidatorTests
    {
        private ChangeVersionValidationResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ChangeVersionParameterValidator.Validate(
                new Dictionary<string, string> { { "minChangeVersion", "a" }, { "maxChangeVersion", "5" } }
            );

        [Test]
        public void It_reports_only_the_min_parse_error() =>
            _result.Errors.Should().ContainSingle().Which.Should().Be(MinError);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Value_Above_Int_Max : ChangeVersionParameterValidatorTests
    {
        private ChangeVersionValidationResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ChangeVersionParameterValidator.Validate(
                new Dictionary<string, string> { { "minChangeVersion", "3000000000" } }
            );

        [Test]
        public void It_accepts_the_long_value() =>
            _result.Range.Should().Be(new ChangeVersionRange(3000000000L, null));

        [Test]
        public void It_returns_no_errors() => _result.Errors.Should().BeEmpty();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Mixed_Case_Parameter_Keys : ChangeVersionParameterValidatorTests
    {
        private ChangeVersionValidationResult _result = null!;

        [SetUp]
        public void Setup() =>
            _result = ChangeVersionParameterValidator.Validate(
                new Dictionary<string, string> { { "MinChangeVersion", "1" }, { "MAXCHANGEVERSION", "2" } }
            );

        [Test]
        public void It_recognizes_the_keys_case_insensitively() =>
            _result.Range.Should().Be(new ChangeVersionRange(1, 2));

        [Test]
        public void It_returns_no_errors() => _result.Errors.Should().BeEmpty();
    }
}
