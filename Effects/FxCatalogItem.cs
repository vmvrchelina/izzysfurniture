namespace IzzysFurniture;

internal sealed class FxCatalogItem
{
    public FxCatalogItem(string name, string path, string category)
    {
        Name = name;
        Path = path;
        Category = category;
    }

    public string Name { get; }
    public string Path { get; }
    public string Category { get; }
}
