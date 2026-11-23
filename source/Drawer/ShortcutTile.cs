using Drawer.Core;
using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
namespace Drawer;
public sealed class ShortcutTile : INotifyPropertyChanged
{
    internal ShortcutEntry? Entry { get; private set; }
    internal ShortcutGroup? Group { get; private set; }
    internal Guid Id => Group?.Id ?? Entry?.Id ?? Guid.Empty;
    public bool IsGroup => Group != null;
    public bool IsEmpty { get; }
    internal bool RebindEntry(ShortcutEntry entry)
    {
        if (Entry is not { } previous || entry.Id != previous.Id || entry.FileName != previous.FileName || entry.Hash != previous.Hash) return false;
        Entry = entry;
        if (entry.ShownName != previous.ShownName)
        {
            Changed(nameof(Name));
            Changed(nameof(FileName));
            Changed(nameof(AccessibleName));
        }
        return true;
    }
    internal void RebindGroup(ShortcutGroup group)
    {
        string? previousName = Group?.Name;
        Group = group;
        if (group.Name == previousName) return;
        Changed(nameof(Name));
        Changed(nameof(FileName));
        Changed(nameof(AccessibleName));
    }
    public string Name => Group?.Name ?? Entry?.ShownName ?? "";
    public string? FileName => Group == null ? Entry?.DisplayName ?? Entry?.FileName : UiText.Get("FolderItemCount", Group.Name, UiText.Items(Group.Members.Count));
    public string AccessibleName => FileName ?? "";
    private bool _selectionShown, _selected;
    public Visibility SelectionVisibility => _selectionShown ? Visibility.Visible : Visibility.Collapsed;
    public string SelectionGlyph => _selected ? "✓" : "";
    public Brush SelectionBackground => DrawerPalette.Brush(_selected ? "SelectionFill" : "Surface");
    public string SelectionStatus => _selectionShown ? _selected ? UiText.Get("Selected") : UiText.Get("NotSelected") : "";
    public double SelectionSize => Math.Min(IconSize, 14);
    public double SelectionFontSize => Math.Min(10, SelectionSize * .72);
    public double IconScale { get; private set; } = 1;
    private double? _pictureSize;
    private double _pictureSizeWithMarker = double.PositiveInfinity;
    internal double NormalPictureSize => _pictureSize ?? IconSize;
    internal double PictureSize => Math.Min(NormalPictureSize,
        _selectionShown ? _pictureSizeWithMarker : double.PositiveInfinity);
    internal double RenderedPictureSize => IconScale * PictureSize;
    public double RenderedIconScale => IconSize > 0 ? RenderedPictureSize / IconSize : 1;
    public VerticalAlignment SelectionAlignment { get; private set; } = VerticalAlignment.Top;
    public Thickness SelectionMargin { get; private set; }
    internal void SetIconScale(double scale)
    {
        if (IconScale == scale) return;
        IconScale = scale;
        Changed(nameof(RenderedIconScale));
    }
    internal void SetSelection(bool shown, bool selected)
    {
        shown &= !IsEmpty;
        if (shown == _selectionShown && selected == _selected) return;
        double oldSize = PictureSize;
        _selectionShown = shown; _selected = selected;
        Changed(nameof(SelectionVisibility)); Changed(nameof(SelectionGlyph)); Changed(nameof(SelectionStatus)); Changed(nameof(SelectionBackground));
        if (PictureSize != oldSize) Changed(nameof(RenderedIconScale));
    }
    public string Glyph => IsGroup ? "\uE8B7" : GlyphFor(Entry);
    internal static string GlyphFor(ShortcutEntry? entry) => entry?.FileName.EndsWith(".url", StringComparison.OrdinalIgnoreCase) == true
        ? "\uE774" : "\uE8A5";
    public double IconSize { get; private set; }
    internal void ResizeIcon(double size)
    {
        if (IconSize == size) return;
        double oldSelectionSize = SelectionSize;
        IconSize = size;
        Changed(nameof(IconSize)); Changed(nameof(RenderedIconScale));
        if (oldSelectionSize != SelectionSize)
        { Changed(nameof(SelectionSize)); Changed(nameof(SelectionFontSize)); }
    }
    public bool ShowNames { get; }
    public Visibility NameVisibility => ShowNames ? Visibility.Visible : Visibility.Collapsed;
    private double tilePadding=5,nameSpacing=5;
    public double NameHeight { get; private set; } = 16;
    public double NameFontSize { get; private set; } = 12;
    public double NameSpacing => nameSpacing;
    public Thickness TilePadding => new(tilePadding);
    internal void ApplyMetrics(LayoutMetrics metrics, bool member = false)
    {
        double extra = member ? FolderLayout.MemberLabelExtra(metrics.CellHeight) : 0;
        double labelHeight = metrics.LabelHeight + extra;
        double labelSize = metrics.LabelFontSize * (1 + .06 * extra);
        double oldSize = PictureSize;
        _pictureSize = metrics.PictureSize(member);
        double margin = DrawerSettings.IconPictureMargin;
        double roomBeside = metrics.CellWidth / 2 - metrics.TilePadding - SelectionSize - margin;
        double roomBelow = metrics.CellHeight - 2 * metrics.TilePadding - IconSize - metrics.NameGap - labelHeight;
        bool markerBelow = !member && 2 * roomBeside < NormalPictureSize * DrawerSettings.SelectionHeldScale &&
            roomBelow >= SelectionSize + margin;
        var alignment = markerBelow ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        _pictureSizeWithMarker = member || markerBelow ? double.PositiveInfinity :
            Math.Max(1, 2 * roomBeside / DrawerSettings.SelectionHeldScale);
        var selectionMargin = member ? new Thickness(0,
            Math.Max(0, (IconSize - NormalPictureSize) / 2 - SelectionSize / 4),
            Math.Max(0, (metrics.CellWidth - NormalPictureSize) / 2 - metrics.TilePadding - SelectionSize / 2), 0)
            : new Thickness(0);
        if (!SelectionMargin.Equals(selectionMargin))
        {
            SelectionMargin = selectionMargin;
            Changed(nameof(SelectionMargin));
        }
        if (SelectionAlignment != alignment)
        {
            SelectionAlignment = alignment;
            Changed(nameof(SelectionAlignment));
        }
        if (PictureSize != oldSize) Changed(nameof(RenderedIconScale));
        if (tilePadding == metrics.TilePadding && nameSpacing == metrics.NameGap &&
            NameHeight == labelHeight && NameFontSize == labelSize) return;
        bool paddingChanged = tilePadding != metrics.TilePadding, spacingChanged = nameSpacing != metrics.NameGap;
        if (paddingChanged) { tilePadding = metrics.TilePadding; Changed(nameof(TilePadding)); }
        if (spacingChanged) { nameSpacing = metrics.NameGap; Changed(nameof(NameSpacing)); }
        if (NameHeight != labelHeight) { NameHeight = labelHeight; Changed(nameof(NameHeight)); }
        if (NameFontSize != labelSize) { NameFontSize = labelSize; Changed(nameof(NameFontSize)); }
    }
    public double NameOpacity => ShowNames ? 1 : 0;
    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; internal set { if (ReferenceEquals(_icon, value)) return; _icon = value; Changed(nameof(Icon)); Changed(nameof(FallbackVisibility)); } }
    public Visibility FallbackVisibility => !IsEmpty && !IsGroup && Icon == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ContentVisibility => IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this,new(name));
    internal void RefreshLanguage()
    {
        Changed(nameof(FileName));
        Changed(nameof(AccessibleName));
        Changed(nameof(SelectionStatus));
    }
    internal ShortcutTile(ShortcutEntry entry, double iconSize = 38, bool showNames = true) { Entry = entry; IconSize = iconSize; ShowNames = showNames; }
    internal ShortcutTile(ShortcutGroup group, double iconSize, bool showNames = true) { Group = group; IconSize = iconSize; ShowNames = showNames; }
    internal ShortcutTile() { IsEmpty = true; }
}
