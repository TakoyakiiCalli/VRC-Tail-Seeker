# Tail Seek Builder

A Unity Editor window that sets up **tail-to-hip interaction** on a VRChat avatar. When another avatar's tail (or the editor test object) comes near your hips, your tail curls using your existing clip, the same way VRCFury Depth Animation scrubs a clip by proximity.

Direction is meant to come from the [VRLabs Contact Tracker](https://github.com/VRLabs/Contact-Tracker) plus an Aim Constraint on the tail root. **Curl works. Live rotation toward another user or the test object is not finished yet.**

## Requirements

- Unity with the VRChat SDK3 Avatars package
- An avatar with a `VRCAvatarDescriptor` and an Animator Controller on the **FX** playable layer
- A tail with a root bone and a tip transform
- A hips / pelvis bone (humanoid Hips is auto-detected)
- Your existing tail curl `AnimationClip` (blendshape sets keyed over time, not looped)
- [VRLabs Contact Tracker](https://github.com/VRLabs/Contact-Tracker) (prefab + FX controller)
- [Gesture Manager](https://github.com/BlackStartx/VRC-Gesture-Manager) is recommended for editor testing

## Installation

1. Copy these scripts into your Unity project:
   - `TailSeekerBuilder_WithTestPrefab.cs` — editor-only (`#if UNITY_EDITOR`), put it under an Editor folder such as `Assets/Editor/`
   - `TailSeekTestObject.cs` — runtime component used by the generated test prefab
2. Import VRLabs Contact Tracker into the same project.
3. Open **Tools → Tail Seek → Builder**.

## How it works

Two avatars that share the same **Collision Tag** (default `TailSeek`) interact like this:

1. A **Contact Sender** on your **tail tip** broadcasts the tag so other people can detect your tail.
2. A **proximity receiver** on your **hip** writes `TailSeek_Curl` (0 at the edge of the receiver, 1 at the center).
3. The FX layer **Tail Seek - Curl** uses **Motion Time** on that float, so proximity scrubs your curl clip from the first frame to the last (VRCFury Depth-style).
4. Six directional receivers from VRLabs Contact Tracker sit at the hip and are meant to move **TailSeek Tracker Target**.
5. An **Aim Constraint** on the tail root is meant to point the tail at that target.

Allow Self stays off so your own tail sender cannot curl your own tail. In VRChat, another player's tail sender is what drives you.

The VRLabs prefab's demo **Container / Cube** is deleted on install. It is only a placeholder for parenting objects onto other players and is not used here.

## Usage

1. Open **Tools → Tail Seek → Builder**.
2. Assign **Avatar**, **Tail Root**, **Tail Tip**, and **Curl Animation**.
3. Assign **Hip**, or click **Find Hips Bone**.
4. Click **Find VRLabs Tracker Assets**, or assign the Contact Tracker prefab and FX controller by hand.
5. Click **BUILD TAIL SEEK SYSTEM**, or **REPLACE / REBUILD EXISTING TAIL SEEK SYSTEM** if you already built an older version.

### Replacing an older build

**Replace / Rebuild** deletes leftover Tail Seek objects, contacts, aim constraints, FX layers, and `TailSeek*` / `ContactTracker/*` parameters, then builds again.

**Remove Existing Tail Seek System** only deletes. It only needs the avatar assigned.

### Editor test object

The builder can create `Assets/TailSeek/Generated/TailSeek Test Object.prefab`.

1. Drag it into the scene.
2. Enter **Play Mode**, then select **Gesture Manager**.
3. Move it with **W/A/S/D** and **Q/E** (Shift to go faster), or drag it in the Scene view.
4. Hold it near the **hips**. Curl should respond. `debugCurl` on the test object should rise off 0.

A scene `VRCContactSender` is not another VRChat player, so Gesture Manager will not treat it as "others" on its own. The test object simulates matching proximity receivers (and writes Gesture Manager playable parameters) so you can test curl in the editor.

## Field reference

### Avatar

| Field | Description |
| --- | --- |
| **Avatar** | The `VRCAvatarDescriptor` to build on. |

### Tail

| Field | Description |
| --- | --- |
| **Tail Root** | First bone of the tail chain. The Aim Constraint is added here. |
| **Tail Tip** | End of the tail. The TailSeek **sender** is placed here. |
| **Curl Animation** | Timed curl clip. Keyframe rest at frame 0 and full curl at the last frame. Required when **Create Curl Animator** is on. |

### Hip

| Field | Description |
| --- | --- |
| **Hip** | Hips / pelvis. The Contact Tracker and curl **receiver** are placed here. |
| **Find Hips Bone** | Uses the humanoid Hips bone, then falls back to objects named `Hips`, `Hip`, or `Pelvis`. |

### Contact Tracker

| Field | Description |
| --- | --- |
| **Tracker Prefab** | VRLabs **Contact Tracker** prefab. |
| **Tracker FX Controller** | VRLabs **Contact Tracker FX** controller. Layers and parameters are merged into the avatar FX controller. |
| **Merge Tracker FX Controller** | Copies those layers/parameters into FX. |
| **Find VRLabs Tracker Assets** | Searches for assets named `Contact Tracker` and `Contact Tracker FX`. |

### Contact Settings

| Field | Default | Description |
| --- | --- | --- |
| **Collision Tag** | `TailSeek` | Shared tag for senders and receivers. Must match on interacting avatars. |
| **Tracker Receiver Radius** | `1.0` | Radius of the six directional tracker receivers (`X+`, `X-`, `Y+`, `Y-`, `Z+`, `Z-`). |
| **Tail Sender Radius** | `0.025` | Radius of the sender on the tail tip. |
| **Allow Self** | off | Leave off so your own tail sender cannot curl your own tail. |
| **Allow Others** | on | Other avatars' senders can trigger your hip receivers. |

### Curl

| Field | Default | Description |
| --- | --- | --- |
| **Create Curl Animator** | on | Adds the curl float and the Motion Time FX layer. |
| **Curl Start Distance** | `0.50` | Hip proximity receiver radius. Outside this, the clip stays on frame 0. |
| **Full Curl Distance** | `0.10` | Must be smaller than Curl Start Distance. |
| **Curl Parameter** | `TailSeek_Curl` | FX float driven by the hip proximity receiver. |

### Directional Seeking

| Field | Default | Description |
| --- | --- | --- |
| **Aim Tail Toward Target** | on | Adds an Aim Constraint from the tail root to `TailSeek Tracker Target`. |
| **Tail Forward Axis** | `(0, 0, 1)` | Local axis of the tail root that should point at the target. |
| **Tail Up Axis** | `(0, 1, 0)` | Local up axis for the Aim Constraint. |

### Play Mode Test Object

| Field | Description |
| --- | --- |
| **Create Test Prefab** | Writes the WASD test sender prefab. |
| **CREATE / RECREATE TEST PREFAB** | Regenerates that prefab without rebuilding the avatar. |

## What gets generated

On the avatar root (world position at the hip):

- `TailSeek Contact Tracker` — VRLabs tracker, collision tag configured, demo Container/Cube removed
- `TailSeek Tracker Target` — moved out of the tracker, as VRLabs requires

On the hip:

- `TailSeek Curl Receiver` — proximity `VRCContactReceiver` writing the curl parameter

On the tail tip:

- `TailSeek Sender` — `VRCContactSender` so other avatars can detect this tail

On the tail root (if aiming is enabled):

- `AimConstraint` targeting `TailSeek Tracker Target`

In the avatar FX controller:

- VRLabs layers `Contact Tracker Control` and `Contact Tracker Blend Tree` (if merge is on)
- Float `TailSeek_Curl`
- Layer `Tail Seek - Curl`, state **Curl Depth**, Motion Time = curl parameter, weight 1

## Known issues

**Rotation from live location is not working yet.** Curl proximity can drive the blendshape clip, but the tail does not yet reliably rotate toward another player's tail or the editor test object using live positional data from the Contact Tracker (`ContactTracker/X+` … `Z-` → Tracker Target → Aim Constraint).

That is the next piece to fix: keep the Tracker Target locked to the other sender's live position (in-game other user, or the test object in Play Mode / Gesture Manager) so the Aim Constraint actually follows them.

Until then:

- Test curl by moving the test object to the **hips**, not the tail tip.
- `ContactTracker/Size` staying at 0 is normal; it is the VRLabs scale control, not curl.
- Do not turn on **Allow Self** just to test. That makes your own tip sender fight the hip receiver.

## Troubleshooting

| Problem | What to check |
| --- | --- |
| Build button disabled | Avatar, tail root, tail tip, hip, tracker prefab, and tracker FX controller are required. Curl animation is also required when Create Curl Animator is on. |
| Tracker assets not found | Import VRLabs Contact Tracker, then assign the prefab and FX controller manually. |
| Build error: no Tracker Target | Use the official VRLabs Contact Tracker prefab. |
| Build error: no FX controller | Assign an Animator Controller on the avatar's FX playable layer. |
| Curl parameter exists but is not a Float | Rename it in the builder, or change/remove the existing parameter. |
| `(NO VALID ANIMATIONS)` on new layers | Rebuild with the current builder. Older builds could strip motions when saving layer weight. |
| Test object near the tail does nothing | Receivers are on the **hip**. Move the test object there. Use Play Mode + Gesture Manager. |
| `debugCurl` moves but the tail does not | FX layer **Tail Seek - Curl** weight must be 1. The clip must animate the same blendshapes as the avatar meshes. |
| Cube still visible on the tracker | Rebuild; the demo Container/Cube is removed on install. |
| Tails do not interact in VRChat | Same collision tag on both avatars. Allow Others on. Contact Tracker FX merged. Enable `ContactTracker/Control` if the VRLabs menu toggle is off. |
| Tail aims the wrong way | Change **Tail Forward Axis** / **Tail Up Axis**. Live aiming is still an open issue (see Known issues). |
