# Reproducing and validating the findings

## Environment

- Vintage Story 1.22.7, Windows client.
- Official Chart 0.3.0 and Manifold 0.6.0.
- Rift Traveler 0.7.4 development build with a roofed Fractured Deep.
- Dimension declares `chartScanTopY = 111`; its dark-sky roof is at 112.
- Other mods were present during the reported client tests. We have not yet
  isolated every observation to a minimal client mod list.

The original save, player data, full modpack, binaries, and raw logs are not
published here. A separate test world should be used; no destructive purge is
required. Versions above describe the tested setup, not the latest releases.

## Database cleanup

1. Start a fresh client process with official Chart 0.3.0 and a test save.
2. Load the world and open the map/minimap.
3. Save and return to the main menu without exiting the client.
4. Reopen the same save and open the map/minimap.
5. Inspect startup errors before any ComposeDialog exception.

The observed error chain, with user paths and save ID removed, was:

```text
Mod exception during event LevelFinalize
System.IO.IOException: Cannot open worldmap database file
  <data>/Maps/<save-id>.db, it seems to be not writable!
  at SQLiteDBConnection.OpenOrCreate(...)
  at ChunkMapLayer..ctor(...)
  at WorldMapManager.OnLvlFinalize()

[Chart] Vanilla ChunkMapLayer not found - may render on top.
[Chart] DimensionAwareWaypointMapLayer was not instantiated ...

System.ArgumentOutOfRangeException: Index was out of range.
  at GuiDialogWorldMap.ComposeDialog(...)
```

These are abbreviated diagnostic excerpts, not a complete log or a claim that
another mod actually replaced the waypoint layer. The latter warning appeared
after initialization had already failed.

Review the removal callback and vanilla's LeaveWorld handler to confirm the
detached layer no longer receives OnShutDown. With the adapter enabled, repeat
the sequence several times. Our client log records:

```text
[RiftTraveler] Released detached vanilla map database.
```

The user reports repeated reloads without crashes. The real SQLite Atlas test
is narrower: it proves an open MapDB blocks the engine's write-access preflight,
and the adapter's cleanup permits reopening while leaving active DBs alone.
It does not automate the entire menu lifecycle or graphics.

## New terrain mapping: hypothesis requiring confirmation

1. Enter a custom dimension and explore beyond its initially mapped area.
2. Confirm terrain is loaded and visible, but the map adds no tiles.
3. At a failing column, inspect `ProcessChunk`'s initial GetMapChunk result,
   the current dimension, dimension slice availability, and dirty-queue events.
4. Specifically test a loaded dimension column with no overworld map chunk at
   the same X/Z. Avoid creating overworld data as a workaround during this test.

Our recent logs retain the same 375 saved tiles across exploration and reloads.
The installed Chart code returns immediately for a missing map chunk before
fetching dimension slices. Our separate Manifold fixtures can create loaded
dimension chunks without corresponding overworld map chunks. Those facts
support the hypothesis but do not yet prove the running client took that branch.

The mapping failure remains present after fixing database cleanup. No terrain
mapping patch or replacement renderer is included.

## Waypoint creation

1. Stand in a custom dimension and create a new marker through the map dialog.
2. Compare its saved internal Y with a marker made using `/waypoint add` while
   standing at that location.
3. With the adapter, verify the map-dialog marker stays visible after reopening
   the map/save and is hidden in the overworld.
4. Check overworld creation and already dimension-encoded positions are unchanged.

The workaround and adapter both worked in our in-game tests. The Atlas scenario
checks position encoding, setter patch installation, vanilla command storage,
and serialization; it does not render Chart's marker pins.

## Running the included tests

These are reference scenarios from our existing Rift Traveler Atlas project,
not a standalone test harness. They expect Rift Traveler to be loaded and use
its ModSystem names (and, for waypoint storage, its Fractured Deep debug command).
Copying the test files alone into Chart will not make them runnable unchanged.

The working suite used .NET 10, Pixnop.Manifold 0.6.0,
Pixnop.Atlas.XUnit 0.15.1, xUnit 2.9.3, and the installed game/Harmony assemblies.
All 20 scenarios passed before publication; only the two relevant scenarios are
included here. Léon can adapt these to his own harness or reproduce the client
behavior with the steps above. A stripped-down runnable fixture can be supplied
later if needed; it is not included in this repository.
