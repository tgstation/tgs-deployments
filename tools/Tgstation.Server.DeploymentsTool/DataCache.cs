using System.Collections.Generic;

namespace Tgstation.Server.DeploymentsTool
{
    sealed class DataCache
    {
        public Dictionary<string, TelemetryEntry>? Installations { get; set; }
    }
}
