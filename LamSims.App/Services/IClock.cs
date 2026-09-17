using System;

namespace LamSims.App.Services;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
