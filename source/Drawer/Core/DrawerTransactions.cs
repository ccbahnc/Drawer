using System;
using System.Linq;
namespace Drawer.Core;

// Stable identity allocated once. Hover state never mutates the confirmed catalog.
internal sealed class DrawerCreation
{
    public DrawerDefinition Definition {get;private set;}
    public bool Confirmed {get;private set;}
    public DrawerCreation(DrawerDefinition draft)=>Definition=draft;
    public DrawerCollection Propose(DrawerCollection saved,int rows,int columns)
    {
        if(!LayoutSelection.Valid(rows,columns))throw new ArgumentException(UiText.Get("LayoutRange"));
        var definition=Definition with{Rows=rows,Columns=columns};
        return saved with{Drawers=Confirmed?saved.Drawers.Select(d=>d.Id==definition.Id?definition:d).ToList():saved.Drawers.Append(definition).ToList()};
    }
    public void Accept(int rows,int columns){Definition=Definition with{Rows=rows,Columns=columns};Confirmed=true;}
}

// Use the committed layout at drag start so preview motion cannot change a boundary.
internal sealed class DrawerInsertion
{
    private DrawerEdge? edge;
    private int rank;
    public int Find(DrawerCollection original, Guid source, DrawerEdge target, double coordinate,
        Arrangement layout, double scale)
    {
        var others = original.Drawers.Where(d => d.Edge == target && d.Id != source).OrderBy(d => d.Order).ToArray();
        var placed = others.Select((drawer, index) => (drawer, index)).Where(p => layout.Items.ContainsKey(p.drawer.Id))
            .Select(p =>
            {
                var rect = layout.Items[p.drawer.Id].Geometry.Drawer;
                return (p.index, Center: target == DrawerEdge.Top ? (rect.Left + rect.Right) / 2d : (rect.Top + rect.Bottom) / 2d);
            }).ToArray();
        int proposed = placed.Count(p => coordinate >= p.Center);
        if (edge == target && proposed != rank)
        {
            double boundary = placed[proposed > rank ? rank : rank - 1].Center;
            if (Math.Abs(coordinate - boundary) < 6 * scale) proposed = rank;
        }
        edge = target;
        rank = proposed;
        return rank < placed.Length ? placed[rank].index : others.Length;
    }
    public void Clear() { edge = null; rank = 0; }
}
