using System;
using System.Linq;
namespace Drawer.Core;

// Presentation-only transaction. The store is untouched until release.
internal sealed class DragPreview
{
    private readonly Guid[] _batch;
    private readonly Guid[] _remaining;
    private readonly System.Collections.Generic.HashSet<Guid> _selected;
    private readonly bool _fixedCells;
    private readonly bool _insertIntoDrawer;
    private readonly int[] _sourceCells;
    public Guid[] Original { get; }
    public Guid[] Order { get; private set; }
    public bool Valid { get; private set; }
    public bool IsCompact { get; private set; }
    public int Index { get; private set; }
    public Guid[] VisualSlots { get; private set; }
    public Guid? HiddenItem { get; private set; }
    private int? _visualGap;
    private FolderInsertion? _memberLanding;

    public DragPreview(Guid[] ids, Guid source) : this(ids, new[] { source }) { }

    public DragPreview(Guid[] ids, Guid[] batch, bool fixedCells = false, int[]? sourceCells = null, bool insertIntoDrawer = false)
    {
        _selected = batch.ToHashSet();
        var occupied = TopGridSlots.Occupied(ids);
        if (batch.Length == 0 || _selected.Count != batch.Length || occupied.Distinct().Count() != occupied.Count ||
            (!fixedCells && occupied.Count != ids.Length) || _selected.Contains(Guid.Empty)) throw new ArgumentException(UiText.Get("InvalidDragData"));
        _fixedCells = fixedCells;
        _insertIntoDrawer = insertIntoDrawer;
        _batch = batch.ToArray();
        Original = ids.ToArray();
        _sourceCells = sourceCells ?? Enumerable.Range(0, ids.Length).Where(i => _selected.Contains(ids[i])).ToArray();
        _remaining = fixedCells
            ? Original.Select(id => _selected.Contains(id) ? Guid.Empty : id).ToArray()
            : Original.Where(id => !_selected.Contains(id)).ToArray();
        Order = VisualSlots = Original;
    }

    public void Compact()
    {
        Order = _remaining;
        VisualSlots = Order;
        HiddenItem = null;
        _visualGap = null;
        _memberLanding = null;
        IsCompact = true;
        Valid = true;
    }

    // A drag without an insertion target still hides and compacts its source members.
    public void ClearLanding()
    {
        if (_fixedCells) Cancel();
        else Compact();
    }

    // Keep source holes at their physical indices. The rest of the view makes
    // room for one slot, regardless of how many items the drop will insert.
    public void PreviewInsertion(int gap)
    {
        if (gap < 0 || !_fixedCells) { Cancel(); return; }
        if (Valid && !IsCompact && _visualGap == gap) return;
        Index = gap;
        var slots = _remaining.ToArray();
        HiddenItem = null;
        if (_insertIntoDrawer) slots = TopGridSlots.PreviewInsert(slots, gap);
        else if (_sourceCells.Length == 0)
        {
            // A member leaving a folder has no top-level source cell.
            var visible = slots.ToList();
            TopGridSlots.Place(visible, _sourceCells, gap, new[] { Guid.Empty });
            slots = visible.ToArray();
        }
        else if (gap < slots.Length && slots[gap] != Guid.Empty)
        {
            Guid occupant = slots[gap];
            slots[gap] = Guid.Empty;
            int refill = Array.FindIndex(_sourceCells, cell => cell < gap || cell - gap >= _batch.Length);
            if (refill >= 0) slots[_sourceCells[refill]] = occupant;
            else HiddenItem = occupant;
        }
        Order = VisualSlots = slots;
        IsCompact = false;
        Valid = true;
        _visualGap = gap;
    }

    public void PreviewMembers(FolderInsertion landing)
    {
        if (Valid && !IsCompact && _memberLanding == landing) return;
        Index = landing.Index;
        Order = VisualSlots = landing.PreviewOrder();
        _memberLanding = landing;
        HiddenItem = null;
        IsCompact = false;
        Valid = true;
    }

    public void Cancel()
    {
        Order = Original;
        VisualSlots = Original;
        HiddenItem = null;
        _visualGap = null;
        _memberLanding = null;
        IsCompact = false;
        Valid = false;
    }
}
internal sealed class SlotMotion
{
    public double X,Y,Vx,Vy,TargetX,TargetY;
    public bool IsMoving => X != TargetX || Y != TargetY || Vx != 0 || Vy != 0;
    public SlotMotion(double x,double y) { X=TargetX=x; Y=TargetY=y; }
    public void Step(double dt)
    {
        double t=Math.Clamp(dt,0,.05), w=DrawerSettings.ReorderSpring, factor=Math.Exp(-w*t);
        void Axis(ref double value,ref double velocity,double target) { double offset=value-target,c=velocity+w*offset; value=target+(offset+c*t)*factor; velocity=(velocity-w*c*t)*factor; if(Math.Abs(value-target)<.05 && Math.Abs(velocity)<.1) {value=target;velocity=0;} }
        Axis(ref X,ref Vx,TargetX); Axis(ref Y,ref Vy,TargetY);
    }
}
