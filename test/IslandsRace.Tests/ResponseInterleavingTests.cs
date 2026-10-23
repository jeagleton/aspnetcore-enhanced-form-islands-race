namespace IslandsRace.Tests;

// No browser: post the form the way the browser does and check the response itself. The redirection that Blazor
// streams once the response has started (<blazor-ssr><template type="redirection">) must come after the page. On 11.0
// RC2 it sometimes lands in the middle of the page: EndpointHtmlRenderer.OnNavigateTo writes it through a new writer
// straight to Response.Body while the page's own BufferedTextWriter still holds HTML it hasn't sent.
public sealed class ResponseInterleavingTests(AppFixture app) : IClassFixture<AppFixture>
{
    private const string Redirection = "<blazor-ssr><template type=\"redirection\"";

    [Theory]
    [InlineData("/profile", true)]                // the bug: expected to FAIL on 11.0 RC2
    [InlineData("/profile-not-enhanced", false)]  // candidate workaround 1
    [InlineData("/profile-no-streaming", true)]   // candidate workaround 2
    public async Task Redirection_is_never_written_inside_the_page(string path, bool enhanced)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.BaseUrl) };
        var bad = new List<string>();
        int total = 0, streamed = 0;
        foreach (int delay in new[] { 0, 1, 2, 5 })
        for (int rep = 0; rep < 10; rep++)
        {
            total++;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{path}?chars=70000&delay={delay}")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["_handler"] = "display-name",
                    ["Edit.Name"] = $"Run {total}",
                }),
            };
            request.Headers.TryAddWithoutValidation("Accept", enhanced ? "text/html; blazor-enhanced-nav=on" : "text/html");
            using HttpResponseMessage response = await http.SendAsync(request, TestContext.Current.CancellationToken);
            string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            int at = body.IndexOf(Redirection, StringComparison.Ordinal);
            if ((int)response.StatusCode is >= 300 and < 400 && at < 0)
                continue;   // the handler finished before the response started: a plain redirect, nothing to interleave
            streamed++;
            int htmlEnd = body.IndexOf("</html>", StringComparison.Ordinal);
            if (at < 0 || at < htmlEnd)
                bad.Add($"delay={delay}: {(int)response.StatusCode}, {body.Length} chars, redirection at {at}, </html> at {htmlEnd}: …{Around(body, at)}…");
        }
        TestContext.Current.SendDiagnosticMessage($"{path}: {total} posts, {streamed} streamed their redirection, {bad.Count} inside the page");
        Assert.True(bad.Count == 0, $"{path}: {bad.Count}/{total} responses had the redirection inside the page:\n" + string.Join("\n", bad.Take(5)));
    }

    private static string Around(string s, int i) => i < 0 ? "(none)" : s.Substring(Math.Max(0, i - 40), Math.Min(120, s.Length - Math.Max(0, i - 40)));
}
