namespace DistributedDebugger.Tools.CloudWatch;

/// <summary>
/// Maps a friendly service name ("content-media-service") + environment ("test")
/// to the actual CloudWatch log group name. EP's pattern is:
///
///     /{env}/ecs/{service}
///
/// e.g. /test/ecs/authoring-service
///
/// The "dev" AWS profile gives access to the TEST account only.
/// Use the "live" profile for the live environment.
/// </summary>
public static class ServiceLogGroupResolver
{
    public static readonly IReadOnlyList<string> KnownEnvironments =
        new[] { "test", "staging", "live", "live-ca-central-1" };

    /// <summary>
    /// The set of services the agent is allowed to query.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownServices =
        new[]
        {
            "authoring-service",
            "content-media-service",
            "content-search-service",
            "ai-content-authoring",
            "ai-content-authoring-processing",
            "authentication",
            "class-management",
            "core-entities-users-api",
            "core-entities-orgs",
            "graphql-gateway-fusion",
            "learning-pathways-backend",
            "queues-app",
            "web-app",
        };

    public static string Resolve(string service, string environment)
    {
        // Soft validation — warn but still try, so the model can explore.
        // CloudWatch will return a clear "log group does not exist" if wrong.
        return $"/{environment.ToLowerInvariant()}/ecs/{service.ToLowerInvariant()}";
    }

    public static string ResolveRegion(string environment, string defaultRegion)
    {
        if (environment.EndsWith("-ca-central-1", StringComparison.OrdinalIgnoreCase))
        {
            return "ca-central-1";
        }
        return defaultRegion;
    }
}
