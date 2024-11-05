// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using PactNet.Verifier;
using PactNet;
using Microsoft.Extensions.Logging;
using PactNet.Infrastructure.Outputters;
using Microsoft.Extensions.Configuration;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.ContractTest.Provider.Tests
{
    [TestFixture]
    public class ProviderIdentityTest : IDisposable
    {
        private static readonly JsonSerializerOptions _options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private IHost _host;
        private IPactVerifier verifier;

        private readonly Uri? pactURL;
        private readonly string? pactFilePath;
        private readonly string? providerStatePath;

        public ProviderIdentityTest()
        {
            // Load configuration from appsettings.json
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())  // Set the base path for your appsettings.json
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            // Get values from the configuration file
            pactURL = new Uri(configuration["Pact:PactURL"]!);
            pactFilePath = configuration["Pact:PactFilePath"];
            providerStatePath = configuration["Pact:ProviderStatePath"];

            _host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseEnvironment("Test");
                    webBuilder.UseUrls(pactURL.ToString()); // Explicitly set the URL and port
                    webBuilder.UseStartup<TestStartup>(); // Use TestStartup class for test configuration
                                                          // Configure logging.
                    webBuilder.ConfigureLogging(logging =>
                    {
                        logging.ClearProviders();
                        logging.AddConsole();
                        logging.SetMinimumLevel(LogLevel.Debug);
                    });
                })
                .Build();

            _host.Start(); // Start the host explicitly
            // Allow some time for the server to start before running the verification
            Thread.Sleep(2000);

            var config = new PactVerifierConfig
            {
                Outputters = new List<IOutput>
                {
                    new ConsoleOutput() // Sends log output to the console
                },
                LogLevel = PactLogLevel.Debug
            };

            verifier = new PactVerifier(config);

        }

        [Test]
        public void Verify()
        {
            verifier!.ServiceProvider("DMS Configuration Service API", pactURL)
                .WithFileSource(new FileInfo(pactFilePath!))
                .WithProviderStateUrl(new Uri(pactURL + providerStatePath))
                .Verify();
        }

        #region IDisposable Support

        private bool _disposed = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _host.Dispose();
            }

            _disposed = true;
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
