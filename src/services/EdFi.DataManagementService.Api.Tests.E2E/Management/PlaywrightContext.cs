using Microsoft.Playwright;

namespace EdFi.DataManagementService.Api.Tests.E2E.Management;

public class PlaywrightContext
{
    private static Task<IAPIRequestContext>? _requestContext;

    public string ApiUrl { get; set; } = "http://localhost:5198";

    public IAPIRequestContext? ApiRequestContext => _requestContext?.GetAwaiter().GetResult();

    public void Dispose()
    {
        _requestContext?.Dispose();
    }

    public async Task CreateApiContext()
    {
        var playwright = await Playwright.CreateAsync();

        _requestContext = playwright.APIRequest.NewContextAsync(
            new APIRequestNewContextOptions { BaseURL = ApiUrl, IgnoreHTTPSErrors = true }
        );
    }
}
