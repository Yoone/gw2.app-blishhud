# Protocol

WebSocket protocol between the GW2.app client (currently the website in a
browser) and the Blish HUD module ("server"). The module listens on
`ws://<host>:38473/`. The same port responds to plain HTTP `GET /` with
`426 Upgrade Required`.

All messages are **JSON text frames**. Image bytes are base64-embedded
(~33% overhead).

Every message has a `type`. Only `state` carries `protocol` (currently `1`);
the version is checked once per connection.

The module relies on the WebSocket implementation's built-in keepalive
(.NET `HttpListener` pings every 30 s) for dead-peer detection.

The HTTP layer also serves CORS preflights, including
`Access-Control-Allow-Private-Network`, so a page on `https://gw2.app`
can talk to a loopback or LAN-IP host. Allowed origins: `localhost`,
`gw2.app`, `*.gw2.app`, and any RFC1918 private IP.

## Connection lifecycle

**Single active client ("last writer wins").** Only one logical client at
a time. A new connection immediately closes any existing one with code
`4000` ("superseded"). Handles the disconnect-then-reconnect race.

**Handshake:**
1. Client opens WS.
2. Client sends `state`. If not received within 5 s, server closes with `4001`.
3. Server sends `subscribe` with the list IDs currently open in-game (may be empty).
4. Client streams `entry` messages with images for each entry of every subscribed list, list order then entry order.
5. Client sends `synced` listing the now-fully-imaged list IDs.

**Steady state:**
- Server sends `subscribe` whenever the in-game subscription set changes.
- Client sends `state` whenever lists or their structure change.
- Client sends `entry` whenever a flag or image changes (image only when subscribed).
- Client sends `synced` after each bulk re-image triggered by `subscribe` or by an entries-array change in `state`.

## Domain model

```
List  = { id, name, settings (opaque JSON), entries: [Entry, ...], is_loot_bag?: bool }
Entry = { completed: bool, autoCompleted: bool, entry_type?: string, has_hover_card?: bool }
```

Entries have **no stable ID**. They are addressed by `(listId, index)`
within the latest `state`.

`entry_type` is a short string identifying the kind of entry, mirroring the discriminator the website uses internally (e.g. `"item"`, `"timer"`, `"location"`, `"mapchest"`, `"recipe"`, `"dailypsna"`, `"vendoritem"`, `"tpdelivery"`, `"wv"`, `"custom"`, etc.). It exists so the module can mirror website-side per-type behaviour, e.g. an in-game "Copy waypoints" can include only entries with `entry_type === "location"` (those are the ones that may carry a Waypoint or POI `chat_link`), `entry_type === "dailypsna"` (which carries multiple vendor waypoints in `chat_link`, space-separated), and `entry_type === "vendoritem"` (a vendor location for a tracked item).

`has_hover_card` is set to `true` on entries that have an associated hovercard the module can request via `request_hover` (see below). Absent or `false` means the entry has no hovercard and the module should suppress any hover-related UX for that row.

### Loot Bag

The user's Loot Bag is a single, special list flagged with `is_loot_bag: true`. There is at most one per session. Treat it as a regular list for imagery purposes, with two differences:

- **ID**: A logged-in user's loot bag has a real UUID like any other list. An anonymous (not-logged-in) user's loot bag has no persisted ID and is sent under the stable wire ID `"loot-bag"` instead. The module should key off `is_loot_bag` rather than the ID string when special-casing it.
- **Completion is read-only**: the loot bag has no per-entry completion concept. The server MUST NOT send `set_entry_completed` for any list with `is_loot_bag: true`; the client ignores such messages with `console.error`.

## Messages

### Client → Server

#### `state`: full snapshot, metadata only (no imagery)

```json
{ "protocol": 1, "type": "state", "lists": [ List, ... ] }
```

Imagery semantics:
- Lists whose `entries` array materially changed (added/removed/reordered, or per-entry flags changed) need re-imaging by the client for any subscribed affected list. The client ends each re-image window with `synced`.
- Pure metadata changes (`name`, `settings`) require no re-imaging.
- The server keeps the previously rendered image for each `(listId, index)` until a fresh `entry` arrives, to avoid mid-re-image visual glitches.

Pruning is driven by `synced`, not `state`:
- On `synced` for a `listId`, drop cached `(listId, index)` images where `index >= state.lists[listId].entries.length`.
- On `synced` for a `listId` no longer in `state`, drop all of its cached imagery.

#### `entry`: single-entry update

Always carries current `completed` and `autoCompleted` (truth, not delta).
Carries `image_b64` + `mime` only when both: the list is subscribed AND the
image changed (or this is part of a bulk re-image). For unsubscribed lists,
`entry` is metadata-only.

```json
{
  "type": "entry",
  "listId": "...",
  "index": 3,
  "completed": true,
  "autoCompleted": false,
  "entry_type": "item",                       // optional
  "has_hover_card": true,                     // optional, omitted when false
  "mime": "image/jpeg",                       // optional
  "image_b64": "...",                         // optional
  "chat_link": "[&BAgIAAA=]",                 // optional
  "link": "https://wiki.guildwars2.com/..."   // optional
}
```

`entry_type` mirrors the field of the same name in `state`'s `Entry` shape. `has_hover_card` is the same truthy-only flag as in `state`: present and `true` means "this row currently has a hovercard the module can `open_hover` for"; absent means "no hovercard, suppress hover UX for this row". When the website renders a row whose hovercard appears or disappears, it re-emits the `entry` with the field flipped accordingly.

`mime` is `image/png` or `image/jpeg`, though the website currently only
sends PNG. Decode failures are logged and the previous image is retained.

