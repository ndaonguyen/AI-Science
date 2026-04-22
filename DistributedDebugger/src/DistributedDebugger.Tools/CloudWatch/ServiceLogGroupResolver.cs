namespace DistributedDebugger.Tools.CloudWatch;

/// <summary>
/// Maps a friendly service name ("content-media-service") + environment ("live")
/// to the actual CloudWatch log group name. EP's pattern is:
///
///     /{env}/ecs/{service}
///
/// where env ∈ { test, staging, live, live-ca-central-1 } and service is the
/// ECS task family name.
///
/// This lives in code (not a config file) intentionally: the resolver is pure
/// and the set of services is small. Moving to appsettings.json is a Phase 3
/// concern if the list grows unwieldy.
/// </summary>
public static class ServiceLogGroupResolver
{
    public static readonly IReadOnlyList<string> KnownEnvironments =
        new[] { "test", "staging", "live", "live-ca-central-1" };

    /// <summary>
    /// The set of services the agent is allowed to query. Deliberate allowlist
    /// so the model can't invent random service names that produce cryptic AWS
    /// errors at runtime.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownServices =
        new[]
        {
            "authoring-service",
            "content-media-service",
            "content-search-service",
        };

    /// <summary>
    /// Build the log group name from a (service, env) pair. Throws on unknown
    /// inputs so the caller surfaces a clear error to the model rather than
    /// silently querying a non-existent log group.
    /// </summary>
    public static string Resolve(string service, string environment)
    {
        if (!KnownServices.Contains(service, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unknown service '{service}'. Known: {string.Join(", ", KnownServices)}.",
                nameof(service));
        }

        if (!KnownEnvironments.Contains(environment, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unknown environment '{environment}'. Known: {string.Join(", ", KnownEnvironments)}.",
                nameof(environment));
        }

        return $"/{environment.ToLowerInvariant()}/ecs/{service.ToLowerInvariant()}";
    }

    /// <summary>
    /// Return just the AWS region implied by the environment name. Most envs
    /// live in a primary region; the ca-central-1 suffix is the one exception.
    /// </summary>
    public static string ResolveRegion(string environment, string defaultRegion)
    {
        if (environment.EndsWith("-ca-central-1", StringComparison.OrdinalIgnoreCase))
        {
            return "ca-central-1";
        }
        return defaultRegion;
    }
}
