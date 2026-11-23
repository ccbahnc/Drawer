using System;

namespace Drawer.Core;

internal sealed class DrawerOpenHolds
{
    private int _count;
    public bool Active => _count != 0;

    public IDisposable Acquire()
    {
        _count++;
        return new Lease(this);
    }

    private sealed class Lease(DrawerOpenHolds owner) : IDisposable
    {
        private DrawerOpenHolds? _owner = owner;

        public void Dispose()
        {
            if (_owner is not { } current) return;
            _owner = null;
            current._count--;
        }
    }
}
