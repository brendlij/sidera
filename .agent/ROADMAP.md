# Sidera Autonomous Roadmap

## Product scope

Sidera is currently:
- Windows standalone first
- simulator first
- .NET 10
- Avalonia
- modern astrophotography sequencer

## Current architecture

Already implemented:
- DeviceRegistry
- RigRegistry
- EventBus
- StateStore
- ResourceManager
- DeviceOperationService
- SequenceRunner
- RepeatStep
- SequenceGroup
- ParallelStep
- SafePoint coordination
- Camera simulator
- Mount simulator
- Camera exposure
- SlewAction

## Next priorities

Work roughly in this direction:

1. Guiding abstraction and simulator
2. Dither coordination
3. Guiding settle
4. Pause / Resume
5. Sequence persistence
6. Equipment UI
7. single-camera real hardware preparation
8. Alpaca
9. ASCOM compatibility

## Do not implement yet

- remote/headless host
- Linux support work
- INDI
- plugin system
- native plate solver
- autofocus
- scheduler
- advanced recovery

## Rules

- Only implement one coherent slice at a time.
- Prefer small architecture changes.
- Do not redesign unrelated systems.
- Maintain backwards compatibility where reasonable.
- Every slice must build with zero warnings and all tests passing.