---
name: datastar
description: Hypermedia framework for building reactive web applications with backend-driven UI. Use this skill for Datastar development patterns, SSE streaming, signal management, DOM morphing, and the "Datastar way" philosophy. Covers data-* attributes, backend integration, and real-time collaborative app patterns.
version: 1.2.0
---

# Datastar

Datastar is a lightweight (~11KB) hypermedia framework for building reactive web applications. It enables server-side rendering with frontend framework capabilities by accepting HTML and SSE responses. The backend drives the frontend - this is the core philosophy.

## Installation

```html
<script type="module" src="https://cdn.jsdelivr.net/gh/starfederation/datastar@1.0.0-RC.7/bundles/datastar.js"></script>
```

## The Tao of Datastar (Philosophy)

The "Datastar way" is a set of opinions from the core team on building maintainable, scalable, high-performance web apps.

### Core Principles

1. **Backend is Source of Truth**: Most application state should reside on the server. The frontend is exposed to users and cannot be trusted.

2. **Sensible Defaults**: Resist the urge to customize before questioning whether changes are truly necessary.

3. **DOM Patching Over Fine-Grained Updates**: The backend drives frontend changes by patching HTML elements and signals. Send large DOM chunks ("fat morph") efficiently.

4. **Morphing as Core Strategy**: Only modified parts of the DOM are updated, preserving state and improving performance.

5. **Restrained Signal Usage**: Signals should serve specific purposes:
   - User interactions (toggling visibility)
   - Binding form inputs to backend requests
   - Overusing signals indicates inappropriate frontend state management

### Anti-Patterns to AVOID

- **Optimistic Updates**: Don't update UI speculatively. Use loading indicators and confirm outcomes only after backend verification.
- **Assuming Fresh Frontend State**: Don't trust frontend cache. Fetch current state from backend.
- **Custom History Management**: Don't manually manipulate browser history. Use standard navigation.

## Architecture: Event Streams All The Way Down

Datastar leverages Server-Sent Events (SSE) as its foundation. The server pushes data through a persistent connection.

**Key insight**: Datastar extends `text/event-stream` beyond SSE's GET-only limitation. HTML fragments wrapped in this protocol support POST, PUT, PATCH, DELETE operations.

### Request/Response Flow

1. User triggers action (click, form submission)
2. Frontend serializes ALL signals (except `_`-prefixed) and sends request
3. Backend reads signals and processes business logic
4. Server streams SSE events back to browser
5. Frontend receives events and updates DOM/signals reactively

## SSE Event Types

### `datastar-patch-elements`

Modifies DOM elements through morphing.

```
event: datastar-patch-elements
data: mode inner
data: selector #target
data: elements <div id="foo">Hello world!</div>
```

**Options:**
- `selector` - CSS selector for target element
- `mode` - `outer` (default), `inner`, `replace`, `prepend`, `append`, `before`, `after`, `remove`
- `useViewTransition` - Enable view transitions

**Multiline Elements:** Each line must be prefixed with `elements`:
```
event: datastar-patch-elements
data: mode inner
data: selector #target
data: elements <div>
data: elements   <span>Line 1</span>
data: elements   <span>Line 2</span>
data: elements </div>
```

### `datastar-patch-signals`

Updates reactive state on the page.

```
event: datastar-patch-signals
data: signals {"foo": 1, "bar": 2}
```

Remove signals by setting to `null`.

## Core Attributes

### Event Handling
- `data-on:click` - Handle click events
- `data-on:submit` - Handle form submissions
- `data-on:keydown__window` - Global key events (with `__window` modifier)
- `data-on-intersect` - Visibility triggers
- `data-on-interval` - Periodic triggers

### State Management
- `data-signals` - Define signals: `data-signals="{count: 0}"`
- `data-bind` - Two-way binding: `data-bind="email"` (NOT `data-bind:value="email"`)
- `data-computed` - Derived values
- `data-init` - Run expression on load (NOT `data-on-load`)

### DOM Updates
- `data-text` - Set text content: `data-text="$count"`
- `data-show` - Conditional visibility: `data-show="$isVisible"`
- `data-class` - Dynamic classes
- `data-attr` - Dynamic attributes: `data-attr:src="$imageUrl"`
- `data-ref` - Element references

