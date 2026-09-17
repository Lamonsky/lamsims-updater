using System;

namespace LamSims.Core.Downloading;

/// <summary>
/// What a single mirror said about the entity it served. Mirrors of the same archive do not
/// share an ETag, because object-store ETags derive from the upload rather than the content,
/// so a validator is recorded per URL rather than once per download.
/// </summary>
public sealed record MirrorValidator(string Url, string? ETag, string? LastModified, long? ContentLength)
{
    /// <summary>Weak ETags (W/ prefix) must never be used as range validators.</summary>
    private bool HasStrongETag => ETag is not null && !ETag.StartsWith("W/", StringComparison.Ordinal);

    public bool HasValidator => HasStrongETag || LastModified is not null;

    /// <summary>The If-Range header value, or null when this mirror offers no usable validator.</summary>
    public string? IfRangeValue => HasStrongETag ? ETag : LastModified;

    /// <summary>
    /// True when a freshly probed validator *proves* the mirror still serves the same
    /// entity. A mirror offering no usable validator can prove nothing: matching URL and
    /// length would otherwise read as "unchanged" for a file replaced between sessions,
    /// and resume would trust chunks that are no longer valid. Such a mirror's prior work
    /// is discarded and refetched.
    /// </summary>
    public bool Matches(MirrorValidator other) =>
        HasValidator && other.HasValidator
        && Url == other.Url && ETag == other.ETag && LastModified == other.LastModified
        && ContentLength == other.ContentLength;
}

/// <summary>A mirror URL paired with the validator observed for it.</summary>
public sealed record MirrorSource(Uri Url, MirrorValidator Validator);
