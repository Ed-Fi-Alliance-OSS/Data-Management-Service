// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Identity;

/// <summary>
/// Reflection-based tests pinning IApiService's five identity facade methods (design.md D9, A1). Each
/// method's last parameter must be a required CancellationToken - no default - unlike every other
/// IApiService entry point added before this story, because an identity request can hold a client-to-
/// tenant binding and a claim-set lookup open, and a caller that forgets to thread its own token would
/// silently opt into an uncancellable identity request.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_The_IApiService_Identity_Facade
{
    private static readonly string[] _expectedMethodNames =
    [
        nameof(IApiService.IdentityCreate),
        nameof(IApiService.IdentityGetById),
        nameof(IApiService.IdentityFind),
        nameof(IApiService.IdentitySearch),
        nameof(IApiService.IdentityResults),
    ];

    private static MethodInfo MethodNamed(string name) =>
        typeof(IApiService).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!;

    [Test]
    public void It_declares_all_five_identity_methods()
    {
        foreach (string methodName in _expectedMethodNames)
        {
            typeof(IApiService)
                .GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
                .Should()
                .NotBeNull($"IApiService should declare {methodName}");
        }
    }

    [TestCaseSource(nameof(_expectedMethodNames))]
    public void It_returns_a_Task_of_IFrontendResponse(string methodName)
    {
        MethodNamed(methodName).ReturnType.Should().Be(typeof(Task<IFrontendResponse>));
    }

    [TestCaseSource(nameof(_expectedMethodNames))]
    public void It_takes_a_FrontendRequest_as_its_first_parameter(string methodName)
    {
        ParameterInfo[] parameters = MethodNamed(methodName).GetParameters();

        parameters.Should().HaveCountGreaterThanOrEqualTo(2);
        parameters[0].ParameterType.Should().Be(typeof(FrontendRequest));
    }

    /// <summary>
    /// The load-bearing assertion (A1): the trailing CancellationToken carries no default value, so a
    /// caller must supply one explicitly. Every other IApiService entry point in this file defaults it
    /// to CancellationToken.None instead - the identity facade deliberately breaks that convention.
    /// </summary>
    [TestCaseSource(nameof(_expectedMethodNames))]
    public void It_requires_a_CancellationToken_as_its_last_parameter_with_no_default(string methodName)
    {
        ParameterInfo lastParameter = MethodNamed(methodName).GetParameters()[^1];

        lastParameter.ParameterType.Should().Be(typeof(CancellationToken));
        lastParameter.HasDefaultValue.Should().BeFalse();
    }

    [Test]
    public void It_IdentityGetById_takes_a_uniqueId_string_between_the_request_and_the_token()
    {
        ParameterInfo[] parameters = MethodNamed(nameof(IApiService.IdentityGetById)).GetParameters();

        parameters.Should().HaveCount(3);
        parameters[1].ParameterType.Should().Be(typeof(string));
        parameters[1].Name.Should().Be("uniqueId");
    }

    [Test]
    public void It_IdentityResults_takes_a_requestToken_string_between_the_request_and_the_token()
    {
        ParameterInfo[] parameters = MethodNamed(nameof(IApiService.IdentityResults)).GetParameters();

        parameters.Should().HaveCount(3);
        parameters[1].ParameterType.Should().Be(typeof(string));
        parameters[1].Name.Should().Be("requestToken");
    }

    [TestCase(nameof(IApiService.IdentityCreate))]
    [TestCase(nameof(IApiService.IdentityFind))]
    [TestCase(nameof(IApiService.IdentitySearch))]
    public void It_create_find_and_search_take_no_route_value_between_the_request_and_the_token(
        string methodName
    )
    {
        MethodNamed(methodName).GetParameters().Should().HaveCount(2);
    }

    [Test]
    public void It_declares_exactly_five_identity_methods_named_Identity_star()
    {
        typeof(IApiService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name.StartsWith("Identity", StringComparison.Ordinal))
            .Select(method => method.Name)
            .Should()
            .BeEquivalentTo(_expectedMethodNames);
    }
}