`chat_link` is the chat-paste string for this entry (waypoint, POI, item
code, etc.). Opaque to the server, pasted into game chat verbatim. May be
absent (nothing to copy) or contain multiple codes separated by spaces
(e.g. PSNA waypoints). Per-field deduped: an `entry` is only emitted when
`chat_link` (or any other field) changes.

`link` is an external URL the user attached to a custom entry (e.g. a
wiki page, a guide, a video). Currently emitted only for entries with
`entry_type === "custom"`. Treat as an `http(s)://` URL to open in the
user's default browser when the in-game module exposes a "go to source"
affordance. Per-field deduped like `chat_link`.

#### `synced`: end of bulk re-image

```json
{ "type": "synced", "listIds": [ "...", "..." ] }
```

The module dismisses the loading indicator and commits the new view in
one frame.

#### `hover_image`: hovercard PNG frame for the currently-open hover

Streamed by the client while a hover subscription is open (see
`open_hover` below). Carries the rendered hovercard image for the
subscribed entry.

```json
{
  "type": "hover_image",
  "listId": "...",
  "index": 3,
  "mime": "image/png",
  "image_b64": "..."
}
```

- Only emitted while exactly one hover is open. Frames carrying
  `(listId, index)` that don't match the currently-open subscription are
  the result of a stale superseded capture and the server SHOULD ignore
  them with `console.warn` (treat them as harmless leftovers, not a
  protocol violation).
- The client emits the first frame as soon as the hovercard's DOM has
  rendered, and continues emitting whenever the rendered pixels change
  (e.g. async data like TP prices land, animations advance), at the same
  capture cadence used for row images. Identical consecutive frames are
  not re-sent.
- `mime` is `image/png` or `image/jpeg`, though the website currently
  only sends PNG.
- Stops when the server sends `close_hover`, or when a new `open_hover`
  supersedes the current subscription.

### Server → Client

#### `subscribe`: absolute set of list IDs the module wants imagery for

```json
{ "type": "subscribe", "listIds": [ "...", "..." ] }
```

Sent once after the initial `state`, then any time the in-game subscription
changes. Empty list = unsubscribe from everything. The server only ever
sends IDs present in the latest `state`; unknown IDs are ignored by the
client (with `console.error`).

On a `subscribe` that adds new IDs, the client streams `entry` messages
(with images) for every entry of each newly-subscribed list (list order,
entry order), then sends `synced` listing those IDs.

#### `set_entry_completed`: user toggled a checkbox in-game

```json
{ "type": "set_entry_completed", "listId": "...", "index": 3, "completed": true }
```

- Targets only the user-controlled `completed` flag. The server MUST NOT send this for entries whose `autoCompleted` is `true`; the client ignores such messages with `console.error`.
- `(listId, index)` resolves against the client's most recent `state`. Unknown lists or out-of-range indices are ignored with `console.error`.
- Idempotent: a no-op toggle is treated as such, so the server may resend to recover.

#### `open_hover`: subscribe to streaming hovercard frames for an entry

```json
{ "type": "open_hover", "listId": "...", "index": 3 }
```

- Only valid for entries with `has_hover_card: true` in the latest `state`. Opening hover for an entry without it is a server bug; the client ignores with `console.error`.
- `(listId, index)` resolves against the client's most recent `state`. Unknown lists or out-of-range indices are ignored with `console.error`.
- **Last-writer-wins; one active subscription at a time.** A new `open_hover` implicitly closes any prior subscription. The client tears down the previous hovercard, mounts the new one, and starts streaming `hover_image` frames for the new entry. The server does not need to send `close_hover` before re-opening.
- The client begins emitting `hover_image` frames as soon as the hovercard's DOM has rendered, and continues for as long as the subscription is open (see `hover_image` for cadence and dedup semantics). Hovercard content for entries that load asynchronously (e.g. TP prices) is allowed to update over the lifetime of the subscription; each update produces a new `hover_image` frame.

#### `close_hover`: end the current hover subscription

```json
{ "type": "close_hover" }
```

- No `(listId, index)`: there is at most one active subscription, so the addressee is implicit.
- The client unmounts the hovercard and stops emitting `hover_image` frames.
- No-op if no subscription is open (client logs `console.warn` and ignores).

**Lifecycle expectations and caching:**

The subscription model puts the lifecycle in the server's hands. A typical flow is:
- User hovers an entry in-game → server sends `open_hover`.
- User leaves the entry → server sends `close_hover`.
- User hovers a different entry → server sends `open_hover` for the new one (no intermediate `close_hover` needed).

Because hovercard content can keep updating after the initial paint (TP prices, achievement progress, etc.), the server SHOULD treat `hover_image` as a live stream while the subscription is open and replace the displayed image on every new frame. After `close_hover` (or supersession), the server MAY keep the most recent `hover_image` PNG cached keyed by `(listId, index)` to display instantly the next time the user hovers the same entry, invalidating that cache when a new row `entry` with `image_b64` arrives for the same `(listId, index)` (the row image changing is the client's signal that underlying data changed).

## Close codes

| Code  | Meaning                                  | Client retry policy |
|-------|------------------------------------------|---------------------|
| 1000  | Normal closure (user-initiated)          | No retry            |
| 4000  | Superseded by newer connection           | No retry; user reconnects manually |
| 4001  | Handshake timeout (no `state` within 5s) | No retry; protocol broken |
| 4002  | Protocol violation (bad JSON, etc.)      | No retry; protocol broken |
| other | Abnormal close (network drop, etc.)     | Auto-retry with backoff |

The connection is started only by explicit user action on the website
("Connect" button). A `4000` means another GW2.app tab took over; the
user must reconnect here manually, no silent retry.

## Versioning

`protocol: 1` on `state` is the wire-format version. Incompatible changes
bump it. The server validates on the first message of each connection and
closes with `4002` on mismatch.
