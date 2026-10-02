# Dimension lighting — native adapter review

Start here for the lighting code. This is **experimental source for review and
adaptation**, not a standalone mod, a released fix, or a stable public API.
Official Manifold and Chart remain unchanged.

## The problem in plain language

Some engine lighting paths retain a custom dimension's identity, while adjacent
paths lose it. A torch with a fixed block light value could work, yet a small
floor lantern whose brightness is provided by its block entity would not.
Removing a source could also leave light behind. Loaded dimension terrain can
fail the normal lighting dispatch guard when overworld map data is absent.

The native adapter keeps the engine's own propagation and task queues. It
corrects those three boundaries only for explicitly registered world/dimension
pairs, instead of periodically repainting chunks or running a second illuminator.

| Boundary | Observed problem | Candidate correction |
| --- | --- | --- |
| Propagator → block-entity lookup | A lookup arrives with dimension 0 and dimension encoded in raw Y | Clone and normalize the position, only inside that propagator's matching accessor scope |
| Server removal → packet 72 | Packet contains local Y, so the client reconstructs dimension 0 | Preserve InternalY in the original matching removal packet |
| Server block-light dispatch | Missing overworld map chunk rejects an otherwise loaded dimension chunk | Submit to the existing native task queue only for that loaded, opted-in dimension chunk |

## Read these files in this order

1. [Reusable boundary module](source/DimensionLightCompatibility.cs): the actual
   candidate. Contains no Fractured Deep terrain codes/heights, source scans,
   extra illuminator, or chunk-resend loop.
2. [Rift Traveler integration and configuration](source/DimensionLightCompatibilitySystem.cs):
   binds the module to Fractured Deep on client/server. This file **does** depend
   on Rift Traveler's `FracturedDeepSystem`, which is not bundled here.
3. [Enabled configuration example](tests/Fixtures/NativeLighting/RiftTraveler.DimensionLighting.json):
   all three corrections enabled. The consumer defaults to disabled when no
   configuration exists. Put this file under ModConfig on both client/server;
   full restart required. Client config is not synchronized from the server.
4. [Implementation and rollback notes](notes/NATIVE_DIMENSION_LIGHTING_CANDIDATE.md):
   detailed scope and remaining gaps. Its 19-test count describes the candidate's
   original validation; the subsequent full suite, including map cleanup, passed
   20 scenarios before publication.
5. [Read-only light diagnostics](source/FracturedDeepLightDiagnostics.cs):
   reports source HSV, block entity, source registration, and nearby stored block
   and sunlight levels. The `/rt debug lightinfo` command registration belongs
   to the full mod and is not included in this snapshot.

## How another consumer would use it

The internal constructor takes an existing engine `WorldMap`, the dimension's
numeric ID, and configuration. A consumer owns the returned disposable binding,
and disposes it when the world/dimension goes away. The included integration
demonstrates that ownership; it is not a generic drop-in integration for every
mod. An adopter would replace the FD-specific dimension discovery with its own.

The module supports simultaneous provider/dimension bindings, including client
and server in singleplayer. Patches are scoped to those bindings. Disposing one
does not unpatch the others; the final binding removes the module's Harmony ID.
Nested thread-local scopes are restored through finalizers. Caller positions
are not edited in place. Signature validation rejects an incompatible engine.

It references .NET 10, Harmony, and Vintage Story API/Lib engine internals from
1.22.7. The consumer additionally references Manifold 0.6.0. The configuration
type currently lives in the consumer source. This is not a packaged framework.

**Do not run a second repair writer alongside it.** In our full project the
older `FracturedDeepBlockLightAdapter` checks `IsServerActive` and skips its
illuminator/listeners when this candidate successfully starts. That legacy
writer is intentionally not included as a proposed solution. Copying only
these files into an older build without its guard is not a supported install.

## What has been tested

The included tests are reference scenarios from the full Rift Traveler Atlas
project, not a standalone harness. See the [test guide](tests/README.md).

Automated coverage exercises the actual candidate through native server queues,
client light buffers, removal packet serialization/handling, and a second
Manifold test dimension. The latter uses controlled save/unload/reload and
preserves the actual lantern stack. Tests also check overworld controls,
overlapping sources, lining changes, and independent client/server bindings.

In-game feedback for the latest candidate is positive: seamstone glows green,
and the user reports the lighting changes look good with no noticed problems.
This is encouraging, not proof that every lifecycle or source type is solved.
The older iterations had flashes and delayed small-lantern updates; they are
not the proposed native path shown here.

## Limits — important before adoption

- Absorption-only dispatch is not corrected.
- The adapter does not initialize every generation-time light source or
  retroactively repair old saved lighting.
- FD's existing one-time threshold relight remains outside the module.
- Controlled server chunk reload is tested, but natural distance eviction,
  process restarts, real multiplayer timing, and actual GPU rendering are not
  comprehensively automated.
- A client queue fixture is not a real connected-client synchronization test.
- No promise of zero visual flashes for every source/scenario is made.
- Sky colour, fog, sun/moon rendering, and ambient sunlight suppression are
  separate concerns. Those FD environment changes are **not** included here.

## Manual validation in a test dimension

Use a fresh test world or an existing safe copy; no purge is required. First
reproduce with the candidate disabled, then enable matching client/server
settings and restart. Keep unrelated repair writers disabled.

Check floor-mounted small lantern placement/removal, lining changes, overlapping
torchlight, large lanterns and torches, placed and freshly generated emissive
blocks, chunk-edge placement, travel out/back, save/reopen, and newly explored
terrain. Repeat ordinary controls in the overworld. Inspect stored block/sun
light before and after, not only screenshots.

Questions for Léon: do these boundary corrections belong upstream in Manifold
or an optional companion, and are there safer engine hooks than these private
members? We would prefer upstream-supported integration to maintaining forks.
