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
    }

    internal class EntryDto
    {
        [JsonProperty("completed")] public bool Completed;
        [JsonProperty("autoCompleted")] public bool AutoCompleted;
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
