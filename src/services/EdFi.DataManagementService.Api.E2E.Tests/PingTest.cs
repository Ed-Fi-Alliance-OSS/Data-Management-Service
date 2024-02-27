using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using FluentAssertions;
using System.Net;

namespace EdFi.DataManagementService.Api.E2E.Tests;

[TestFixture]
public class Ping : PageTest
{
    private PlaywrightContext _PlaywrightContext;

    [OneTimeSetUp]
    public void Init()
    {
        _PlaywrightContext = new PlaywrightContext();
    }

    [Test]
    public async Task Test()
    {
        var response = await _PlaywrightContext.ApiRequestContext?.GetAsync("ping")!;

        string content = await response.TextAsync();
        string expectedDate = DateTime.Now.ToString("yyyy-MM-dd");

        response.Status.Should().Be((int)HttpStatusCode.OK);
        content.Should().Contain(expectedDate);
    }

}
