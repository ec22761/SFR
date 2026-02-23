# Making Objects Interactable in SFR

This guide explains how to make a placed/thrown object interactable — giving it a highlight prompt that players can activate by pressing the interact key. This is based on how the Mimic Mine originally used the interact system to disguise itself as a supply crate pickup.

---

## Table of Contents

1. [Overview](#1-overview)
2. [Enabling Interaction on an Object](#2-enabling-interaction-on-an-object)
3. [Handling the Activate Callback](#3-handling-the-activate-callback)
4. [Conditional Activation (State Machines)](#4-conditional-activation-state-machines)
5. [Key Properties Reference](#5-key-properties-reference)
6. [Full Example](#6-full-example)
7. [Tips & Pitfalls](#7-tips--pitfalls)

---

## 1. Overview

Any `ObjectData` subclass (thrown grenades, placed mines, custom objects, etc.) can be made interactable. When an object is interactable, players within range see a highlight/prompt and can press the interact key to trigger custom logic.

This is useful for:
- Trap objects that detonate when a player tries to "pick them up"
- Interactive world objects (buttons, levers, containers)
- Any object that should respond to player intent

---

## 2. Enabling Interaction on an Object

To make an object interactable, set three properties on it (typically inside `Initialize()` or a state-change handler):

```csharp
Activateable = true;               // Enables the interact prompt
ActivateableHighlightning = true;   // Shows the visual highlight around the object
ActivateRange = 14f;                // Range (in game units) at which the prompt appears
```

These are built-in properties on `ObjectData`. You can set them at any time — during initialization, or later in response to a state change.

### Disabling Interaction

To remove interactability (e.g., after the object has been used):

```csharp
Activateable = false;
ActivateableHighlightning = false;
```

---

## 3. Handling the Activate Callback

Override the `Activate` method to define what happens when a player interacts:

```csharp
public override void Activate(ObjectData sender)
{
    if (GameOwner != GameOwnerEnum.Client)
    {
        // Your logic here — e.g., trigger an explosion, spawn items, change state
    }
}
```

**Important:** Always guard server-only logic with `GameOwner != GameOwnerEnum.Client` to prevent duplicate execution in multiplayer.

The `sender` parameter is the `ObjectData` of the player who activated the object.

---

## 4. Conditional Activation (State Machines)

Often you only want an object to be interactable in a specific state. A common pattern is to use a state enum and enable interaction when the object reaches the right state:

```csharp
private enum MyObjectState
{
    Airborne,
    Arming,
    Ready,      // ← interactable in this state
    Triggered,
    Done
}

private MyObjectState _state = MyObjectState.Airborne;
```

Enable interaction when transitioning to the ready state:

```csharp
public override void PropertyValueChanged(ObjectPropertyInstance propertyChanged)
{
    if (propertyChanged.Base.PropertyID == 212) // Mine_Status property ID
    {
        int status = (int)Properties.Get(ObjectPropertyID.Mine_Status).Value;
        switch (status)
        {
            case 2: // Ready
                _state = MyObjectState.Ready;
                Activateable = true;
                ActivateableHighlightning = true;
                ActivateRange = 14f;
                break;
            case 3: // Triggered
                _state = MyObjectState.Triggered;
                Activateable = false;
                ActivateableHighlightning = false;
                break;
        }
    }
}
```

Guard the `Activate` callback with a state check:

```csharp
public override void Activate(ObjectData sender)
{
    if (_state == MyObjectState.Ready && GameOwner != GameOwnerEnum.Client)
    {
        Properties.Get(ObjectPropertyID.Mine_Status).Value = 3; // Trigger next state
        SoundHandler.PlaySound("MineTrigger", GameWorld);
    }
}
```

---

## 5. Key Properties Reference

| Property | Type | Description |
|----------|------|-------------|
| `Activateable` | `bool` | Whether the object can be activated by players |
| `ActivateableHighlightning` | `bool` | Whether to show the visual highlight/prompt |
| `ActivateRange` | `float` | Distance (game units) at which the prompt appears. `14f` is a typical pickup range |

These are all instance properties on `ObjectData` and can be set/changed at any time during the object's lifetime.

---

## 6. Full Example

Below is a minimal example of an object that becomes interactable after landing and arms itself, then explodes when a player interacts with it:

```csharp
using SFD;
using SFD.Effects;
using SFD.Sounds;
using Explosion = SFD.Explosion;

namespace SFR.Objects;

internal sealed class ObjectTrapBox : ObjectData
{
    private bool _armed;

    internal ObjectTrapBox(ObjectDataStartParams startParams) : base(startParams) { }

    public override void Initialize()
    {
        EnableUpdateObject();
        Body.SetBullet(true);
    }

    public override void ImpactHit(ObjectData otherObject, ImpactHitEventArgs e)
    {
        base.ImpactHit(otherObject, e);

        // Arm and become interactable on first landing
        if (!_armed && GameOwner != GameOwnerEnum.Client)
        {
            _armed = true;
            Activateable = true;
            ActivateableHighlightning = true;
            ActivateRange = 14f;
        }
    }

    public override void Activate(ObjectData sender)
    {
        if (_armed && GameOwner != GameOwnerEnum.Client)
        {
            Destroy(); // triggers OnDestroyObject
        }
    }

    public override void OnDestroyObject()
    {
        if (GameOwner != GameOwnerEnum.Client)
        {
            _ = GameWorld.TriggerExplosion(GetWorldPosition(), 80f);
            SoundHandler.PlaySound("Explosion", GetWorldPosition(), GameWorld);
            EffectHandler.PlayEffect("EXP", GetWorldPosition(), GameWorld);
        }
    }
}
```

---

## 7. Tips & Pitfalls

- **Client/Server guards:** Always wrap state-changing logic in `GameOwner != GameOwnerEnum.Client`. The `Activate` callback fires on all peers; only the server should mutate state.
- **Timing:** You can enable/disable `Activateable` at any point. It's common to enable it after a delay (arming timer) and disable it once triggered.
- **Range tuning:** `ActivateRange = 14f` matches the default item pickup range. Increase it for objects that should be easier to interact with, decrease it for "trap" objects that require the player to be very close.
- **Visual feedback:** Combine `ActivateableHighlightning` with decal swaps or blinking to give players visual cues about the object's state. Use `SwapDecal()` patterns to change the object's appearance.
- **Property sync:** If your object needs to sync state across the network, use `ObjectPropertyID` properties (like `Mine_Status`) and handle `PropertyValueChanged` to react to state transitions on all peers.
- **Interaction vs. physics triggers:** `Activate` requires the player to press the interact key. If you want the object to react to physical contact (kicks, punches, projectiles), use `PlayerMeleeHit`, `ProjectileHit`, or `ExplosionHit` overrides instead.
