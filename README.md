# Blazor (.NET 11 RC2): a redirect from a form handler is written into the middle of a streaming page

A minimal repro and draft upstream ticket for a bug found in ParkManV3Core's GenericConsumer
(`upstream-bugs/2026-10-21-aspnetcore-enhanced-form-islands-race`). The ticket is in [ISSUE-DRAFT.md](ISSUE-DRAFT.md),
ready to file once this folder is pushed and the link is filled in.

**Status (2026-10-23): reproduced, root cause found in the framework source, workaround verified.**

## What happens

A static SSR page with `[StreamRendering]` has a form whose handler awaits something (a save) and then calls
`NavigationManager.NavigateTo(...)` (post-redirect-get). If the page is bigger than the first 64 KB that Blazor sends,
and the handler finishes while that first chunk is being flushed, the response comes out like this:

```
<!DOCTYPE html> … 65,536 characters of the page …<blazor-ssr><template type="redirection" from="form-post" enhanced="true">_framework/opaque-redirect?url=…</template><blazor-ssr-end></blazor-ssr-end></blazor-ssr>… the rest of the page … </html><!--ssr-framing marker-->
```

The redirection is spliced into the page at exactly character 65,536, before `</html>` and before the framing marker.
What the browser does next depends on where the split lands. When it lands inside an interactive island's
`<!--Blazor:{…}-->` comment (likely, because those comments are long), `blazor.web.js` throws while applying the
response and the redirect never happens:

```
Error: Found malformed component comment at Blazor:{"type":"server","prerenderId":"…","key":{…},"sequence":0,"descriptor":"CfDJ8…<blazor-ssr><template type="redirection" from="form-post" enhanced="true">_framework/opaque-redirect?url=…</template><blazor-ssr-end></blazor-ssr-end></blazor-ssr>…"}
```

That's what GenericConsumer's `/profile` did. The save succeeds (the handler ran), the guest stays on the old page, and
there's no request for `?saved=1`. The first sighting's `TypeError: … reading 'nodeType'` is presumably the same
corruption landing somewhere else in the markup; it wasn't captured again.

### What it is not

The original theory, "the islands are still attaching", was wrong. The islands don't cause it: the response is
already corrupt when it leaves the server. Waiting for the islands before submitting (the test-side change in
ParkManV3Core) only shifted the timing. A guest who submits at the wrong moment still hits it.

## Root cause (v11.0.0-rc.2.26504.105, unchanged on `main` as of 2026-10-23)

`src/Components/Endpoints/src/Rendering/EndpointHtmlRenderer.EventDispatch.cs`, `OnNavigateTo` (line 127):

```csharp
if (_httpContext.Response.HasStarted)
{
    var defaultBufferSize = 16 * 1024;
    await using var writer = new HttpResponseStreamWriter(_httpContext.Response.Body, Encoding.UTF8, defaultBufferSize, …);
    using var bufferWriter = new BufferedTextWriter(writer);
    HandleNavigationAfterResponseStarted(bufferWriter, _httpContext, uri);
    await bufferWriter.FlushAsync();
}
```

It writes the redirection through a **new writer straight onto `Response.Body`**. The page itself is written by
`RazorComponentEndpointInvoker` through a different `BufferedTextWriter`:
`htmlContent.WriteTo(bufferWriter)` (line 161), then `SendStreamingUpdatesAsync(…, bufferWriter)` (line 165), which
yields at `await writer.FlushAsync()` (`EndpointHtmlRenderer.Streaming.cs` line 75). During that flush the first 64 KB
are on the wire (`Response.HasStarted` is true) and the rest of the page is still in the buffer. The renderer's sync
context is free, so the handler's continuation runs, calls `NavigateTo`, and `OnNavigateTo` writes the redirection
between the two halves.

The throwing navigation path doesn't have this bug. With `BlazorDisableThrowNavigationException=false`, `NavigateTo`
throws `NavigationException`, `SendStreamingUpdatesAsync` catches it after its flush, and it writes the redirection
through the page's own writer (`HandleNavigationAfterResponseStarted(writer, …)`). The tests below pass that way.

`SetNotFoundWhenResponseHasStarted` (line 92) writes `NotFound` with the same pattern, so a streamed `NotFound` can
presumably be interleaved the same way (not tested).

