# Lighting test guide

These are source snapshots from the existing Rift Traveler Atlas project.
They are **not independently runnable**: the full mod, its registered systems,
assets, world setup, and game installation references are required. Adapt the
fixtures to an upstream harness before running them there.

| File | What it establishes |
| --- | --- |
| `VanillaDimensionLightBaselineScenarios.cs` | Known unpatched custom-dimension small-lantern propagation failure, with torch/overworld controls |
| `NativeLightTestBinding.cs` | Reflection wrapper which instantiates the actual runtime candidate and reads its counters |
| `QueuedDimensionLightPrototypeScenarios.cs` | Actual runtime candidate through the native server queue: lantern placement, lining change, overlap, removal |
| `ClientDimensionLightScenarios.cs` | Independent native client chunks/buffers and removal handler; client/server patch ownership |
| `DimensionLightPacketScenarios.cs` | Baseline malformed removal packet and runtime correction, serialization, handling, controls, disposal |
| `DimensionLightLifecycleScenarios.cs` | Second Manifold dimension, absent overworld map chunk, chunk-edge light, controlled disk save/unload/reload |
| `NativeLightingStartupScenarios.cs` | Enabled config at startup, legacy writer absent, native lantern and seamstone updates |
| `Fixtures/NativeLighting/RiftTraveler.DimensionLighting.json` | Enabled startup scenario configuration |

Some filenames retain “Prototype”, and some files contain historical helper
classes. The runtime assertions use `NativeLightTestBinding`; those historical
helpers are investigation history or observers, **not additional modules to
install with the candidate**. Baseline tests deliberately exercise known broken
engine behavior; a passing baseline means it reproduced the defect.

The original project uses Pixnop.Atlas.XUnit 0.15.1, xUnit 2.9.3,
Microsoft.NET.Test.Sdk 17.14.1, .NET 10, and an installed Vintage Story 1.22.7.
It stages the complete Rift Traveler folder mod and official Manifold 0.6.0 ZIP,
and disables test parallelization. The startup scenario maps its fixture to
ModConfig through `AtlasDataFiles`. The full project also needs to copy those
fixtures to its test output directory.

The module and consumer themselves are in `../source`. Full Rift Traveler
fixtures referenced here include `FracturedDeepSystem` and the disabled legacy
writer; those dependencies are not supplied in this review package.

Known coverage limits: headless client lighting is drained by the fixture, not
the real render loop; packet sink tests do not exercise network latency; the
controlled native unload is not natural distance-based eviction or a process
restart. Real client/server acceptance remains necessary.
