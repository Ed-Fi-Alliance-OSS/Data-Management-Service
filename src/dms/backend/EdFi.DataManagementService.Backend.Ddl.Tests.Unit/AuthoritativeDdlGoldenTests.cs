// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
[Category("Authoritative")]
public class Given_AuthoritativeDdl_With_Ds52Core : DdlGoldenFixtureTestBase
{
    protected override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(projectRoot, "..", "Fixtures", "authoritative", "ds-5.2");
}

[TestFixture]
[Category("Authoritative")]
public class Given_AuthoritativeDdl_With_Ds52Core_And_SampleExtension : DdlGoldenFixtureTestBase
{
    protected override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(projectRoot, "..", "Fixtures", "authoritative", "sample");
}
