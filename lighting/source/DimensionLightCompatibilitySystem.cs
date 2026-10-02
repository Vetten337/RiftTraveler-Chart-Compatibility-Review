using System;
using System.Linq;
using HarmonyLib;
using Manifold.Api.Client;
using Manifold.Api.Helpers;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Common;

namespace RiftTraveler;

public sealed class DimensionLightingConfig
{
    public bool Enabled { get; set; } = false;
    public bool CorrectInventoryLookups { get; set; } = true;
    public bool CorrectRemovalPackets { get; set; } = true;
    public bool CorrectMissingMapDispatch { get; set; } = true;
}

// Rift Traveler is the first consumer. Only this integration knows the FD code.
public sealed class DimensionLightCompatibilitySystem : ModSystem
{
    internal const string ConfigFile = "RiftTraveler.DimensionLighting.json";
    private DimensionLightCompatibility binding;
    private ICoreAPI api;
    private IManifoldClient clientManifold;
    private long clientListener;
    private int boundDimension = -1;
    private DimensionLightingConfig config;
    internal bool IsServerActive => api?.Side == EnumAppSide.Server && binding != null;
    public override double ExecuteOrder() => 0.59;

    private bool LoadConfig(ICoreAPI api)
    {
        this.api = api;
        try
        {
            config = api.LoadModConfig<DimensionLightingConfig>(ConfigFile) ?? new DimensionLightingConfig();
            return config.Enabled;
        }
        catch(Exception error)
        {
            api.Logger.Warning("[RiftTraveler] Native dimension lighting config unavailable; candidate disabled: {0}", error.Message);
            return false;
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        if(!LoadConfig(api)) return;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => {
            var deep = api.ModLoader.GetModSystem<FracturedDeepSystem>();
            if(deep?.IsAvailable == true) Bind(deep.DimensionId);
        });
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        if(!LoadConfig(api)) return;
        clientManifold = api.GetManifoldClient();
        clientListener = api.Event.RegisterGameTickListener(_ => RefreshClientBinding(), 250);
        api.Event.LeaveWorld += OnLeaveWorld;
        RefreshClientBinding();
    }

    private void RefreshClientBinding()
    {
        int dimension = clientManifold.Dimensions.FirstOrDefault(d => d.Code.Equals(FracturedDeepSystem.DimensionCode))?.InternalId ?? -1;
        if(dimension == boundDimension) return;
        binding?.Dispose();
        binding = null;
        boundDimension = -1;
        if(dimension > 0) Bind(dimension);
    }

    private void Bind(int dimension)
    {
        try
        {
            var map = AccessTools.Field(api.World.GetType(), "WorldMap")?.GetValue(api.World) as WorldMap
                ?? throw new MissingFieldException("WorldMap");
            binding = new DimensionLightCompatibility(map, dimension, config);
            boundDimension = dimension;
            api.Logger.Notification("[RiftTraveler] Native dimension lighting active on {0}, dimension {1}; no repair scans or chunk resends.", api.Side, dimension);
        }
        catch(Exception error)
        {
            boundDimension = dimension; // One clear failure, not a repeated patch attempt every tick.
            api.Logger.Error("[RiftTraveler] Native dimension lighting candidate unavailable on {0}: {1}", api.Side, error);
        }
    }

    private void OnLeaveWorld()
    {
        if(clientListener != 0 && api is ICoreClientAPI client)
            client.Event.UnregisterGameTickListener(clientListener);
        clientListener = 0;
        binding?.Dispose();
        binding = null;
        boundDimension = -1;
    }

    public override void Dispose()
    {
        if(api is ICoreClientAPI client)
        {
            if(clientListener != 0) client.Event.UnregisterGameTickListener(clientListener);
            client.Event.LeaveWorld -= OnLeaveWorld;
        }
        clientListener = 0;
        OnLeaveWorld();
        base.Dispose();
    }
}
