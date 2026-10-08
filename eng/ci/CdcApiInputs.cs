#!/usr/bin/env dotnet
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

#:property PublishAot=false
#:project ../../src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj

using System.Security.Cryptography;
using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.SchemaTools.Cdc;
using Microsoft.Extensions.DependencyInjection;

// Private runner input resolution. Reuses the production loader/state validation and the
// same complete-binding serialization as the fixture; no controller or executor starts.
using var handoff = JsonDocument.Parse(File.ReadAllText(args[0]));
var root = handoff.RootElement;
if (root.GetProperty("version").GetInt32() != 1)
{
    throw new InvalidOperationException("CDC_API_HANDOFF_INVALID");
}
var config = CdcCommandConfiguration.Load(root.GetProperty("settingsPath").GetString()!);
using var settings = (IDisposable)config.Settings;
config.ValidateControllerSettings();
var services = new ServiceCollection();
services.AddDmsCdcControlPlane();
services.Configure<CdcBindingStateStoreOptions>(options =>
    options.RootPath = root.GetProperty("statePath").GetString()!
);
await using var provider = services.BuildServiceProvider();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var retained = await provider
    .GetRequiredService<ICdcBindingLifecycleService>()
    .ReadBindingAsync(CdcBindingIdentity.FromTargetIdentity(config.Target), timeout.Token);
if (
    retained.Status != CdcControlPlaneOperationStatus.Succeeded
    || retained.State?.Binding is not { } binding
    || binding.ToTargetIdentity() != config.Target
)
{
    throw new InvalidOperationException("CDC_API_BINDING_INVALID");
}
await using var connection = config.CreateConnection();
var inputs = new
{
    Provider = config.GetProvider() == CdcProvider.Postgresql ? "Postgresql" : "Mssql",
    BindingId = Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(binding.ToCompleteBindingIdentity()))
    ),
    binding.Generation,
    Database = connection.Database,
};
File.WriteAllBytes(args[1], JsonSerializer.SerializeToUtf8Bytes(inputs));
