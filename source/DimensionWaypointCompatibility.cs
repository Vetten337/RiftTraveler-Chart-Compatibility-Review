using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace RiftTraveler;

/// <summary>
/// The map-click Add Waypoint dialog supplies a local Y to /waypoint addati.
/// Chart filters vanilla waypoints by the dimension encoded in their internal Y.
/// Fix the new-dialog position before its normal command is sent; leave vanilla
/// command handling, existing markers, edits, and Chart itself untouched.
/// </summary>
public sealed class DimensionWaypointCompatibility : ModSystem
{
    private const string HarmonyId = "rifttraveler.dimension-waypoint-creation";
    private Harmony harmony;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        if(!api.ModLoader.IsModEnabled("manifold")) return;
        try
        {
            MethodInfo setter = AccessTools.PropertySetter(
                typeof(GuiDialogAddWayPoint), nameof(GuiDialogAddWayPoint.WorldPos))
                ?? throw new MissingMethodException(
                    typeof(GuiDialogAddWayPoint).FullName, "set_WorldPos");
            // The inherited API field is intentionally read through Harmony's
            // field injection. Check it before patching so API drift fails safely.
            if(AccessTools.Field(typeof(GuiDialogAddWayPoint), "capi") == null)
                throw new MissingFieldException(typeof(GuiDialogAddWayPoint).FullName, "capi");
            harmony = new Harmony(HarmonyId);
            harmony.Patch(setter, prefix: new HarmonyMethod(
                typeof(DimensionWaypointCompatibility), nameof(BeforePositionAssigned)));
            api.Logger.Notification("[RiftTraveler] Dimension-aware map marker creation installed.");
        }
        catch(Exception exception)
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            api.Logger.Warning("[RiftTraveler] Dimension waypoint adapter skipped: {0}", exception.Message);
        }
    }

    private static void BeforePositionAssigned(ref Vec3d __0, ICoreClientAPI ___capi)
    {
        int dimension = ___capi?.World?.Player?.Entity?.Pos?.Dimension ?? 0;
        __0 = EncodeNewMarkerPosition(__0, dimension);
    }

    // Clone only corrected positions: map-owned coordinate objects must not be
    // modified in place. Already encoded positions are retained, making this
    // harmless if the game or another adapter has already fixed the position.
    internal static Vec3d EncodeNewMarkerPosition(Vec3d position, int dimension)
    {
        if(position == null || dimension <= 0 ||
            !double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
            !double.IsFinite(position.Z) || position.Y < 0 ||
            position.Y >= BlockPos.DimensionBoundary)
            return position;

        Vec3d encoded = position.Clone();
        encoded.Y += (double)dimension * BlockPos.DimensionBoundary;
        return encoded;
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        base.Dispose();
    }
}
