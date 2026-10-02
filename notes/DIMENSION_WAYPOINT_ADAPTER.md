# Dimension waypoint adapter

`DimensionWaypointCompatibility.cs` is a client-only Rift Traveler compatibility
adapter for Vintage Story 1.22.7 with Manifold. It does not replace Chart or add
a separate marker registry.

The Add Waypoint dialog can supply a dimension-local Y to `/waypoint addati`.
Chart identifies a vanilla waypoint's dimension from its encoded internal Y, so
a local height incorrectly identifies that new marker as an overworld marker.

The adapter patches only the new dialog's `WorldPos` setter. For a nonzero current
dimension and a local, finite position, it clones the position and adds
`dimension * BlockPos.DimensionBoundary` to Y before the ordinary save command.
Already encoded positions are unchanged, so the offset is not applied twice.
The dimension is captured when the position is assigned, not when Save is clicked.

Existing markers, marker editing, server waypoint commands, saving, and sync stay
vanilla. Markers previously saved with the wrong dimension must be recreated;
their intended dimension cannot safely be inferred from their saved position.
Official Chart remains unmodified. This does not address Chart's separate map
reload crash or terrain sampling/shading issues.

The Harmony patch has its own ID and is removed on disposal. If the upstream
creation path is fixed, remove this isolated ModSystem after verifying the fix.
Failure to find or patch the expected API logs a warning rather than preventing
the game from loading.

## Verification

Atlas checks local/encoded positions, overworld preservation, invalid positions,
actual Harmony setter installation and invocation with no client world, the
server's `/waypoint addati` path in the Fractured Deep, and position serialization.
The headless test does not verify visible pins in Chart.

In-game acceptance: restart the client, enter the Fractured Deep, create a fresh
marker from the map, and check that it appears. Reopen the map, restart the save,
and check it remains. Return to the overworld and verify that the new dimension
marker is hidden there and existing overworld pins are unchanged.
