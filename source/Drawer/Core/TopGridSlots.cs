using System;
using System.Collections.Generic;
using System.Linq;

namespace Drawer.Core;

// Empty is an internal cell marker. The library serializes it as JSON null.
internal static class TopGridSlots
{
    internal static void Vacate(List<Guid> slots, IReadOnlySet<Guid> ids)
    {
        for (int i = 0; i < slots.Count; i++)
            if (ids.Contains(slots[i])) slots[i] = Guid.Empty;
    }

    internal static void Insert(List<Guid> slots, int target, IReadOnlyList<Guid> batch)
    {
        if (target < 0) throw new ArgumentOutOfRangeException(nameof(target));
        while (slots.Count < target) slots.Add(Guid.Empty);

        // Insert consecutively. Each step consumes the nearest hole to its right.
        foreach (var id in batch)
        {
            int empty = slots.IndexOf(Guid.Empty, target);
            if (empty < 0)
            {
                empty = slots.Count;
                slots.Add(Guid.Empty);
            }
            for (int i = empty; i > target; i--) slots[i] = slots[i - 1];
            slots[target++] = id;
        }
    }

    internal static int PageCount(int recorded, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recorded);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        return recorded == 0 ? 1 : (recorded - 1) / capacity + 1;
    }

    internal static int[] ReclaimVacatedPages(List<Guid> slots, IEnumerable<int> sourceCells, int capacity)
    {
        if (capacity <= 0) return Array.Empty<int>();
        var empty = sourceCells.Select(cell => cell / capacity).Distinct().OrderBy(page => page)
            .Where(page => slots.Skip(page * capacity).Take(capacity).All(id => id == Guid.Empty)).ToArray();
        for (int i = empty.Length - 1; i >= 0; i--)
        {
            int start = empty[i] * capacity;
            if (start < slots.Count) slots.RemoveRange(start, Math.Min(capacity, slots.Count - start));
        }
        return empty;
    }

    internal static int RemapPage(int page, IReadOnlyList<int> removed) => page - removed.Count(p => p < page);

    internal static Guid[] PreviewInsert(IEnumerable<Guid> slots, int target)
    {
        var preview = slots.ToList();
        Insert(preview, target, new[] { Guid.Empty });
        return preview.ToArray();
    }

    // A local move has already vacated its source slots. The landing range
    // owns any source cells it overlaps.
    internal static void Place(List<Guid> slots, IReadOnlyList<int> sourceCells, int start, IReadOnlyList<Guid> batch)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        int end = checked(start + batch.Count);
        while (slots.Count < end) slots.Add(Guid.Empty);
        var displaced = new Queue<Guid>(slots.GetRange(start, batch.Count).Where(id => id != Guid.Empty));
        for (int i = 0; i < batch.Count; i++) slots[start + i] = batch[i];

        foreach (int cell in sourceCells)
        {
            if (displaced.Count == 0) break;
            if (cell >= start && cell < end) continue;
            slots[cell] = displaced.Dequeue();
        }

        // Folder members have no top-level source cell. Keep displaced
        // projects at the destination and use its next free cells.
        int next = end;
        while (displaced.TryDequeue(out Guid id))
        {
            int empty = slots.IndexOf(Guid.Empty, next);
            if (empty < 0) slots.Add(id);
            else slots[empty] = id;
            next = empty < 0 ? slots.Count : empty + 1;
        }
    }

    internal static void InsertPaged(List<Guid> slots, int target, IReadOnlyList<Guid> batch, int capacity)
    {
        if (target < 0 || capacity < 1) throw new ArgumentOutOfRangeException();
        if (batch.Count == 0) return;
        int pageStart = target / capacity * capacity;
        int cell = target % capacity;
        var carry = batch.ToList();
        while (carry.Count != 0)
        {
            var page = new List<Guid>(capacity);
            for (int i = 0; i < capacity; i++)
                page.Add(pageStart + i < slots.Count ? slots[pageStart + i] : Guid.Empty);

            // If a batch reaches the end, use space before its start as well.
            int before = page.Take(cell).Count(id => id == Guid.Empty);
            cell -= Math.Min(before, Math.Max(0, carry.Count - (capacity - cell)));
            int take = Math.Min(carry.Count, capacity - cell);

            // Removing right-hand holes pulls the intervening items left in
            // the buffer; inserting at the same cell pushes them back once.
            int holes = take;
            for (int i = cell; i < page.Count && holes > 0;)
            {
                if (page[i] == Guid.Empty) { page.RemoveAt(i); holes--; }
                else i++;
            }
            for (int i = cell - 1; i >= 0 && holes > 0; i--)
            {
                if (page[i] != Guid.Empty) continue;
                page.RemoveAt(i);
                holes--;
            }
            page.InsertRange(cell, carry.Take(take));
            carry.RemoveRange(0, take);
            if (page.Count > capacity) carry.AddRange(page.Skip(capacity));

            // Display-only trailing cells don't become records. Previously
            // recorded holes, including an emptied page, stay recorded.
            int used = -1;
            for (int i = capacity - 1; i >= 0; i--)
            {
                if (page[i] == Guid.Empty) continue;
                used = i;
                break;
            }
            while (slots.Count <= pageStart + used) slots.Add(Guid.Empty);
            for (int i = 0; i < capacity && pageStart + i < slots.Count; i++)
                slots[pageStart + i] = page[i];
            pageStart += capacity;
            cell = 0;
        }
    }

    internal static List<Guid> Occupied(IEnumerable<Guid> slots) => slots.Where(id => id != Guid.Empty).ToList();
}
