using System;

namespace Tgstation.Server.DeploymentsTool
{
    sealed class TelemetryEntry
    {
        public DateTimeOffset UpdatedAt { get; set; }

        public string? FriendlyName { get; set; }

        public string? Version { get; set; }

        public long? ActiveDeploymentId { get; set; }
    }
}