### Morphing Control
- `data-ignore` - Skip this element during morph
- `data-ignore-morph` - Preserve element across morphs
- `data-preserve-attr` - Keep specific attributes

### Backend Actions
- `@get('/path')` - GET request
- `@post('/path')` - POST request
- `@put('/path')` - PUT request
- `@patch('/path')` - PATCH request
- `@delete('/path')` - DELETE request

### Utility Actions
- `@peek()` - Access signals without subscribing
- `@setAll()` - Set all matching signals
- `@toggleAll()` - Toggle boolean signals

## Signal Reference Syntax

**CRITICAL:** In expressions, signals MUST be prefixed with `$`:

```html
<!-- CORRECT -->
<span data-text="$count"></span>
<div data-show="$isVisible"></div>
<img data-attr:src="$imageUrl">
<span data-text="`Loaded ${$count} items`"></span>

<!-- WRONG - Missing $ prefix -->
<span data-text="count"></span>
<div data-show="isVisible"></div>
```

## Signal Naming Convention

- **Regular signals**: Sent with every backend request
- **`_`-prefixed signals**: LOCAL ONLY, NOT sent to backend

```html
<div data-signals="{username: '', _isMenuOpen: false}">
  <!-- username goes to backend, _isMenuOpen stays local -->
</div>
```

**IMPORTANT:** Do NOT use `_` prefix for signals that need to be sent to the backend (like form values). Use regular names for data that must reach the server.

## data-bind Syntax

The signal name goes in the VALUE, not as a key suffix:

```html
<!-- CORRECT -->
<input data-bind="email">
<input data-bind="username">

<!-- WRONG -->
<input data-bind:value="email">
```

## Backend SDK Pattern

All SDKs provide helpers for reading signals and streaming responses:

```csharp
// ASP.NET Core example
public static async Task GetFeedsAsync(
    HttpResponse response,
    SqliteConnectionFactory connectionFactory,
    CancellationToken cancellationToken)
{
    // Build HTML content
    StringBuilder html = new();
    html.AppendLine("<div class=\"feed-item\">...</div>");

    // Stream SSE response
    SseHelper sse = response.CreateSseHelper();
    await sse.StartAsync(cancellationToken);
    await sse.PatchElementsAsync(html.ToString(), "#target", "inner", cancellationToken);
}
```

Available SDKs: Go, Python, TypeScript, PHP, Ruby, Rust, Java, Kotlin, Scala, C#, Clojure

## SSE Response Format

When writing SSE responses, ensure:

