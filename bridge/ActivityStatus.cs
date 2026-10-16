using Newtonsoft.Json.Linq;

namespace DD2Bridge
{
    public sealed partial class DD2BridgePlugin
    {
        // Presentation-only activity message. This is deliberately not a READY
        // boundary and advertises no actions. The host may render it however it
        // likes (spinner today, something else later).
        private void QueueActivityStatus(string activity)
        {
            if (string.IsNullOrEmpty(activity))
            {
                return;
            }

            QueueOutgoingMessage(
                new JObject
                {
                    ["type"] = "status",
                    ["activity"] = activity
                });
        }
    }
}
