using System;
using System.Collections.Generic;
using System.Linq;

namespace Drawer.Core;

// A drag uses one ordered snapshot until the folder or batch changes.
internal sealed class FolderInsertionSequence(Guid folder, IReadOnlyList<Guid> members, IReadOnlyList<Guid> batch)
{
    private readonly Guid[] _members = members.ToArray();
    public Guid Folder { get; } = folder;
    public IReadOnlyList<Guid> Batch { get; } = batch.ToArray();
    public IReadOnlyList<Guid> Candidates { get; } = members.Except(batch).ToArray();
    public int PreviewWidth => Math.Max(1, Batch.Count);

    public bool Matches(Guid id, IReadOnlyList<Guid> members, IReadOnlyList<Guid> batch) =>
        Folder == id && _members.SequenceEqual(members) && Batch.SequenceEqual(batch);

    public FolderInsertion? Hit(double x, double y, double offset, double viewportHeight,
        LayoutMetrics layout, FolderInsertion? shown = null)
    {
        if (x < 0 || y < 0 || x >= layout.CellWidth * layout.Columns || y >= viewportHeight) return null;
        double contentY = y + Math.Max(0, offset);
        int cell = (int)(contentY / layout.CellHeight) * layout.Columns + (int)(x / layout.CellWidth);
        // Map a stable projected slot back to the compact candidates.
        // Staying over the existing transparent gap preserves its anchor.
        if (shown?.Sequence == this && cell >= shown.Index)
        {
            if (cell - shown.Index < PreviewWidth) return shown;
            cell -= PreviewWidth;
        }
        bool after = layout.Columns == 1 ? contentY % layout.CellHeight >= layout.CellHeight / 2
            : x % layout.CellWidth >= layout.CellWidth / 2;
        return new(this, Math.Min(cell + (after ? 1 : 0), Candidates.Count));
    }
}

internal sealed record FolderInsertion(FolderInsertionSequence Sequence, int Index)
{
    public Guid? Before => Index > 0 ? Sequence.Candidates[Index - 1] : null;
    public Guid? After => Index < Sequence.Candidates.Count ? Sequence.Candidates[Index] : null;

    public Guid[] PreviewOrder() => Sequence.Candidates.Take(Index)
        .Concat(Sequence.Batch.Count == 0 ? new[] { Guid.Empty } : Sequence.Batch)
        .Concat(Sequence.Candidates.Skip(Index)).ToArray();

    public void Validate(Guid folder, int index, IReadOnlyList<Guid> members)
    {
        if (folder != Sequence.Folder || index != Index || Index < 0 || Index > Sequence.Candidates.Count ||
            !Sequence.Candidates.SequenceEqual(members))
            throw new InvalidOperationException(UiText.Get("FolderPlacementChanged"));
    }

    public void Insert(Guid folder, int index, List<Guid> members, IReadOnlyList<Guid> batch)
    {
        Validate(folder, index, members);
        if (Sequence.Batch.Count > 0 && !Sequence.Batch.SequenceEqual(batch))
            throw new InvalidOperationException(UiText.Get("DragOrderChanged"));
        members.InsertRange(Index, batch);
    }
}
