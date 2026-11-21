// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public class JobCompositeErrorCodeTests
{
    private const string Composite = "RefreshIncomplete";
    private const string Alpha = "CategoryAlpha";
    private const string Beta = "CategoryBeta";
    private const string Gamma = "CategoryGamma";
    private const string Delta = "CategoryDelta";
    private const string AlphaMessage = "Alpha could not be read.";
    private const string GammaMessage = "Gamma off.";
    private const string DeltaMessage = "Delta is registered but not allowed.";
    private const string RoomyMessage = "Some data stores were not refreshed.";

    /// <summary>
    /// What a composite allowing Alpha, Beta and Gamma spends beyond its own message when all three are listed: the
    /// 21-character prefix, the three codes, two separators, and the period.
    /// </summary>
    private const int AllCategoriesOverhead = 21 + 13 + 12 + 13 + (2 * 2) + 1;

    /// <summary>60 characters, so it does not fit where a shorter message still would.</summary>
    private static readonly string _betaMessage = "Beta " + new string('b', 54) + ".";

    private static readonly string[] _allowed = [Alpha, Beta, Gamma];

    private static Exception? ThrownBy(Action registration)
    {
        try
        {
            registration();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>Fixed text of exactly <paramref name="length"/> characters.</summary>
    private static string MessageOfLength(int length) => new string('m', length - 1) + ".";

    private static ServiceCollection WithCategories()
    {
        ServiceCollection services = new();
        services.AddJobErrorCode(Alpha, AlphaMessage);
        services.AddJobErrorCode(Beta, _betaMessage);
        services.AddJobErrorCode(Gamma, GammaMessage);
        services.AddJobErrorCode(Delta, DeltaMessage);
        return services;
    }

    /// <summary>A registry with the categories and one composite, built from the public registration API.</summary>
    public abstract class RegistryFixture
    {
        private ServiceProvider? _provider;

        protected IJobErrorCodeRegistry Registry { get; private set; } = null!;

        protected void Build(string message, params string[] allowed)
        {
            ServiceCollection services = WithCategories();
            services.AddJobCompositeErrorCode(Composite, message, allowed.Length == 0 ? _allowed : allowed);
            _provider = services.BuildServiceProvider();
            Registry = _provider.GetRequiredService<IJobErrorCodeRegistry>();
        }

        protected string? Compose(params string[] categories) =>
            Registry.TryCompose(Composite, categories, out string? message) ? message : null;

        [TearDown]
        public void DisposeProvider() => _provider?.Dispose();
    }

    [TestFixture]
    public class Given_a_composite_whose_message_fits_every_category_with_prose : RegistryFixture
    {
        [SetUp]
        public void Setup() => Build(RoomyMessage);

        [Test]
        public void It_lists_the_codes_then_their_registered_prose_in_order() =>
            Compose(Beta, Alpha)
                .Should()
                .Be($"{RoomyMessage} Problem categories: {Beta}, {Alpha}. {_betaMessage} {AlphaMessage}");

        [Test]
        public void It_collapses_duplicates_in_first_occurrence_order() =>
            Compose(Gamma, Alpha, Gamma, Alpha, Gamma)
                .Should()
                .Be($"{RoomyMessage} Problem categories: {Gamma}, {Alpha}. {GammaMessage} {AlphaMessage}");

        [Test]
        public void It_lists_every_category_within_the_limit()
        {
            string? composed = Compose(Alpha, Beta, Gamma);
            composed.Should().Contain($"Problem categories: {Alpha}, {Beta}, {Gamma}.");
            composed!.Length.Should().BeLessThanOrEqualTo(1000);
        }

        [Test]
        public void It_is_deterministic() => Compose(Beta, Gamma).Should().Be(Compose(Beta, Gamma));

        [Test]
        public void It_does_not_resolve_the_composite_as_a_single_code() =>
            Registry.TryGet(Composite, out _).Should().BeFalse();
    }

    [TestFixture]
    public class Given_a_composite_with_no_spare_room_beyond_its_categories : RegistryFixture
    {
        private static readonly string _message = MessageOfLength(1000 - AllCategoriesOverhead);

        [SetUp]
        public void Setup() => Build(_message);

        [Test]
        public void It_lists_every_code_and_no_prose_in_exactly_1000_characters()
        {
            string? composed = Compose(Gamma, Beta, Alpha);
            composed.Should().Be($"{_message} Problem categories: {Gamma}, {Beta}, {Alpha}.");
            composed!.Length.Should().Be(1000);
        }
    }

    [TestFixture]
    public class Given_a_composite_with_room_for_only_part_of_the_prose : RegistryFixture
    {
        // The header is 950 characters: Alpha's prose fits (975), Beta's would not (1036), and Gamma's would still
        // fit after Alpha's (986) but follows Beta, so the prose stops at Beta.
        private static readonly string _message = MessageOfLength(950 - AllCategoriesOverhead);

        [SetUp]
        public void Setup() => Build(_message);

        [Test]
        public void It_appends_prose_only_while_it_fits_and_stops_at_the_first_that_does_not()
        {
            string? composed = Compose(Alpha, Beta, Gamma);
            composed.Should().Be($"{_message} Problem categories: {Alpha}, {Beta}, {Gamma}. {AlphaMessage}");
            composed!.Length.Should().Be(975);
        }
    }

    [TestFixture]
    public class Given_a_composite_with_an_infrastructure_category : RegistryFixture
    {
        [SetUp]
        public void Setup() => Build("Parts failed.", Alpha, JobErrorCode.TenantUnavailable.Code);

        [Test]
        public void It_uses_the_infrastructure_code_registered_text() =>
            Compose(JobErrorCode.TenantUnavailable.Code)
                .Should()
                .Be(
                    $"Parts failed. Problem categories: {JobErrorCode.TenantUnavailable.Code}. "
                        + JobErrorCode.TenantUnavailable.Message
                );
    }

    [TestFixture("UnregisteredPrimary", new[] { Alpha })]
    [TestFixture(Alpha, new[] { Alpha })]
    [TestFixture(Composite, new string[0])]
    [TestFixture(Composite, new[] { Alpha, "Unknown" })]
    [TestFixture(Composite, new[] { Alpha, Delta })]
    [TestFixture(Composite, new[] { "categoryalpha" })]
    [TestFixture(Composite, new[] { Alpha, "" })]
    public class Given_a_composition_that_must_fail_closed(string primary, string[] categories)
        : RegistryFixture
    {
        private bool _composed;
        private string? _message;

        [SetUp]
        public void Setup()
        {
            Build(RoomyMessage);
            _composed = Registry.TryCompose(primary, categories, out _message);
        }

        [Test]
        public void It_composes_no_message()
        {
            _composed.Should().BeFalse();
            _message.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_composites_either_side_of_the_capacity_limit
    {
        private Exception? _atLimit;
        private Exception? _overLimit;

        [SetUp]
        public void Setup()
        {
            _atLimit = ThrownBy(() =>
                WithCategories()
                    .AddJobCompositeErrorCode(
                        Composite,
                        MessageOfLength(1000 - AllCategoriesOverhead),
                        _allowed
                    )
            );
            _overLimit = ThrownBy(() =>
                WithCategories()
                    .AddJobCompositeErrorCode(
                        Composite,
                        MessageOfLength(1001 - AllCategoriesOverhead),
                        _allowed
                    )
            );
        }

        [Test]
        public void It_accepts_a_worst_case_of_1000_characters() => _atLimit.Should().BeNull();

        [Test]
        public void It_rejects_a_worst_case_of_1001_characters() =>
            _overLimit
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Contain("1001 characters");
    }

    [TestFixture("Unknown", "is not a registered error code")]
    [TestFixture(Alpha + "," + Alpha, "is listed more than once")]
    [TestFixture("Existing", "is itself a composite code")]
    public class Given_a_composite_whose_categories_break_the_rules(string categories, string expected)
    {
        private Exception? _thrown;

        [SetUp]
        public void Setup()
        {
            ServiceCollection services = WithCategories();
            services.AddJobCompositeErrorCode("Existing", "Another composite.", [Beta]);
            _thrown = ThrownBy(() =>
                services.AddJobCompositeErrorCode(Composite, "Parts failed.", categories.Split(','))
            );
        }

        [Test]
        public void It_fails_the_registration() =>
            _thrown.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain(expected);
    }

    [TestFixture]
    public class Given_a_category_registered_after_its_composite
    {
        private Exception? _thrown;

        [SetUp]
        public void Setup()
        {
            ServiceCollection services = new();
            _thrown = ThrownBy(() =>
            {
                services.AddJobCompositeErrorCode(Composite, "Parts failed.", [Alpha]);
                services.AddJobErrorCode(Alpha, AlphaMessage);
            });
        }

        [Test]
        public void It_fails_the_composite_registration() =>
            _thrown
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Contain("register each category before the composite");
    }

    [TestFixture]
    public class Given_a_composite_with_no_categories
    {
        private Exception? _empty;
        private Exception? _missing;

        [SetUp]
        public void Setup()
        {
            _empty = ThrownBy(() =>
                WithCategories().AddJobCompositeErrorCode(Composite, "Parts failed.", [])
            );
            _missing = ThrownBy(() =>
                WithCategories().AddJobCompositeErrorCode(Composite, "Parts failed.", null!)
            );
        }

        [Test]
        public void It_fails_the_registration_of_an_empty_list() =>
            _empty.Should().BeOfType<ArgumentException>().Which.ParamName.Should().Be("categoryCodes");

        [Test]
        public void It_fails_the_registration_of_a_missing_list() =>
            _missing.Should().BeOfType<ArgumentException>().Which.ParamName.Should().Be("categoryCodes");
    }

    [TestFixture]
    public class Given_a_composite_code_that_collides_with_another_code
    {
        private Exception? _compositeOverSingle;
        private Exception? _singleOverComposite;
        private Exception? _compositeOverInfrastructure;

        [SetUp]
        public void Setup()
        {
            _compositeOverSingle = ThrownBy(() =>
                WithCategories().AddJobCompositeErrorCode(Alpha, "Parts failed.", [Beta])
            );
            _singleOverComposite = ThrownBy(() =>
                WithCategories()
                    .AddJobCompositeErrorCode(Composite, "Parts failed.", [Beta])
                    .AddJobErrorCode(Composite, "A single code.")
            );
            _compositeOverInfrastructure = ThrownBy(() =>
                WithCategories()
                    .AddJobCompositeErrorCode(JobErrorCode.HandlerFailed.Code, "Parts failed.", [Beta])
            );
        }

        [Test]
        public void It_refuses_a_composite_with_a_single_code_name() =>
            _compositeOverSingle
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Contain("registered more than once");

        [Test]
        public void It_refuses_a_single_code_with_a_composite_name() =>
            _singleOverComposite
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Contain("registered more than once");

        [Test]
        public void It_refuses_to_redefine_an_infrastructure_code() =>
            _compositeOverInfrastructure
                .Should()
                .BeOfType<InvalidOperationException>()
                .Which.Message.Should()
                .Contain("infrastructure code");
    }

    [TestFixture]
    public class Given_a_composite_code_or_message_that_breaks_the_single_code_rules
    {
        private Exception? _badCode;
        private Exception? _badMessage;

        [SetUp]
        public void Setup()
        {
            _badCode = ThrownBy(() =>
                WithCategories().AddJobCompositeErrorCode("Refresh-Incomplete", "Parts failed.", [Beta])
            );
            _badMessage = ThrownBy(() =>
                WithCategories().AddJobCompositeErrorCode(Composite, "Parts of {0} failed.", [Beta])
            );
        }

        [Test]
        public void It_refuses_the_code() =>
            _badCode.Should().BeOfType<ArgumentException>().Which.ParamName.Should().Be("code");

        [Test]
        public void It_refuses_the_message() =>
            _badMessage.Should().BeOfType<ArgumentException>().Which.ParamName.Should().Be("message");
    }
}
