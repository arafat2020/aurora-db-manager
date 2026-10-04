namespace AuroraDbManager.Api.Application;

/// <summary>What every paged listing has in common.</summary>
public static class Paging
{
    /// <summary>
    /// The last page that can be asked for. With at most 100 items a page, the offset of any page
    /// up to here fits an <see cref="int"/> many times over; without a bound, a page number near
    /// <see cref="int.MaxValue"/> overflowed into a negative offset, which the database refuses.
    /// </summary>
    public const int MaxPage = 1_000_000;
}
