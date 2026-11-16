using TerrainTool.Core.Theater;

namespace TerrainTool.Core.NewTerrain;

public enum Producer { Tool, TerrainBuilder, Manual, CopyFromKorea, Unknown }

/// <summary>Un file o una cartella attesi in NewTerrain.</summary>
public sealed class SpecEntry
{
    public required string RelPath { get; init; }
    public bool IsDirectory { get; init; }
    public required string Description { get; init; }
    public Producer ProducedBy { get; init; }
    /// <summary>Dimensione attesa in byte calcolata dai parametri del teatro; null se non nota.</summary>
    public Func<TheaterConfig, long?> ExpectedSize { get; init; } = _ => null;
    /// <summary>Se true la dimensione non dipende dal teatro: si confronta con quella della Korea.</summary>
    public bool SameSizeAsKorea { get; init; }
    public bool Optional { get; init; }
}

/// <summary>
/// Struttura di NewTerrain ricavata dalla Korea di BMS 4.38 (KTO 1024 km, mappa 32768 px).
/// Le dimensioni con formula sono verificate sulla Korea; le altre si confrontano solo per presenza.
/// </summary>
public static class NewTerrainSpec
{
    private static long Sq(long n, long bytesPerPx) => n * n * bytesPerPx;
    private const long DdsHeader = 128;

