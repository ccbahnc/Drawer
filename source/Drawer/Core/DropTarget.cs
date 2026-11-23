using System;

namespace Drawer.Core;

internal enum DropKind { Place, Swap, Insert, Extract, Copy, MergePending, Merge, Append, Members, CopyMembers }

// Index is absolute on top-level pages and relative to the remaining
// members inside a folder. Receiver is a stable ID, never a visual control.
internal sealed record DropTarget(Guid Drawer, int Page, DropKind Kind, int Index, Guid? Receiver = null,
    FolderInsertion? MemberPlacement = null)
{
    public (int Rows, int Columns, int DisplayRevision, int Cell)? TopLayout { get; init; }
    public bool Ready => Kind != DropKind.MergePending;
    public bool MatchesRelease(DropTarget? release) => Ready && this == release;
    public bool MatchesMemberSurface(DropTarget? release) =>
        release != null && Drawer == release.Drawer && Page == release.Page && Kind == release.Kind &&
        Receiver == release.Receiver && MemberPlacement != null &&
        MemberPlacement.Sequence == release.MemberPlacement?.Sequence;
    public ItemDrop Placement => new(
        Kind is DropKind.Append or DropKind.Members or DropKind.CopyMembers ? Receiver : null,
        Kind == DropKind.Merge ? Receiver : null, Index, MemberPlacement: MemberPlacement);
}
