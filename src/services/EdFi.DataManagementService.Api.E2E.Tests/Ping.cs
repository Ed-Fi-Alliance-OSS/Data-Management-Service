using Microsoft.Playwright;

namespace EdFi.DataManagementService.Api.E2E.Tests;

public class Ping
{
    [SetUp]
    public void Setup()
    {

    }

    [Test]
    public void Test1()
    {
        var playwright = await Playwright.CreateAsync();
        var requestContext = playwright.APIRequest.NewContextAsync();

        requestContext.GetAsync()


        Assert.Pass();
    }
}
