// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;
using EdFi.DataManagementService.CustomValidation;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Validation;

[TestFixture]
[Parallelizable]
public class Given_An_Invalid_Json_Path_For_A_Path_Failure
{
    [Test]
    public void It_rejects_a_null_json_path()
    {
        Action construct = () => _ = new CustomValidationFailure.OnPath(null!, "a message");

        construct.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_an_empty_json_path()
    {
        Action construct = () => _ = new CustomValidationFailure.OnPath("", "a message");

        construct.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_a_whitespace_only_json_path()
    {
        Action construct = () => _ = new CustomValidationFailure.OnPath("   ", "a message");

        construct.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_a_bare_dollar_json_path()
    {
        // "$" is not "$."-prefixed. Rejected on purpose, while a bare "$." is accepted below.
        Action construct = () => _ = new CustomValidationFailure.OnPath("$", "a message");

        construct.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_a_json_path_that_is_not_dollar_dot_prefixed()
    {
        Action construct = () => _ = new CustomValidationFailure.OnPath("name", "a message");

        construct.Should().Throw<ArgumentException>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_An_Invalid_Message_For_A_Failure
{
    [Test]
    public void It_rejects_a_null_message_on_a_path_failure()
    {
        Action construct = () => _ = new CustomValidationFailure.OnPath("$.name", null!);

        construct.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_an_empty_message_on_a_path_failure()
    {
        Action construct = () => _ = new CustomValidationFailure.OnPath("$.name", "");

        construct.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_a_whitespace_only_message_on_a_path_failure()
    {
        Action construct = () => _ = new CustomValidationFailure.OnPath("$.name", "   ");

        construct.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_a_null_message_on_a_resource_failure()
    {
        Action construct = () => _ = new CustomValidationFailure.OnResource(null!);

        construct.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_an_empty_message_on_a_resource_failure()
    {
        Action construct = () => _ = new CustomValidationFailure.OnResource("");

        construct.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_a_whitespace_only_message_on_a_resource_failure()
    {
        Action construct = () => _ = new CustomValidationFailure.OnResource("   ");

        construct.Should().Throw<ArgumentException>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Document_Level_Path_Failure
{
    private CustomValidationFailure.OnPath _failure = null!;

    [SetUp]
    public void Setup()
    {
        // "$." is DMS's own document-level validationErrors key (see DocumentValidator and
        // ParseBodyMiddleware), so it must remain expressible through OnPath rather than being folded
        // into OnResource.
        _failure = new CustomValidationFailure.OnPath("$.", "a message");
    }

    [Test]
    public void It_accepts_a_bare_dollar_dot_path()
    {
        _failure.JsonPath.Should().Be("$.");
    }

    [Test]
    public void It_carries_the_message()
    {
        _failure.Message.Should().Be("a message");
    }
}

[TestFixture]
[Parallelizable]
public class Given_A_Resource_Level_Failure
{
    private CustomValidationFailure.OnResource _failure = null!;

    [SetUp]
    public void Setup()
    {
        // OnResource is the only way to express a failure carrying no path, so a constructor that
        // rejected every input would make that case unreachable while every rejection test above
        // still passed.
        _failure = new CustomValidationFailure.OnResource("a message");
    }

    [Test]
    public void It_accepts_a_non_empty_message()
    {
        _failure.Message.Should().Be("a message");
    }

    [Test]
    public void It_is_a_failure_of_the_closed_hierarchy()
    {
        _failure.Should().BeAssignableTo<CustomValidationFailure>();
    }
}

[TestFixture]
[Parallelizable]
public class Given_The_Failure_Hierarchy
{
    private MethodInfo[] _closureMembers = null!;
    private ConstructorInfo[] _externallyReachableConstructors = null!;
    private InternalsVisibleToAttribute[] _friendGrants = null!;
    private Type[] _cases = null!;

    [SetUp]
    public void Setup()
    {
        // The private base constructor does not close a record hierarchy on its own: the compiler
        // synthesizes a protected copy constructor on every unsealed record, and an external
        // assembly can chain to it with `: base(source)`. That derivation was verified to compile
        // before this closure was added. What blocks it is an abstract member no external type can
        // override, so that is what these tests pin: drop the member and a third case becomes
        // declarable outside this assembly, silently breaking every exhaustive switch over it.
        _closureMembers = typeof(CustomValidationFailure)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.IsAbstract && method.IsFamilyAndAssembly)
            .ToArray();

        _externallyReachableConstructors = typeof(CustomValidationFailure)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(constructor =>
                constructor.IsPublic || constructor.IsFamily || constructor.IsFamilyOrAssembly
            )
            .ToArray();

        _friendGrants = typeof(CustomValidationFailure)
            .Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .ToArray();

        _cases = typeof(CustomValidationFailure)
            .Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(CustomValidationFailure)))
            .ToArray();
    }

    [Test]
    public void It_is_closed_against_derivation_from_another_assembly()
    {
        _closureMembers
            .Should()
            .ContainSingle("an abstract private protected member is what makes the hierarchy closed");
    }

    [Test]
    public void It_grants_no_friend_assembly_access()
    {
        // `private protected` extends to friend assemblies, so an InternalsVisibleTo grant on this
        // assembly would let a friend override the closure member and declare a concrete third case.
        // Probe-verified: with a grant the derivation compiles, without it the same source fails
        // CS0534 and CS0115. The closure test above counts the member and would not notice, so the
        // absence of a grant is asserted separately.
        _friendGrants.Should().BeEmpty("a friend assembly can override the closure member");
    }

    [Test]
    public void It_exposes_only_the_synthesized_copy_constructor()
    {
        // The declared parameterless constructor is private, so the only externally reachable
        // constructor is the one the compiler synthesizes. C# forbids narrowing it (CS8878), which is
        // why closure is enforced by the abstract member rather than by hiding this.
        _externallyReachableConstructors
            .Should()
            .ContainSingle()
            .Which.GetParameters()
            .Should()
            .ContainSingle()
            .Which.ParameterType.Should()
            .Be<CustomValidationFailure>();
    }

    [Test]
    public void It_declares_exactly_two_failure_cases()
    {
        _cases
            .Should()
            .BeEquivalentTo(
                new[] { typeof(CustomValidationFailure.OnPath), typeof(CustomValidationFailure.OnResource) }
            );
    }

    [Test]
    public void It_seals_every_failure_case()
    {
        _cases.Should().OnlyContain(type => type.IsSealed);
    }
}

[TestFixture]
[Parallelizable]
public class Given_The_Write_Operation_Enum
{
    // CustomValidationOperation.cs documents an append-only rule: `default` is Upsert, and a member
    // is never inserted because inserting one renumbers the rest. A validator compiled against an
    // older version binds the ordinal, not the name, so a reorder silently inverts its meaning
    // while every name-based reference in this repository keeps compiling. These pin the ordinals
    // so that promise is enforced rather than merely stated.
    [Test]
    public void It_numbers_upsert_zero_so_default_is_upsert()
    {
        ((int)CustomValidationOperation.Upsert).Should().Be(0);
    }

    [Test]
    public void It_numbers_update_one()
    {
        ((int)CustomValidationOperation.Update).Should().Be(1);
    }

    [Test]
    public void It_declares_exactly_the_two_known_operations()
    {
        Enum.GetNames<CustomValidationOperation>()
            .Should()
            .Equal(new[] { "Upsert", "Update" }, "a new member is appended, never inserted");
    }
}
