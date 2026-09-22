// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

/// <summary>
/// Reflects over the CMS provider surface named in story DMS-1515 design decision D5 and asserts every
/// listed method's last parameter is a defaulted <see cref="CancellationToken"/>, so a caller's
/// cancellation can be threaded all the way to the Configuration Service.
/// </summary>
[TestFixture]
public class Given_The_Cms_Provider_Cancellation_Surface
{
    private static IEnumerable<TestCaseData> CancellationSurfaceMethods()
    {
        yield return new TestCaseData(typeof(IDataStoreProvider), nameof(IDataStoreProvider.LoadTenants));
        yield return new TestCaseData(
            typeof(IApplicationContextProvider),
            nameof(IApplicationContextProvider.GetApplicationByClientIdAsync)
        );
        yield return new TestCaseData(
            typeof(IApplicationContextProvider),
            nameof(IApplicationContextProvider.ReloadApplicationByClientIdAsync)
        );
        yield return new TestCaseData(
            typeof(IConfigurationServiceApplicationProvider),
            nameof(IConfigurationServiceApplicationProvider.GetApplicationByClientIdAsync)
        );
        yield return new TestCaseData(
            typeof(IConfigurationServiceApplicationProvider),
            nameof(IConfigurationServiceApplicationProvider.ReloadApplicationByClientIdAsync)
        );
        yield return new TestCaseData(typeof(IClaimSetProvider), nameof(IClaimSetProvider.GetAllClaimSets));
        yield return new TestCaseData(
            typeof(IConfigurationServiceClaimSetProvider),
            nameof(IConfigurationServiceClaimSetProvider.GetAllClaimSets)
        );
        yield return new TestCaseData(
            typeof(CachedClaimSetProvider),
            nameof(CachedClaimSetProvider.InvalidateCacheAsync)
        );
    }

    [TestCaseSource(nameof(CancellationSurfaceMethods))]
    public void It_Declares_A_Trailing_Defaulted_CancellationToken_Parameter(
        Type declaringType,
        string methodName
    )
    {
        MethodInfo method = declaringType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)!;

        method.Should().NotBeNull($"{declaringType.Name}.{methodName} should exist");

        ParameterInfo[] parameters = method.GetParameters();
        parameters
            .Should()
            .NotBeEmpty($"{declaringType.Name}.{methodName} should accept a CancellationToken");

        ParameterInfo lastParameter = parameters[^1];
        lastParameter.ParameterType.Should().Be(typeof(CancellationToken));
        // Reflection reports no constant DefaultValue for a struct optional parameter without a
        // primitive representation (CancellationToken is such a struct), so IsOptional is the
        // reliable signal that the parameter is defaulted rather than required.
        lastParameter.IsOptional.Should().BeTrue();
    }
}
