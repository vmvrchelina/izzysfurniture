using System.Numerics;

namespace IzzysFurniture;

internal sealed class NpcPatrolPoint
{
    public NpcPatrolPoint()
    {
    }

    public NpcPatrolPoint(Vector3 position)
        => this.Position = position;

    public Vector3 Position { get; set; }
}
