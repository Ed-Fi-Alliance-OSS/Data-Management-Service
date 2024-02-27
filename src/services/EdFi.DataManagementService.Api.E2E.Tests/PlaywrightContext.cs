using Microsoft.Playwright;

namespace EdFi.DataManagementService.Api.E2E.Tests;

public class PlaywrightContext
{
    private readonly Task<IAPIRequestContext?>? _requestContext;

    public PlaywrightContext()
    {
        _requestContext = CreateApiContext();
    }

    public IAPIRequestContext? ApiRequestContext => _requestContext?.GetAwaiter().GetResult();

    public void Dispose()
    {
        _requestContext?.Dispose();
    }


    private async Task<IAPIRequestContext?> CreateApiContext()
    {
        var playwright = await Playwright.CreateAsync();

        return await playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            BaseURL = "http://localhost:5198/",
            IgnoreHTTPSErrors = true
        });
    }
}
