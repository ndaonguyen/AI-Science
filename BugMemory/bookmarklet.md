# Slack-to-BugMemory bookmarklet

A browser bookmark that scrapes the open Slack thread pane and drops you
into the BugMemory Add tab with the fields pre-filled by the AI extract.

## How to install

1. Make sure BugMemory is running locally at `http://localhost:5080`.
2. Create a new browser bookmark. Name it whatever you like (e.g.
   "→ Bug Memory").
3. For the URL, paste the entire one-liner from
   [`bookmarklet.min.txt`](./bookmarklet.min.txt) — it starts with
   `javascript:`.
4. Save.

## How to use

1. Open the Slack web app at `https://app.slack.com`. (The desktop app
   doesn't support bookmarks. Browser only.)
2. Open a thread that documents a bug — click any reply count to open
   the thread pane.
3. Click your bookmark.
4. The bookmarklet sends the thread text to BugMemory's `/api/extract`
   endpoint, which uses the LLM to parse out title / tags / context /
   root cause / solution.
5. A new tab opens at `http://localhost:5080/?prefill=...` with the
   Add tab pre-populated. Review the fields, edit anything that's
   wrong, and click **Save bug**.

The flow keeps the human-in-the-loop review step deliberately. The
extract is good but not perfect — verifying before save matters for a
knowledge base you'll trust later.

## How it works (under the hood)

```
┌─────────────────┐  click  ┌─────────────────┐ POST    ┌───────────────┐
│ Slack tab       │────────▶│ bookmarklet JS  │────────▶│ /api/extract  │
│ (DOM with       │         │ scrapes DOM,    │         │ (LLM parses   │
│  thread pane)   │         │ builds text     │         │  fields)      │
└─────────────────┘         └─────────────────┘         └───────┬───────┘
                                                                │
                            ┌────────────────────────────┐      │
                            │ /?prefill=<base64-json>    │◀─────┘
                            │ (Add tab, review & save)   │ open in new tab
                            └────────────────────────────┘
```

## Source (readable)

This is the un-minified version. The actual bookmarklet (URL form) is
in `bookmarklet.min.txt`.

```javascript
(function () {
  // Find the most likely scope: the open thread pane, or the
  // channel scroll if nothing else. Selectors prefer data-qa attrs
  // (used by Slack's QA tests, change less often than class names).
  const scope =
    document.querySelector('[data-qa="threads_view"]') ||
    document.querySelector('.p-threads_view') ||
    document.querySelector('[data-qa="virtual-list-flex"]') ||
    document.querySelector('[data-qa="message_pane"]') ||
    document.body;

  // Collect message containers within the scope. Multiple selectors
  // for resilience — Slack ships UI updates that rename classes.
  const messages = scope.querySelectorAll(
    '[data-qa="message_container"], .c-virtual_list__item'
  );

  if (!messages.length) {
    alert(
      'Bug Memory bookmarklet: no Slack messages found.\n\n' +
      "Open a thread first (click 'X replies' on a message), then click the bookmark again."
    );
    return;
  }

  // Per-message: try to grab sender + text. If sender or text isn't
  // findable, fall back to the message's full text content rather
  // than skipping it entirely — partial info is better than missing it.
  const lines = [];
  messages.forEach(function (m) {
    const senderEl =
      m.querySelector('[data-qa="message_sender_name"]') ||
      m.querySelector('.c-message__sender') ||
      m.querySelector('.c-message_kit__sender');
    const textEl =
      m.querySelector('[data-qa="message-text"]') ||
      m.querySelector('.c-message_kit__text') ||
      m.querySelector('.p-rich_text_block') ||
      m.querySelector('.c-message__body');

    const sender = senderEl ? senderEl.textContent.trim() : '(unknown)';
    const text = (textEl ? textEl.textContent : m.textContent).trim();

    if (text) lines.push(sender + ': ' + text);
  });

  const sourceText = lines.join('\n\n');

  if (sourceText.length < 30) {
    alert(
      'Bug Memory bookmarklet: extracted text was too short (' +
      sourceText.length + ' chars). ' +
      'Make sure the thread pane is open and visible.'
    );
    return;
  }

  console.log('[BugMemory] extracting', lines.length, 'messages,',
              sourceText.length, 'chars');

  // POST to /api/extract on localhost:5080. Cross-origin from
  // app.slack.com — the API has a narrow CORS allow for this origin.
  fetch('http://localhost:5080/api/extract', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sourceText: sourceText }),
  })
    .then(function (r) {
      if (!r.ok) return r.text().then(function (t) {
        throw new Error(r.status + ': ' + t);
      });
      return r.json();
    })
    .then(function (extracted) {
      // Base64-encode the extracted JSON, navigate to /?prefill=...
      // unescape(encodeURIComponent(...)) handles UTF-8 (emoji,
      // accented chars in Slack content) before btoa.
      const json = JSON.stringify(extracted);
      const b64 = btoa(unescape(encodeURIComponent(json)));
      window.open(
        'http://localhost:5080/?prefill=' + encodeURIComponent(b64),
        '_blank'
      );
    })
    .catch(function (err) {
      alert('Bug Memory bookmarklet failed:\n\n' + (err.message || err) +
            '\n\nIs http://localhost:5080 running?');
    });
})();
```

## Failure modes — read this before reporting an issue

The bookmarklet has three classes of failure, in roughly increasing
difficulty to fix:

**1. "Is http://localhost:5080 running?" alert.**
The fetch failed before getting an HTTP response. Likely causes:
- BugMemory backend isn't running. `dotnet run --project src/BugMemory.Api`.
- Browser blocked mixed content (HTTPS Slack page → HTTP localhost).
  Chrome usually allows this; Firefox sometimes blocks it. If Firefox
  is blocking, click the shield icon in the address bar and disable
  protections for `app.slack.com`.

**2. "Extracted text was too short" alert.**
The DOM selectors found things, but the text content was less than
30 characters. Usually means you clicked the bookmarklet before
opening a thread. Click `N replies` on a message first.

**3. "No Slack messages found" alert.**
None of the selectors matched. This means Slack changed their DOM and
the bookmarklet is out of date. Open DevTools, inspect a Slack message,
and look at what data-qa or class attributes are on the container and
the text. Update the selectors in the source above and re-minify.

## Updating the bookmarklet

When Slack changes their DOM and the bookmarklet stops working,
update the selector arrays in the readable source, then re-minify.
There's no automated build — paste the readable source into a
minifier (or just remove comments and collapse whitespace by hand —
the source is small enough to do manually) and replace
`bookmarklet.min.txt`.

A safe minifier: https://terser.org/repl/ — paste, click "Minify",
then prefix with `javascript:`.

## Why a bookmarklet and not a Slack app?

Three reasons, in order of importance:

1. **No EP IT/Security approval needed.** A Slack app requires
   workspace-admin install and (for incoming endpoints) a publicly
   reachable HTTPS URL. A bookmarklet is JS you save in your own
   browser; nobody else's involved.
2. **No public hosting needed.** Bookmarklet POSTs to localhost
   directly. No ngrok, no tunnel, no deployed endpoint.
3. **Tight scope.** The bookmarklet only runs when you click it,
   only on the page you're viewing. It can't poll, can't subscribe
   to events, can't post messages. That's a feature, not a limit.

If usage outgrows the bookmarklet (e.g. you find yourself wanting
to capture from the Slack desktop app, or you want a slash command),
the next step would be a real Slack app — but that's worth doing
ONLY after the bookmarklet has proven the workflow is valuable.
