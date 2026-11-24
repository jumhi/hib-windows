// H.I.B — Flick bundles (SPACE.md §14): many items thrown at once land as one stack, which fans out into a
// pattern on click. This file is the UI-free part: the bundle model (whole-bundle progress, title, the files a
// drag of the stack carries), the registry that groups flickStarts by bundle id, and the fan-out geometry.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using HIB.Core.Phone;

namespace HIB.Views.Flick;

/// <summary>One bundle: every item of one throw, in throw order (index 0 is the top of the stack).</summary>
public sealed class FlickBundle
{
    public FlickBundle(string id, int count)
    {
        Id = id;
        Count = Math.Clamp(count, 1, 1000);
        Items = new FlickCard?[Count];
    }

    public string Id { get; }
    /// <summary>How many items the sender threw (the count badge).</summary>
    public int Count { get; }
    /// <summary>The items by index; null until that item's flickStart (or transfer) arrived.</summary>
    public FlickCard?[] Items { get; }
    public string? From { get; set; }
    public DateTime Started { get; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public bool Landed { get; set; }
    public bool Hovered { get; set; }
    /// <summary>Spread out into its pattern (or on its way there).</summary>
    public bool Fanned { get; set; }
    /// <summary>Seconds left before the stack settles away (counts down once landed, stacked, idle and not hovered).</summary>
    public double Remaining { get; set; } = 14;
    /// <summary>Items that never announced themselves are given up on (they count as failed).</summary>
    public bool MissingGaveUp { get; set; }

    /// <summary>Puts an item at its place. False when the index doesn't fit this bundle.</summary>
    public bool Put(int index, FlickCard card)
    {
        if (index < 0 || index >= Count) return false;
        Items[index] = card;
        From ??= card.From;
        LastActivity = DateTime.UtcNow;
        return true;
    }

    public int IndexOf(string flickId) => Array.FindIndex(Items, c => c?.Id == flickId);

    /// <summary>Items that announced themselves and weren't dragged away, in throw order.</summary>
    public IReadOnlyList<FlickCard> Present => Items.Where(c => c is { Taken: false }).Select(c => c!).ToList();

    public int Announced => Items.Count(c => c is not null);
    public int Arrived => Items.Count(c => c is { Transfer: FlickTransfer.Done or FlickTransfer.None });
    public int Failed => Items.Count(c => c is { Transfer: FlickTransfer.Failed }) + (MissingGaveUp ? Count - Announced : 0);
    /// <summary>Still receiving: an item is in flight, or announced items are still missing.</summary>
    public bool IsBusy => Items.Any(c => c?.IsBusy == true) || (!MissingGaveUp && Announced < Count);
    public bool AllArrived => Arrived == Count;
    public long TotalSize => Items.Sum(c => c?.Size ?? 0);

    /// <summary>
    /// Progress of the whole bundle, 0…1: bytes received over bytes announced, where items without a size (or not yet
    /// announced) weigh as much as an average item. Arrived items count in full.
    /// </summary>
    public double Progress
    {
        get
        {
            var known = Items.Where(c => c is not null && c.Size > 0).ToList();
            var avg = known.Count > 0 ? known.Average(c => (double)c!.Size) : 1;
            double total = 0, done = 0;
            foreach (var c in Items)
            {
                var weight = c is { Size: > 0 } ? c.Size : avg;
                total += weight;
                if (c is null) continue;
                done += weight * (c.Transfer switch
                {
                    FlickTransfer.Done or FlickTransfer.None or FlickTransfer.Failed => 1,
                    FlickTransfer.Receiving => Math.Clamp(c.Progress, 0, 1),
                    _ => 0,
                });
            }
            return total <= 0 ? 0 : Math.Clamp(done / total, 0, 1);
        }
    }

    /// <summary>The common kind of the items ("image", "video", "file", …), or "mixed".</summary>
    public string Kind
    {
        get
        {
            var kinds = Items.Where(c => c is not null).Select(c => c!.Kind).Distinct().ToList();
            return kinds.Count == 1 ? kinds[0] : kinds.Count == 0 ? "file" : "mixed";
        }
    }

    /// <summary>"5 photos", "3 videos", "4 photos & videos", "2 files", "6 items".</summary>
    public string Title => $"{Count} {Noun(Count, Items.Where(c => c is not null).Select(c => c!.Kind).Distinct().ToList())}";

    public static string Noun(int count, IReadOnlyCollection<string> kinds)
    {
        var one = count == 1;
        if (kinds.Count == 0) return one ? "item" : "items";
        if (kinds.All(k => k == "image")) return one ? "photo" : "photos";
        if (kinds.All(k => k == "video")) return one ? "video" : "videos";
        if (kinds.All(k => k is "image" or "video")) return "photos & videos";
        if (kinds.All(k => k == "link")) return one ? "link" : "links";
        if (kinds.All(k => k == "text")) return one ? "note" : "notes";
        if (kinds.All(k => k == "file")) return one ? "file" : "files";
        return one ? "item" : "items";
    }

    /// <summary>The line under the title: progress while it arrives, then where it came from and how big it is.</summary>
    public string Subtitle
    {
        get
        {
            if (IsBusy) return $"Receiving {Arrived} of {Count} · {(int)Math.Round(Progress * 100)}%";
            if (Failed > 0) return $"{Arrived} of {Count} arrived";
            var size = TotalSize > 0 ? FlickCard.FormatSize(TotalSize) : null;
            return (From is { Length: > 0 } from ? "From " + from : null) is { } who
                ? size is null ? who : $"{who} · {size}"
                : size ?? "All arrived";
        }
    }

    /// <summary>The first item still in the bundle (the top of the stack).</summary>
    public FlickCard? Top => Items.FirstOrDefault(c => c is { Taken: false });

    /// <summary>Dragging the stack drags these: every arrived file still in the bundle, in throw order.</summary>
    public IReadOnlyList<string> DragPaths => Items.Where(c => c is { Taken: false, FilePath: not null }).Select(c => c!.FilePath!).ToList();

    /// <summary>Where the bundle's files were saved (the "+N" card opens it).</summary>
    public string? Folder => Items.Where(c => c?.FilePath is not null).Select(c => Path.GetDirectoryName(c!.FilePath!)).FirstOrDefault(d => !string.IsNullOrEmpty(d));

    /// <summary>The count badge: how many items the stack holds now ("99+" beyond that).</summary>
    public string Badge
    {
        get
        {
            var n = Count - Items.Count(c => c is { Taken: true });
            return n > 99 ? "99+" : n.ToString(CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>Groups flickStarts by <c>bundle.id</c> (SPACE.md §14). Bundles of one aren't bundles.</summary>
public sealed class FlickBundleRegistry
{
    readonly Dictionary<string, FlickBundle> _bundles = new();
    readonly Dictionary<string, string> _flickToBundle = new();

    public IReadOnlyCollection<FlickBundle> All => _bundles.Values;
    public int Count => _bundles.Count;

    public FlickBundle? Get(string bundleId) => _bundles.TryGetValue(bundleId, out var b) ? b : null;

    /// <summary>The bundle a flick id belongs to, if any.</summary>
    public FlickBundle? ForFlick(string flickId)
        => _flickToBundle.TryGetValue(flickId, out var bid) && _bundles.TryGetValue(bid, out var b) ? b : null;

    /// <summary>
    /// Adds an item to its bundle (creating the bundle on its first item, whichever index that is). Null when the
    /// item isn't part of a bundle of two or more, or its index doesn't fit — then it lands as a single card.
    /// </summary>
    public (FlickBundle Bundle, bool Created)? Accept(FlickBundleTag? tag, FlickCard card)
    {
        if (tag is null || tag.Count < 2) return null;
        var created = false;
        if (!_bundles.TryGetValue(tag.Id, out var bundle))
        {
            if (tag.Index < 0 || tag.Index >= tag.Count) return null;
            bundle = new FlickBundle(tag.Id, tag.Count);
            _bundles[tag.Id] = bundle;
            created = true;
        }
        if (!bundle.Put(tag.Index, card))
        {
            if (created) _bundles.Remove(tag.Id);
            return null;
        }
        _flickToBundle[card.Id] = tag.Id;
        return (bundle, created);
    }

    public void Remove(string bundleId)
    {
        if (!_bundles.Remove(bundleId)) return;
        foreach (var key in _flickToBundle.Where(kv => kv.Value == bundleId).Select(kv => kv.Key).ToList()) _flickToBundle.Remove(key);
    }
}

/// <summary>One place in a fan: unit offsets from the centre (1 unit = a card plus the gap), and which item sits there.</summary>
/// <param name="Item">The item's position among the bundle's present items; -1 for the "+N" card.</param>
/// <param name="More">For the "+N" card: how many more items the folder holds.</param>
public readonly record struct FanSlot(double Ux, double Uy, int Item, int More)
{
    public bool IsMore => Item < 0;
}

/// <summary>Where every card of a fan sits (DIPs, screen-relative) and how big the cards are.</summary>
public sealed record FanPlacement(IReadOnlyList<FanSlot> Slots, IReadOnlyList<Point> Centers, double Scale, Rect Bounds);

/// <summary>
/// The fan-out patterns (SPACE.md §14): 2 side by side · 3 two on top, one centred below · 4 = 2×2 · 5 dice-five
/// (2 top, 1 middle, 2 bottom) · 6 = 3×2 · 7 = 2-3-2 · 8 = 3-2-3 · 9 = 3×3 · 10+ a 3×3 grid whose last card is "+N".
/// </summary>
public static class FlickFan
{
    /// <summary>Cards shown at most; beyond that the last place is the "+N" card.</summary>
    public const int MaxCards = 9;
    public const double Gap = 22;

    /// <summary>Cards per row, top to bottom.</summary>
    public static IReadOnlyList<int> Rows(int count) => count switch
    {
        <= 1 => [1],
        2 => [2],
        3 => [2, 1],
        4 => [2, 2],
        5 => [2, 1, 2],
        6 => [3, 3],
        7 => [2, 3, 2],
        8 => [3, 2, 3],
        _ => [3, 3, 3],
    };

    public static IReadOnlyList<FanSlot> Slots(int count)
    {
        count = Math.Max(1, count);
        if (count == 5)
        {
            // Dice-five: the middle card nests between the corners (half a row up and down), like the pips.
            return [new(-1, -0.5, 0, 0), new(1, -0.5, 1, 0), new(0, 0, 2, 0), new(-1, 0.5, 3, 0), new(1, 0.5, 4, 0)];
        }
        var rows = Rows(count);
        var list = new List<FanSlot>();
        var item = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            var uy = r - (rows.Count - 1) / 2.0;
            for (var c = 0; c < rows[r]; c++)
            {
                var ux = c - (rows[r] - 1) / 2.0;
                var isMore = count > MaxCards && item == MaxCards - 1;
                list.Add(isMore ? new FanSlot(ux, uy, -1, count - (MaxCards - 1)) : new FanSlot(ux, uy, item, 0));
                item++;
            }
        }
        return list;
    }

    /// <summary>
    /// Places a fan of <paramref name="count"/> centred on (<paramref name="cx"/>, <paramref name="cy"/>) — where the stack
    /// landed — moved just enough to stay inside <paramref name="area"/>, and shrunk (never below 45 %) when it can't fit.
    /// </summary>
    public static FanPlacement Place(int count, double cx, double cy, Rect area, double cardWidth, double cardHeight, double gap = Gap)
    {
        var slots = Slots(count);
        var spanX = slots.Max(s => s.Ux) - slots.Min(s => s.Ux);
        var spanY = slots.Max(s => s.Uy) - slots.Min(s => s.Uy);
        // width(s) = spanX·(cardWidth·s + gap) + cardWidth·s  →  the largest s that fits, per axis.
        var sx = (area.Width - spanX * gap) / (cardWidth * (spanX + 1));
        var sy = (area.Height - spanY * gap) / (cardHeight * (spanY + 1));
        var scale = Math.Clamp(Math.Min(sx, sy), 0.45, 1);
        var unitX = cardWidth * scale + gap;
        var unitY = cardHeight * scale + gap;
        var w = spanX * unitX + cardWidth * scale;
        var h = spanY * unitY + cardHeight * scale;
        var midUx = (slots.Max(s => s.Ux) + slots.Min(s => s.Ux)) / 2;
        var midUy = (slots.Max(s => s.Uy) + slots.Min(s => s.Uy)) / 2;
        var ox = w >= area.Width ? area.Center.X : Math.Clamp(cx, area.X + w / 2, area.Right - w / 2);
        var oy = h >= area.Height ? area.Center.Y : Math.Clamp(cy, area.Y + h / 2, area.Bottom - h / 2);
        var centers = slots.Select(s => new Point(ox + (s.Ux - midUx) * unitX, oy + (s.Uy - midUy) * unitY)).ToList();
        return new FanPlacement(slots, centers, scale, new Rect(ox - w / 2, oy - h / 2, w, h));
    }

    /// <summary>A small, steady tilt per place so the fan looks thrown, not printed (−2°…+2°).</summary>
    public static double Tilt(int place) => ((place * 7 + 3) % 5 - 2) * 0.9;
}
