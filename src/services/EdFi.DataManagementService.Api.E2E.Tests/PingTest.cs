using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using FluentAssertions;
using System.Net;

namespace EdFi.DataManagementService.Api.E2E.Tests;

[TestFixture]
public class PingTest : PageTest
{

    [TestFixture]
    public class Given_a_ping_to_the_server : PingTest
    {

        private PlaywrightContext _PlaywrightContext;

        [SetUp]
        public void SetUp()
        {
            _PlaywrightContext = new PlaywrightContext();
        }

        [Test]
        public async Task It_returns_the_dateTime()
        {
            var response = await _PlaywrightContext.ApiRequestContext?.GetAsync("ping")!;

            string content = await response.TextAsync();
            string expectedDate = DateTime.Now.ToString("yyyy-MM-dd");

            response.Status.Should().Be((int)HttpStatusCode.OK);
            content.Should().Contain(expectedDate);
        }

    }

}
