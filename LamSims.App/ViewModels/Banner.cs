namespace LamSims.App.ViewModels;

public enum BannerKind { Info, Warning, Error }

/// <summary>
/// <paramref name="Id"/> keys a banner so a repeated cause replaces its predecessor instead of
/// stacking.
/// </summary>
public sealed record Banner(string Id, string Text, BannerKind Kind)
{
    public bool IsWarning => Kind is BannerKind.Warning;

    public bool IsError => Kind is BannerKind.Error;
}