    public static IReadOnlyList<SpecEntry> Entries(TheaterConfig cfg) => new List<SpecEntry>
    {
        new() { RelPath = "Theater.txt", Description = "Theater parameters and projection", ProducedBy = Producer.Tool },
        new() { RelPath = "TerrainFractalNoise_nsrgb.dds", Description = "Terrain fractal noise (generic)", ProducedBy = Producer.CopyFromKorea, SameSizeAsKorea = true },
        new() { RelPath = "Foam.dds", Description = "Foam texture (generic)", ProducedBy = Producer.CopyFromKorea, SameSizeAsKorea = true },
        new() { RelPath = "ArtTextures", IsDirectory = true, Description = "Detail textures Diffuse/Normals", ProducedBy = Producer.CopyFromKorea },

        new() { RelPath = @"HeightMaps\HeightMap.raw", Description = "Heights int16 (feet), side = map size", ProducedBy = Producer.TerrainBuilder,
                ExpectedSize = c => Sq(c.MapSizePixels, 2) },
        new() { RelPath = @"HeightMaps\WaterHeightMap.raw", Description = "Water heights int16, 250 m/px", ProducedBy = Producer.TerrainBuilder,
                ExpectedSize = c => Sq(c.Size250m, 2) },
        new() { RelPath = @"HeightMaps\LOS250.raw", Description = "Line of sight, 250 m/px", ProducedBy = Producer.Unknown,
                ExpectedSize = c => Sq(c.Size250m, 2) },
        new() { RelPath = @"HeightMaps\LOSMin.raw", Description = "LOS minimum (Korea 256²×2)", ProducedBy = Producer.Unknown },
        new() { RelPath = @"HeightMaps\LOSMax.raw", Description = "LOS maximum (Korea 256²×2)", ProducedBy = Producer.Unknown },
        new() { RelPath = @"HeightMaps\TerrainLevelingData.bin", Description = "Leveling (runways/objects). If missing: crash in TryLoadTerrainLevelDataFromFile", ProducedBy = Producer.Unknown },
        new() { RelPath = @"HeightMaps\SpecialPolygonData.binpb", Description = "Special polygons (protobuf)", ProducedBy = Producer.Unknown },
        new() { RelPath = @"HeightMaps\LandCoverData.binpb", Description = "Land cover data (protobuf)", ProducedBy = Producer.Unknown },
        new() { RelPath = $@"HeightMaps\{cfg.Name}.aef", Description = "Per-objective data (unknown format)", ProducedBy = Producer.Unknown, Optional = true },

        new() { RelPath = @"GroundTypes\LandCoverMap.raw", Description = "Land cover classes 8-bit (0–19), side = map size", ProducedBy = Producer.TerrainBuilder,
                ExpectedSize = c => Sq(c.MapSizePixels, 1) },
        new() { RelPath = @"GroundTypes\LandCoverTypes.txt", Description = "Class mapping (proto text)", ProducedBy = Producer.TerrainBuilder },
        new() { RelPath = @"GroundTypes\Types.txt", Description = "Ground types", ProducedBy = Producer.CopyFromKorea },
        new() { RelPath = @"GroundTypes\GlobalAlpha_nsrgb.dds", Description = "R=255, G=GT, B=GlobalAlpha_Blue (Korea 16384² DXT1)", ProducedBy = Producer.Tool },
        new() { RelPath = @"GroundTypes\SnowMapWinter.raw", Description = "Snow winter", ProducedBy = Producer.Unknown },
        new() { RelPath = @"GroundTypes\SnowMapSpring.raw", Description = "Snow spring", ProducedBy = Producer.Unknown },
        new() { RelPath = @"GroundTypes\SnowMapSummer.raw", Description = "Snow summer", ProducedBy = Producer.Unknown },
        new() { RelPath = @"GroundTypes\SnowMapFall.raw", Description = "Snow fall", ProducedBy = Producer.Unknown },

        new() { RelPath = @"Trees\Maps\ForestMap.raw", Description = "Forest 8-bit (4 MSB density, 4 LSB type), side = map/2", ProducedBy = Producer.TerrainBuilder,
                ExpectedSize = c => Sq(c.MapSizePixels / 2, 1) },
        new() { RelPath = @"Trees\Maps\ForestTypes.txt", Description = "Forest types (proto text)", ProducedBy = Producer.TerrainBuilder },
        new() { RelPath = @"Trees\Art", IsDirectory = true, Description = "Tree/bush/rock textures", ProducedBy = Producer.CopyFromKorea },

        new() { RelPath = @"WaterMaps\ColorMap.dds", Description = "Water color BC7", ProducedBy = Producer.Tool },
        new() { RelPath = @"WaterMaps\DepthMap_nsrgb.dds", Description = "Depth R/G/B, DXT1 no mipmaps (manual says DepthMap.dds)", ProducedBy = Producer.Tool },
        new() { RelPath = @"WaterMaps\CullingMap_nsrgb.dds", Description = "Culling DXT1 (manual says CullingMap.dds)", ProducedBy = Producer.Tool },
        new() { RelPath = @"WaterMaps\FFTMap.raw", Description = "Wave FFT map", ProducedBy = Producer.TerrainBuilder },
        new() { RelPath = @"WaterMaps\WavesDirectionMap.raw", Description = "Wave direction", ProducedBy = Producer.Unknown },
        new() { RelPath = @"WaterMaps\WavesDirectionMapXY.raw", Description = "Wave direction XY", ProducedBy = Producer.Unknown },
        new() { RelPath = @"WaterMaps\UnderWaterColor.dds", Description = "Underwater color (generic)", ProducedBy = Producer.CopyFromKorea, SameSizeAsKorea = true },

        new() { RelPath = @"Photoreal\GlobalColorMap.dds", Description = "Global color BC7 (Korea 8192² with mips)", ProducedBy = Producer.Tool },
        new() { RelPath = @"Photoreal\4K", IsDirectory = true, Description = "Photoreal 4K", ProducedBy = Producer.Tool },
        new() { RelPath = @"Photoreal\Night4K", IsDirectory = true, Description = "Photoreal night 4K", ProducedBy = Producer.Tool },
        new() { RelPath = @"Photoreal\Hires", IsDirectory = true, Description = "Photoreal high resolution (numbered tiles)",ProducedBy = Producer.Tool, Optional = true },
        new() { RelPath = @"Photoreal\16K", IsDirectory = true, Description = "Photoreal 16K", ProducedBy = Producer.Tool, Optional = true },
    };

    public static string ProducerLabel(Producer p) => p switch
    {
        Producer.Tool => "This tool",
        Producer.TerrainBuilder => "Terrain Builder",
        Producer.Manual => "Manual",
        Producer.CopyFromKorea => "Generic copy",
        _ => "To investigate",
    };
}
