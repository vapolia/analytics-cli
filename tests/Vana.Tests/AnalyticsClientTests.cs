using System.Net;
using System.Text;
using Vana.Client;

namespace Vana.Tests;

/// <summary>The user's token goes to the portal only; the read server only ever sees the short read token.</summary>
[TestClass]
public class AnalyticsClientTests
{
    sealed class Recorder : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Request, string Body)> Calls = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Calls.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancel)));
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/api/token-exchange" => """{"access_token":"read-token","expires_in":300,"read_url":"https://read.test/base","source":"kavale"}""",
                "/api/projects" => """[{"id":"kav","name":"Kavale","company":null,"role":"reader"}]""",
                var p when p.StartsWith("/base/views/nope") => null,
                _ => """{"rows":[]}""",
            };
            return json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"Unknown view."}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    readonly Recorder recorder = new();
    AnalyticsClient Client() => new(new HttpClient(recorder), new Uri("https://portal.test/"));

    [TestMethod]
    public async Task ReadsAViewWithTheExchangedTokenOnly()
    {
        await Client().GetView("user-token", "kav", "weekly_active", limit: 4, filters: new Dictionary<string, string[]> { ["platform"] = ["ios", "web"] });

        var (exchange, form) = recorder.Calls[0];
        Assert.AreEqual("https://portal.test/api/token-exchange", exchange.RequestUri!.ToString());
        StringAssert.Contains(form, "subject_token=user-token");
        StringAssert.Contains(form, "audience=kav");
        StringAssert.Contains(form, "view=weekly_active");

        var (read, _) = recorder.Calls[1];
        Assert.AreEqual("https://read.test/base/views/weekly_active?limit=4&platform=ios&platform=web", read.RequestUri!.ToString());
        Assert.AreEqual("read-token", read.Headers.Authorization!.Parameter);
    }

    [TestMethod]
    public async Task ListsProjectsWithTheUserToken()
    {
        var projects = await Client().Projects("user-token");
        Assert.AreEqual("kav", projects.Single().Id);
        Assert.AreEqual("user-token", recorder.Calls[0].Request.Headers.Authorization!.Parameter);
    }

    [TestMethod]
    public async Task ReportsTheReadServerReason()
    {
        var e = await Assert.ThrowsExactlyAsync<AnalyticsException>(() => Client().GetView("user-token", "kav", "nope"));
        Assert.AreEqual(404, e.Status);
        Assert.AreEqual("Unknown view.", e.Message);
    }
}
