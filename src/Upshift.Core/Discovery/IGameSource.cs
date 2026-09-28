using Upshift.Core.Models;

namespace Upshift.Core.Discovery;

/// <summary>One place games can come from (a launcher, the registry, a folder).</summary>
public interface IGameSource
{
    string DisplayName { get; }
    IReadOnlyList<DiscoveredGame> Find(CancellationToken ct);
}
