// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

// The comparer boundary below is a deliberate pin: if a .NET casing-table change moves any of these
// verdicts, the change must surface here as a test diff rather than as a silent identity change.

[TestFixture]
public class Given_The_SqlServer_Identity_Equality_Contract
{
    private IdentityEqualityContract _contract = null!;
    private StringComparer _comparer = null!;

    [SetUp]
    public void Setup()
    {
        _contract = IdentityEqualityContract.SqlServer;
        _comparer = _contract.IdentityTextComparer;
    }

    [Test]
    public void It_declares_the_SQL_Latin1_General_CP1_CI_AS_identity_collation()
    {
        _contract.IdentityTextCollation.Should().Be("SQL_Latin1_General_CP1_CI_AS");
    }

    [Test]
    public void It_equates_ascii_case_variants()
    {
        _comparer.Equals("ABC123", "abc123").Should().BeTrue();
    }

    [Test]
    public void It_gives_ascii_case_variants_the_same_hash_code()
    {
        _comparer.GetHashCode("ABC123").Should().Be(_comparer.GetHashCode("abc123"));
    }

    [Test]
    public void It_does_not_equate_sharp_s_and_ss()
    {
        _comparer.Equals("ß", "ss").Should().BeFalse();
    }

    [Test]
    public void It_does_not_equate_long_s_and_s()
    {
        _comparer.Equals("ſ", "s").Should().BeFalse();
    }

    [Test]
    public void It_does_not_equate_dotless_i_and_i()
    {
        _comparer.Equals("ı", "i").Should().BeFalse();
    }

    [Test]
    public void It_does_not_equate_the_kelvin_sign_and_k()
    {
        _comparer.Equals("K", "k").Should().BeFalse();
    }

    [Test]
    public void It_equates_the_N_grave_case_pair()
    {
        _comparer.Equals("Ǹ", "ǹ").Should().BeTrue();
    }
}

[TestFixture]
public class Given_The_Postgresql_Identity_Equality_Contract
{
    private IdentityEqualityContract _contract = null!;
    private StringComparer _comparer = null!;

    [SetUp]
    public void Setup()
    {
        _contract = IdentityEqualityContract.Postgresql;
        _comparer = _contract.IdentityTextComparer;
    }

    [Test]
    public void It_declares_no_explicit_identity_collation()
    {
        _contract.IdentityTextCollation.Should().BeNull();
    }

    [Test]
    public void It_does_not_equate_ascii_case_variants()
    {
        _comparer.Equals("ABC123", "abc123").Should().BeFalse();
    }

    [Test]
    public void It_equates_identical_values()
    {
        _comparer.Equals("ABC123", "ABC123").Should().BeTrue();
    }

    [Test]
    public void It_does_not_equate_the_N_grave_case_pair()
    {
        _comparer.Equals("Ǹ", "ǹ").Should().BeFalse();
    }
}

[TestFixture]
public class Given_MssqlDialectRules_Identity_Equality
{
    private IdentityEqualityContract _contract = null!;

    [SetUp]
    public void Setup()
    {
        _contract = new MssqlDialectRules().IdentityEquality;
    }

    [Test]
    public void It_exposes_the_SqlServer_contract()
    {
        _contract.Should().BeSameAs(IdentityEqualityContract.SqlServer);
    }
}

[TestFixture]
public class Given_PgsqlDialectRules_Identity_Equality
{
    private IdentityEqualityContract _contract = null!;

    [SetUp]
    public void Setup()
    {
        _contract = new PgsqlDialectRules().IdentityEquality;
    }

    [Test]
    public void It_exposes_the_Postgresql_contract()
    {
        _contract.Should().BeSameAs(IdentityEqualityContract.Postgresql);
    }
}
