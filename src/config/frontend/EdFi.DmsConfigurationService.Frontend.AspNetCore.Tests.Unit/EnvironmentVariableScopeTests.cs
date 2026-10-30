// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
public class Given_An_Environment_Variable_Scope
{
    // A name no other test reads, so this fixture can run in parallel with hosts that read the
    // process environment.
    private const string SetName = "CMS_TEST_ENVIRONMENT_VARIABLE_SCOPE_SET";
    private const string UnsetName = "CMS_TEST_ENVIRONMENT_VARIABLE_SCOPE_UNSET";

    private string? _setValueInsideScope;
    private string? _setValueAfterScope;
    private string? _unsetValueAfterScope;

    [SetUp]
    public void Setup()
    {
        Environment.SetEnvironmentVariable(SetName, "from the shell");
        Environment.SetEnvironmentVariable(UnsetName, null);

        using (new EnvironmentVariableScope(SetName, UnsetName))
        {
            _setValueInsideScope = Environment.GetEnvironmentVariable(SetName);
            Environment.SetEnvironmentVariable(SetName, "set by a test");
            Environment.SetEnvironmentVariable(UnsetName, "set by a test");
        }

        _setValueAfterScope = Environment.GetEnvironmentVariable(SetName);
        _unsetValueAfterScope = Environment.GetEnvironmentVariable(UnsetName);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(SetName, null);
        Environment.SetEnvironmentVariable(UnsetName, null);
    }

    [Test]
    public void It_clears_the_variable_inside_the_scope()
    {
        _setValueInsideScope.Should().BeNull();
    }

    [Test]
    public void It_restores_the_value_the_variable_had_before()
    {
        _setValueAfterScope.Should().Be("from the shell");
    }

    [Test]
    public void It_removes_a_variable_that_was_unset_before()
    {
        _unsetValueAfterScope.Should().BeNull();
    }
}
