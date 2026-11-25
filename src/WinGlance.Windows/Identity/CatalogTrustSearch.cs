namespace WinGlance.Windows.Identity;

/// <summary>The native enumerator consumes the previous context when advancing.</summary>
internal static class CatalogTrustSearch
{
    internal static (bool MicrosoftVerified, string? Publisher)? Find(
        Func<nint, nint> next,
        Func<nint, (bool MicrosoftVerified, string? Publisher)?> verify,
        Action<nint> release)
    {
        nint current = 0;
        (bool MicrosoftVerified, string? Publisher)? fallback = null;
        try
        {
            while ((current = next(current)) != 0)
            {
                var candidate = verify(current);
                if (candidate is { MicrosoftVerified: true }) return candidate;
                fallback ??= candidate;
            }
            return fallback;
        }
        finally
        {
            // Normal advancement releases the old context. Only an early exit leaves one owned here.
            if (current != 0) release(current);
        }
    }
}
