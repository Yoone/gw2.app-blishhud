using System;
using System.Collections.Generic;
using Blish_HUD.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GW2app
{
    internal class ListWindowEntry
    {
        public string ListId;
        public GW2appWindow Window;
        public Panel Panel;
        // Footer area below the main panel (action button: "Copy waypoints" / "Back").
        public Panel FooterPanel;
        // Vertical space (px) reserved below the main panel for the footer.
        // 0 when no footer is shown.
        public int FooterReserve;
        // While the panel is in copy-waypoints mode, this re-renders only the chunk
        // buttons (leaving the slider alive so the user's drag doesn't dead-end).
        // Null otherwise.
        public System.Action RerenderCopyChunks;
    }

    // Fields are assigned via Newtonsoft.Json reflection; suppress "never assigned" warnings.
#pragma warning disable 0649

    internal enum MessageKind { State, Entry, Synced, ConnectionLost, ClientReplaced }

    internal class IncomingMessage
    {
        public MessageKind Kind;
        public StateMessage State;
        public EntryMessage Entry;
        public List<string> SyncedListIds;
    }

    internal class StateMessage
    {
        [JsonProperty("protocol")] public int Protocol;
        [JsonProperty("type")] public string Type;
        [JsonProperty("lists")] public List<ListDto> Lists;
    }

    internal class ListDto
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        [JsonProperty("settings")] public JToken Settings;
        [JsonProperty("entries")] public List<EntryDto> Entries;
        [JsonProperty("is_loot_bag")] public bool IsLootBag;
    }

    internal class EntryDto
    {
        [JsonProperty("completed")] public bool Completed;
        [JsonProperty("autoCompleted")] public bool AutoCompleted;
        // Optional. Mirrors the website's entry-type discriminator. Used in the
        // module to scope features like "Copy waypoints" to entries that carry a
        // chat_link representing a location (currently "location" and "dailypsna").
        [JsonProperty("entry_type")] public string EntryType;
    }

    internal class EntryMessage
    {
        [JsonProperty("type")] public string Type;
        [JsonProperty("listId")] public string ListId;
        [JsonProperty("index")] public int Index;
        [JsonProperty("completed")] public bool Completed;
        [JsonProperty("autoCompleted")] public bool AutoCompleted;
        [JsonProperty("mime")] public string Mime;
        [JsonProperty("image_b64")] public string ImageB64;
        [JsonProperty("chat_link")] public string ChatLink;
    }

    internal class SubscribeMessage
    {
        [JsonProperty("type")] public string Type;
        [JsonProperty("listIds")] public List<string> ListIds;
    }

    internal class SetEntryCompletedMessage
    {
        [JsonProperty("type")] public string Type;
        [JsonProperty("listId")] public string ListId;
        [JsonProperty("index")] public int Index;
        [JsonProperty("completed")] public bool Completed;
    }

#pragma warning restore 0649

    internal class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }
}
