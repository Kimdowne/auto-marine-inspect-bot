using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json.Converters;

namespace ShipRobot.EquipmentMonitoring
{
    public static class EquipmentWireProtocol
    {
        public const int Version = 1;
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Converters = new List<JsonConverter> { new StringEnumConverter() },
            DateFormatString = "yyyy-MM-ddTHH:mm:ss.fffffffK"
        };
        public static string Telemetry(long sequence, IEnumerable<EquipmentSnapshot> equipment) =>
            JsonConvert.SerializeObject(new { version = Version, type = "telemetry", sequence,
                sentAtUtc = DateTime.UtcNow, equipment }, Settings);
        public static string Response(string id, bool ok, string code, string message) =>
            JsonConvert.SerializeObject(new { version = Version, type = "commandResult", commandId = id, ok, code, message }, Settings);
    }

    // This is the command integration seam for a future robot mission handler.
    public sealed class EquipmentCommandRouter
    {
        private long session = -1;
        private readonly Dictionary<string, (string request, string response)> cache = new Dictionary<string, (string, string)>();
        private readonly Queue<string> order = new Queue<string>();
        public string Handle(long connectionId, string json, Func<string, string, string> execute)
        {
            if (session != connectionId) { session = connectionId; cache.Clear(); order.Clear(); }
            string id = null;
            JObject command;
            try
            {
                command = JObject.Parse(json);
                id = command["commandId"]?.Type == JTokenType.String ? (string)command["commandId"] : null;
                if (string.IsNullOrWhiteSpace(id) || id.Length > 80) return EquipmentWireProtocol.Response(null, false, "invalid_command", "commandId must be 1-80 characters");
                if (command["version"]?.Type != JTokenType.Integer || (int)command["version"] != 1 || (string)command["type"] != "command")
                    return EquipmentWireProtocol.Response(id, false, "invalid_protocol", "Expected version 1 and type command");
                if (command["equipmentId"]?.Type != JTokenType.String || command["action"]?.Type != JTokenType.String)
                    return EquipmentWireProtocol.Response(id, false, "invalid_command", "equipmentId and action are required strings");
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidCastException || ex is FormatException || ex is OverflowException)
            { return EquipmentWireProtocol.Response(id, false, "invalid_json", "Invalid command JSON"); }
            if (cache.TryGetValue(id, out var previous))
                return previous.request == json ? previous.response : EquipmentWireProtocol.Response(id, false, "id_conflict", "ID reused with different content");
            string result;
            try
            {
                string error = execute((string)command["equipmentId"], (string)command["action"]);
                result = EquipmentWireProtocol.Response(id, error == null, error == null ? "ok" : "rejected", error ?? "Command applied");
            }
            catch (Exception ex) { result = EquipmentWireProtocol.Response(id, false, "execution_error", ex.Message); }
            cache[id] = (json, result); order.Enqueue(id);
            if (order.Count > 128) cache.Remove(order.Dequeue());
            return result;
        }
    }
}
