using Microsoft.Playwright;

namespace IslandsRace.Tests;

// The browser's view of the same bug. The page weight is calibrated so the 64 KB split falls inside the Interactive
// Server island's <!--Blazor:{...}--> comment, as it did in the app where this was found. When the redirection lands
// there, blazor.web.js throws "Found malformed component comment" while applying the response, and never navigates.
public sealed class EnhancedFormIslandsRaceTests(AppFixture app) : IClassFixture<AppFixture>
{
    private const int Attempts = 20;
    private const int FirstChunk = 65536;   // what Blazor has sent when the handler's redirection cuts in

    [Fact]
    public Task Enhanced_form_redirect_completes()   // the bug: expected to FAIL on 11.0 RC2
        => SubmitRepeatedly("/profile", enhanced: true);

    [Fact]
    public Task Not_enhanced_form_redirect_completes()   // not a workaround: also expected to FAIL
        => SubmitRepeatedly("/profile-not-enhanced", enhanced: false);

    [Fact]
    public Task Page_without_streaming_redirect_completes()   // the workaround: expected to pass
        => SubmitRepeatedly("/profile-no-streaming", enhanced: true);

    private async Task SubmitRepeatedly(string path, bool enhanced)
    {
        int chars = await CalibrateAsync(path, enhanced);
        var failures = new List<string>();
        for (int i = 1; i <= Attempts; i++)
        {
            await using IBrowserContext context = await app.Browser.NewContextAsync(new() { BaseURL = app.BaseUrl });
            IPage page = await context.NewPageAsync();
            var errors = new List<string>();
            page.PageError += (_, e) => errors.Add(e.Split('\n')[0][..Math.Min(140, e.Split('\n')[0].Length)]);

            await page.GotoAsync($"{path}?chars={chars}&delay=1");
            await page.GetByTestId("name").FillAsync($"Run {i}");
            await page.GetByTestId("save").ClickAsync();
            try
            {
                await page.WaitForURLAsync("**/*saved=1*", new() { Timeout = 10_000 });
                await page.GetByTestId("saved").WaitForAsync(new() { Timeout = 10_000 });
            }
            catch (TimeoutException)
            {
                failures.Add($"attempt {i}: redirect lost, still at {page.Url}; {string.Join(" | ", errors)}");
            }
        }
        Assert.True(failures.Count == 0, $"{path} (chars={chars}): {failures.Count}/{Attempts} submits lost their redirect:\n" + string.Join("\n", failures));
    }

    // Size the filler so the first 64 KB of the POST response ends ~200 characters into the Server island's start comment.
    private async Task<int> CalibrateAsync(string path, bool enhanced)
    {
        const int probe = 40_000;
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.BaseUrl) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{path}?chars={probe}&delay=100")   // slow save: never interleaved
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_handler"] = "display-name", ["Edit.Name"] = "Run 1" }),
        };
        request.Headers.TryAddWithoutValidation("Accept", enhanced ? "text/html; blazor-enhanced-nav=on" : "text/html");
        using HttpResponseMessage response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        int island = body.IndexOf("<!--Blazor:{\"type\":\"server\"", StringComparison.Ordinal);
        return island < 0 ? 70_000 : probe + (FirstChunk - island) - 200;
    }
}
