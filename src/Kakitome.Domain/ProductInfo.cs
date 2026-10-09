namespace Kakitome.Domain;

/// <summary>Stable product identity shared by every layer (see ADR-016).</summary>
public static class ProductInfo
{
    public const string Name = "Kakitome";

    /// <summary>Folder name used under user-visible roots such as Documents.</summary>
    public const string FolderName = "Kakitome";
}
