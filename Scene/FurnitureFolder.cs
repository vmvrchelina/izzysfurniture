using System;

namespace IzzysFurniture;

internal sealed class FurnitureFolder
{
    public FurnitureFolder(string name)
    {
        this.Id = Guid.NewGuid();
        this.Name = name;
    }

    public FurnitureFolder(Guid id, string name, bool isOpen)
    {
        this.Id = id;
        this.Name = name;
        this.IsOpen = isOpen;
    }

    public Guid Id { get; }
    public string Name { get; set; }
    public bool IsOpen { get; set; } = true;
}
