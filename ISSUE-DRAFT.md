# Draft: dotnet/aspnetcore issue (NOT FILED)

File at https://github.com/dotnet/aspnetcore/issues/new?template=10_bug_report.yml after pushing this folder. Fill in
the one **TODO** (the repro link).

---

**Title:** Streaming SSR: `NavigateTo` after the response has started writes the redirection into the middle of the page (`OnNavigateTo` bypasses the response's buffered writer)

### Is there an existing issue for this?

- [x] I have searched the existing issues

### Describe the bug

On a static SSR page with `[StreamRendering]`, a form handler that awaits something and then calls
`NavigationManager.NavigateTo(...)` (post-redirect-get, non-throwing navigation, the .NET 10+ default) sometimes
produces a corrupt response. The `<blazor-ssr><template type="redirection" …>` block is written **in the middle of
the page**, at exactly character 65,536, instead of after it:

```
… first 65,536 characters of the page …<blazor-ssr><template type="redirection" from="form-post" enhanced="true">_framework/opaque-redirect?url=…</template><blazor-ssr-end></blazor-ssr-end></blazor-ssr>… rest of the page …</html><!--{ssr-framing}-->
```

When the split falls inside an interactive component's marker comment (likely on pages with islands, since those
comments are long), `blazor.web.js` throws and the navigation is lost. The handler has run (the data is saved), but the
user stays on the old page:

```
Error: Found malformed component comment at Blazor:{"type":"server","prerenderId":"…","key":{…},"sequence":0,"descriptor":"CfDJ8…<blazor-ssr><template type="redirection" from="form-post" enhanced="true">_framework/opaque-redirect?url=…</template><blazor-ssr-end></blazor-ssr-end></blazor-ssr>…"}
```

It happens with enhanced and plain forms alike. We found it in a production-shaped app on a Release build, where the
first form submit after sign-in lost its redirect about once per test run.

**Cause** (`v11.0.0-rc.2.26504.105`, same on `main`): `EndpointHtmlRenderer.OnNavigateTo`
(`src/Components/Endpoints/src/Rendering/EndpointHtmlRenderer.EventDispatch.cs`), when `Response.HasStarted`, creates
a new `HttpResponseStreamWriter` on `Response.Body` and writes the redirection through it. The page is written by
`RazorComponentEndpointInvoker` through its own `BufferedTextWriter` (`htmlContent.WriteTo(bufferWriter)`, then
`SendStreamingUpdatesAsync(…, bufferWriter)`), which yields at `await writer.FlushAsync()`. While that flush is in
progress, part of the page is on the wire and the rest is still buffered. The handler's continuation runs on the now
free sync context, calls `NavigateTo`, and the redirection goes out between the two parts.

With `BlazorDisableThrowNavigationException=false` it never happens: the `NavigationException` is caught in
`SendStreamingUpdatesAsync` after its flush, and the redirection is written through the page's own writer.

`SetNotFoundWhenResponseHasStarted` uses the same side-writer pattern, so streamed NotFound is probably affected too.

### Expected Behavior

The redirection is written after the page (and after the SSR framing marker), through the same writer, as the
throwing path does it, so the navigation completes.

### Steps To Reproduce

Code and tests to repro: **https://github.com/jeagleton/aspnetcore-enhanced-form-islands-race**

`./run.ps1` publishes the app (Release) and runs two checks on three pages:

- without a browser: 40 POSTs per page; the redirection must not appear before `</html>`;
- with Playwright: 20 submits per page; each must reach `?saved=1`.

| Page | Server: bad of 40 | Browser: lost of 20 |
|---|---|---|
| `/profile`: enhanced form, `[StreamRendering]` | 3–5 | 7–10 (`Found malformed component comment`) |
| `/profile-not-enhanced`: plain form, `[StreamRendering]` | 0–4 | 1–2 |
| `/profile-no-streaming`: enhanced form, no streaming | 0 | 0 |

`./run.ps1 -ThrowingNavigation` (`BlazorDisableThrowNavigationException=false`): 0 everywhere.

By hand: run the app, then POST to `/profile?chars=70000&delay=1` with `_handler=display-name&Edit.Name=x` and
`Accept: text/html; blazor-enhanced-nav=on` a few times. Some 200 responses have `<blazor-ssr><template
type="redirection"` at offset 65536.

The knobs: `chars` makes the page bigger than the first 64 KB chunk; `delay=1` makes the handler finish while that
chunk is being flushed.

### Exceptions (if any)

`Error: Found malformed component comment at Blazor:{…}` from `blazor.web.js`, raised while applying the response.

### .NET Version

11.0.100-rc.2.26504.105

### Anything else?

- Chromium 1243 (Playwright 1.63), Windows 11 Pro 26300, Kestrel over HTTP
- Workarounds: `BlazorDisableThrowNavigationException=false`, or no `[StreamRendering]` on pages whose forms
  navigate. Removing `Enhance` from the form does not help.
- Possibly related: #48123 (same client error on long pages, .NET 8 era, closed).