## The repro

`src/IslandsRace` is a .NET 11 RC2 Blazor Web App (`dotnet new blazor -int Auto --empty`) with one page body
(`Components/ProfileForm.razor`: a form whose handler awaits `?delay=` ms and then calls `NavigateTo`, `?chars=` of
filler, an Interactive Server island and an Interactive WebAssembly island) on three pages:

| Page | Form | Page |
|---|---|---|
| `/profile` | `Enhance` | `[StreamRendering]` (the bug) |
| `/profile-not-enhanced` | plain | `[StreamRendering]` |
| `/profile-no-streaming` | `Enhance` | not streaming |

`test/IslandsRace.Tests` (xUnit v3, Microsoft.Testing.Platform, Playwright/Chromium) runs the Release publish:

- `ResponseInterleavingTests`, **no browser:** 40 POSTs per page (save delay 0/1/2/5 ms, 70,000 chars of filler).
  Each must either be a plain 302 or have the redirection after `</html>`.
- `EnhancedFormIslandsRaceTests`, **browser:** 20 submits per page, with the filler sized so character 65,536 falls
  inside the Server island's comment. Each must reach `?saved=1`.

```powershell
./run.ps1                       # as shipped
./run.ps1 -ThrowingNavigation   # with the workaround
```

### Results (SDK 11.0.100-rc.2.26504.105, Chromium 1243, Windows 11 Pro 26300)

| Page | As shipped: server (bad/40) | As shipped: browser (lost/20) | `-ThrowingNavigation`: server | `-ThrowingNavigation`: browser |
|---|---|---|---|---|
| `/profile` | **3–5** | **7–10**, all `Found malformed component comment` | 0 | 0 |
| `/profile-not-enhanced` | **0–4** | **1–2** | 0 | 0 |
| `/profile-no-streaming` | 0 | 0 | 0 | 0 |

Ranges are over the last three runs; it's a race, but `/profile` has failed in every run.

### In GenericConsumer itself

A temporary test in `TestGenericConsumerPlaywright` (since removed) submitted the static form 15 times on Release
without waiting for the islands. Run alone it passed 15/15. Inside the full suite, attempt 1 lost its redirect with
the `Found malformed component comment` error above, the redirection inside the Server island's descriptor, and the
requests `POST /profile → 200` and nothing after. That's why the original failures only showed inside the class: the
timing has to line up.

## Our workaround

**Recommended: make `NavigateTo` throw again during static rendering.** In
`src/GenericConsumer/GenericConsumer/GenericConsumer.csproj`:

```xml
<PropertyGroup>
  <!-- NavigateTo throws during static rendering (the pre-.NET 10 behavior). The non-throwing path writes a redirect
       that happens after a streaming response has started straight onto the response body, into the middle of the
       page (11 RC2; upstream-bugs/2026-10-21-aspnetcore-enhanced-form-islands-race). Remove when the fix ships. -->
  <BlazorDisableThrowNavigationException>false</BlazorDisableThrowNavigationException>
</PropertyGroup>
```

This keeps the enhanced form and the streaming page. GenericConsumer's three `NavigateTo` calls (`Profile.razor`
`SaveAsync`, `RedirectToSignIn`, `DebugSignIn`) are each the last thing their method does, with no `try/catch`
around them, so the exception changes nothing else. The comment in `Profile.razor` ("no NavigationException in .NET
10+") would need updating. Interactive (circuit and WebAssembly) navigation never throws either way.

**Alternative: take `[StreamRendering]` off `Profile.razor`.** A POST then waits for the handler before sending
anything, so the redirect is a plain 302 (`/profile-no-streaming`: 0 failures). The cost is that the GET waits for the
identity before the first byte, so the spinner never shows.

**Not a workaround:** dropping `Enhance` from the form. A plain POST hits the same interleaving (`/profile-not-enhanced`
failed in the browser and on the server). Neither is waiting for the islands in the tests; that only hides it.

## Existing reports (searched 2026-10-23)

No duplicate. #48123 ("Found malformed component comment", closed 2023, on pages "with a lengthier HTML section") has
the same client symptom but a different cause. #68516, #68529 and #64722 are logical-tree bugs in DomSync, which this
isn't.
