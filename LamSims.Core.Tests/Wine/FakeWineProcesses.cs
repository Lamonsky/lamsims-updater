using System.Collections.Generic;
using LamSims.Core.Unlocking.Wine;

namespace LamSims.Core.Tests;

/// <summary>
/// Every operation test drives this rather than the real /proc, so the suite's answers about
/// liveness are the ones the test chose and no test depends on what is running on the machine.
/// </summary>
public sealed class FakeWineProcesses : IWineProcesses
{
    /// <summary>Set true to make the prefix report busy, the way a running game does.</summary>
    public bool Live { get; set; }

    public List<string> Clients { get; } = [];

    /// <summary>The prefixes asked about, so a test can assert the question was scoped.</summary>
    public List<string> Asked { get; } = [];

    public bool IsPrefixLive(string prefixRoot)
    {
        Asked.Add(prefixRoot);
        return Live;
    }

    public IReadOnlyList<string> RunningClients(string prefixRoot,
                                               IReadOnlyList<string> processNames) => Clients;
}
