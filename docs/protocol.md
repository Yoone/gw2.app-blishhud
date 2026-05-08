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
Entry = { completed: bool, autoCompleted: bool, entry_type?: string }
```

Entries have **no stable ID**. They are addressed by `(listId, index)`
within the latest `state`.

`entry_type` is a short string identifying the kind of entry, mirroring the discriminator the website uses internally (e.g. `"item"`, `"timer"`, `"location"`, `"mapchest"`, `"recipe"`, `"dailypsna"`, `"tpdelivery"`, `"wv"`, `"custom"`, etc.). It exists so the module can mirror website-side per-type behaviour, e.g. an in-game "Copy waypoints" can include only entries with `entry_type === "location"` (those are the ones that may carry a Waypoint or POI `chat_link`) and `entry_type === "dailypsna"` (which carries multiple vendor waypoints in `chat_link`, space-separated).

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
  "mime": "image/jpeg",          // optional
  "image_b64": "...",            // optional
  "chat_link": "[&BAgIAAA=]"     // optional
}
```

`mime` is `image/png` or `image/jpeg`, though the website currently only
sends PNG. Decode failures are logged and the previous image is retained.

`chat_link` is the chat-paste string for this entry (waypoint, POI, item
code, etc.). Opaque to the server, pasted into game chat verbatim. May be
absent (nothing to copy) or contain multiple codes separated by spaces
(e.g. PSNA waypoints). Per-field deduped: an `entry` is only emitted when
`chat_link` (or any other field) changes.

#### `synced`: end of bulk re-image

```json
{ "type": "synced", "listIds": [ "...", "..." ] }
```

The module dismisses the loading indicator and commits the new view in
one frame.

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