1. **Content-Type**: `text/event-stream`
2. **No BOM**: Use UTF-8 without BOM (`new UTF8Encoding(false)` in C#)
3. **Multiline data**: Prefix each line with the data key
4. **No HTTP/1.x headers**: Do NOT set `Connection`, `Transfer-Encoding`, `Keep-Alive`, `Upgrade`, or `Proxy-Connection` headers - these are invalid for HTTP/2 and HTTP/3 and will cause Kestrel warnings

```
event: datastar-patch-elements
data: mode inner
data: selector #source-filters
data: elements <div class="item">
data: elements   Content here
data: elements </div>
data: elements <div class="item">
data: elements   More content
data: elements </div>

```

## Common Patterns

### Form Binding

```html
<div data-signals="{email: '', password: ''}">
  <input type="email" data-bind="email">
  <input type="password" data-bind="password">
  <button data-on:click="@post('/login')">Login</button>
</div>
```

### Initialization on Load

```html
<div data-init="@get('/feeds'); @get('/items')">
  <!-- Content loaded on page init -->
</div>
```

### Conditional Rendering

```html
<div data-signals="{_showDetails: false}">
  <button data-on:click="_showDetails = !_showDetails">Toggle</button>
  <div data-show="$_showDetails">
    Details here...
  </div>
</div>
```

### Loading Indicators

```html
<button data-on:click="@post('/submit')"
        data-indicator="fetching"
        data-attr:disabled="$fetching">
  Submit
</button>
<div data-show="$fetching">Loading...</div>
```

### Template Literals in Expressions

```html
<span data-text="`Loaded ${$count} items`"></span>
<span data-text="`Hello, ${$username}!`"></span>
```

### Polling

```html
<div data-on-interval="1000; @get('/status')">
  <!-- Refreshes every second -->
</div>
```

### Lazy Loading

```html
<div data-on-intersect="@get('/load-more')">
  Loading...
</div>
```

### Confirmation Before Action

Use native JavaScript `confirm()` - NOT `@confirm` which doesn't exist:

```html
<!-- CORRECT: Use native JS confirm() -->
<button data-on:click="confirm('Delete this item?') && @delete('/items/123')">
  Delete
</button>

<!-- WRONG: @confirm is not a valid action -->
<button data-on:click="@confirm('Delete?') && @delete('/items/123')">
  Delete
</button>
```

## CQRS Pattern

Segregate commands (writes) from queries (reads):
- **Long-lived read connections**: Real-time updates via SSE
- **Short-lived write requests**: POST/PUT/DELETE

This enables real-time collaboration patterns.

## Performance Tips

1. **Use Compression**: Brotli on SSE streams can achieve 200:1 ratios due to repetitive DOM data
2. **Fat Morph**: Don't fear sending large HTML chunks - morphing is efficient
3. **Debounce Input**: Use `data-on:input__debounce.500ms` for search fields

## Reading Signals on the Backend

**CRITICAL:** Datastar sends signals differently depending on the HTTP method:

- **GET requests**: Signals are sent via the `datastar` query parameter as URL-encoded JSON
- **POST/PUT/PATCH/DELETE requests**: Signals are sent in the request body as JSON

```csharp
public static async Task<Dictionary<string, JsonElement>> ReadSignalsAsync(this HttpRequest request)
{
    // GET requests send signals via query parameter
    string? datastarParam = request.Query["datastar"];
    if (!string.IsNullOrWhiteSpace(datastarParam))
    {
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(datastarParam) ?? [];
    }

    // POST/PUT/PATCH/DELETE send signals in body
    using StreamReader reader = new(request.Body, Encoding.UTF8);
    string body = await reader.ReadToEndAsync();
    return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? [];
}
```

Example GET request URL:
```
GET /river/items?reset=true&datastar=%7B%22selectedFeedIds%22%3A%22abc123%22%7D
```

## Common Mistakes to Avoid

1. **Missing `$` prefix in expressions**: Always use `$signalName` in `data-text`, `data-show`, `data-attr`, etc.
2. **Wrong data-bind syntax**: Use `data-bind="signalName"`, not `data-bind:value="signalName"`
3. **Using `data-on-load`**: Use `data-init` instead
4. **Underscore signals to backend**: Signals starting with `_` are NOT sent to the backend
5. **SSE BOM**: Ensure SSE responses don't include UTF-8 BOM
6. **Incomplete multiline data**: Each line in SSE must have the data key prefix
7. **Using `@confirm` action**: `@confirm` is NOT a valid Datastar action. Use native JavaScript `confirm()` instead:
   ```html
   <!-- WRONG -->
   <button data-on:click="@confirm('Delete?') && @delete('/item/1')">Delete</button>
   
   <!-- CORRECT -->
   <button data-on:click="confirm('Delete?') && @delete('/item/1')">Delete</button>
   ```
8. **Reading signals from body on GET requests**: For GET requests, Datastar sends signals via the `datastar` query parameter, NOT the request body. Always check both locations.
9. **Setting Connection header in SSE**: Do NOT set `response.Headers.Connection = "keep-alive"` in SSE responses. This header is invalid for HTTP/2 and HTTP/3 - Kestrel handles keep-alive automatically and will emit warnings if these headers are present.

## When to Use This Skill

- Building reactive web UIs without heavy JS frameworks
- Implementing real-time features (chat, notifications, live updates)
- Converting traditional server-rendered apps to be more interactive
- Questions about Datastar attributes, patterns, or philosophy
- Debugging SSE connections or signal behavior
- Choosing between client-side and server-side state

## Links

- [Official Documentation](https://data-star.dev)
- [GitHub Repository](https://github.com/starfederation/datastar)
- [Discord Community](https://discord.gg/datastar)
- [Examples](https://data-star.dev/examples)
- [More Examples with ASP.NET Core Minimal API usage](https://github.com/dodyg/practical-aspnetcore/tree/net8.0/projects/datastar)
