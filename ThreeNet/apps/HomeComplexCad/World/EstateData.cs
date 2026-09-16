using System.Numerics;
using ThreeNet;

namespace HomeComplexCad.World;

public enum PropertyStatus
{
    Available,
    Reserved,
    Sold,
    Facility,
}

/// <summary>Everything the info panel shows about a building and its land.</summary>
public sealed class PropertyInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>House type, e.g. "Tipe 90/150".</summary>
    public required string Type { get; init; }

    public string Block { get; init; } = "";

    public PropertyStatus Status { get; init; } = PropertyStatus.Available;

    public float LandArea { get; init; }

    public float BuildingArea { get; init; }

    public Vector2 LotSize { get; init; }

    public int Floors { get; init; } = 1;

    public int Bedrooms { get; init; }

    public int Bathrooms { get; init; }

    public int Carports { get; init; }

    public string Certificate { get; init; } = "SHM (Sertifikat Hak Milik)";

    public string Facing { get; init; } = "Utara";

    public int PowerVa { get; init; } = 2200;

    public string Water { get; init; } = "PDAM";

    public long PriceRupiah { get; init; }

    public int YearBuilt { get; init; } = 2026;

    public string Structure { get; init; } = "Beton bertulang, dinding bata merah diplester";

    public string Roof { get; init; } = "Rangka baja ringan, genteng keramik";

    public string Flooring { get; init; } = "Granit 60x60, parket kayu di kamar";

    public string Description { get; init; } = "";

    public List<string> Features { get; init; } = [];

    /// <summary>World space centre of the lot, used by "go to" and the mini map.</summary>
    public Vector3 Position { get; set; }

    public string StatusText => Status switch
    {
        PropertyStatus.Available => "Tersedia",
        PropertyStatus.Reserved => "Dipesan",
        PropertyStatus.Sold => "Terjual",
        _ => "Fasilitas umum",
    };

    public string PriceText => PriceRupiah <= 0
        ? "-"
        : PriceRupiah >= 1_000_000_000
            ? $"Rp {PriceRupiah / 1_000_000_000d:0.##} miliar"
            : $"Rp {PriceRupiah / 1_000_000d:0} juta";
}

/// <summary>A place the camera can jump to from the locations menu.</summary>
public sealed record Location(
    string Group,
    string Name,
    Vector3 Eye,
    Vector3 Target,
    bool Interior,
    string? PropertyId = null,
    string Icon = "📍");
