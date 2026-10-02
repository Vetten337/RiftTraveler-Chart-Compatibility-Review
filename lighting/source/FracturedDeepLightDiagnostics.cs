using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace RiftTraveler;

/// <summary>Read-only light inspection. Does not relight or replace blocks.</summary>
internal static class FracturedDeepLightDiagnostics
{
    internal static string Inspect(IWorldAccessor world, BlockPos position)
    {
        IBlockAccessor accessor = world.BlockAccessor;
        Block block = accessor.GetBlock(position);
        byte[] hsv = block.GetLightHsv(accessor, position);
        var chunk = accessor.GetChunkAtBlockPos(position);
        int index = ((position.Y % 32) * 32 + position.Z % 32) * 32 + position.X % 32;
        bool registered = chunk?.LightPositions?.Contains(index) == true;
        string entity = accessor.GetBlockEntity(position)?.GetType().Name ?? "none";
        StringBuilder report = new();
        report.AppendLine($"Light inspection: {block.Code}, dim={position.dimension}, XYZ={position.X},{position.Y},{position.Z}");
        report.AppendLine($"Block entity={entity}; emitted HSV={(hsv == null ? "none" : string.Join(",", hsv))}; registered source={registered}");
        foreach(var sample in new[]
        {
            ("source", position.Copy()),
            ("above", position.Copy().Add(0, 1, 0)),
            ("east", position.Copy().Add(1, 0, 0)),
            ("north", position.Copy().Add(0, 0, -1))
        })
            report.AppendLine($"{sample.Item1}: stored block={accessor.GetLightLevel(sample.Item2, EnumLightLevelType.OnlyBlockLight)}, sun={accessor.GetLightLevel(sample.Item2, EnumLightLevelType.OnlySunLight)}");
        return report.ToString().TrimEnd();
    }
}
