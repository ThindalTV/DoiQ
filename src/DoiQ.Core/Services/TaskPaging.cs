using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DoiQ.Shared.Enums;

namespace DoiQ.Core.Services;

/// <summary>The fields a task list sorts by; a search cursor is the key of the last task a page returned.</summary>
internal sealed record SortKey(int Id, BoardStatus Status, double SortOrder, DateTime LastActivityAt, DateTime CreatedAt, DateOnly? DueDate, string Title);

/// <summary>
/// The order of a task list: updated (default, newest first), created, lane (by status, then higher SortOrder first),
/// due (soonest first, no date last) or title; dir overrides the direction. Ties break on Id, so the order is total.
/// </summary>
internal sealed class SortComparer : IComparer<SortKey>
{
    private SortComparer(string sort, bool asc)
    {
        Sort = sort;
        Asc = asc;
    }

    public string Sort { get; }
    public bool Asc { get; }

    public static SortComparer For(string? sort, string? dir)
    {
        var s = (sort ?? "updated").ToLowerInvariant();
        if (s is not ("created" or "lane" or "due" or "title")) s = "updated";
        var asc = dir is null ? s is "due" or "title" : dir.Equals("asc", StringComparison.OrdinalIgnoreCase);
        return new SortComparer(s, asc);
    }

    public int Compare(SortKey? x, SortKey? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int c;
        if (Sort == "lane")
        {
            // Lanes group by status in board order whatever the direction; within a lane higher SortOrder is higher on screen.
            c = x.Status.CompareTo(y.Status);
            if (c != 0) return c;
            c = x.SortOrder.CompareTo(y.SortOrder);
        }
        else
        {
            c = Sort switch
            {
                "created" => x.CreatedAt.CompareTo(y.CreatedAt),
                "due" => (x.DueDate ?? DateOnly.MaxValue).CompareTo(y.DueDate ?? DateOnly.MaxValue),
                "title" => string.Compare(x.Title.ToLowerInvariant(), y.Title.ToLowerInvariant(), StringComparison.CurrentCulture),
                _ => x.LastActivityAt.CompareTo(y.LastActivityAt),
            };
        }
        if (!Asc) c = -c;
        return c != 0 ? c : x.Id.CompareTo(y.Id);
    }
}

/// <summary>Opaque search cursor: the sort it belongs to and the key of the last task returned.</summary>
internal static class SearchCursor
{
    private sealed record Payload(string Sort, bool Asc, SortKey Key);

    public static string Encode(SortKey last, SortComparer order) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new Payload(order.Sort, order.Asc, last)));

    /// <summary>The key to continue after, or null for the first page. A cursor from another sort is refused.</summary>
    public static SortKey? Decode(string? cursor, SortComparer order)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        Payload? p;
        try { p = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(cursor.Trim())); }
        catch (Exception e) when (e is FormatException or JsonException) { p = null; }
        if (p?.Key is null) throw new ValidationException("cursor is not valid: pass the nextCursor of a previous page unchanged.");
        if (p.Sort != order.Sort || p.Asc != order.Asc)
            throw new ValidationException("cursor belongs to a different sort: page with the same sort and dir as the first call.");
        return p.Key;
    }
}

/// <summary>Opaque archive cursor: DoneAt (ticks) and Id of the last task returned.</summary>
internal static class ArchiveCursor
{
    public static string Encode(DateTime doneAt, int id) =>
        Base64Url.EncodeToString(Encoding.ASCII.GetBytes($"{doneAt.Ticks.ToString(CultureInfo.InvariantCulture)}:{id.ToString(CultureInfo.InvariantCulture)}"));

    public static (DateTime DoneAt, int Id)? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.ASCII.GetString(Base64Url.DecodeFromChars(cursor.Trim())).Split(':');
            if (parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && ticks <= DateTime.MaxValue.Ticks
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                return (new DateTime(ticks, DateTimeKind.Utc), id);
        }
        catch (FormatException) { /* not base64url */ }
        throw new ValidationException("cursor is not valid: pass the nextCursor of a previous page unchanged.");
    }
}
