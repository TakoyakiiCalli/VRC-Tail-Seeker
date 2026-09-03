# Tail Seek Builder

A Unity Editor window that sets up **tail-to-hip interaction** on a VRChat avatar. When another avatar's tail (or the editor test object) comes near your hips, your tail curls using your existing clip, the same way VRCFury Depth Animation scrubs a clip by proximity.

Direction uses **Left** and **Right** curl clips on FX, blended by `ContactTracker/X-` and `ContactTracker/X+`. That is the same animator path as proximity curl. Unity Aim / Rotation Constraints are not used — they get overwritten by the humanoid and FX graphs, so the bone never turns even when the tracker floats are moving.

## Requirements

- Unity with the VRChat SDK3 Avatars package
- An avatar with a `VRCAvatarDescriptor` and an Animator Controller on the **FX** playable layer
- A tail with a root bone and a tip transform
- An Armature whose first child bone is the hips (detected automatically)
- Your existing tail curl `AnimationClip` (blendshape sets keyed over time, not looped)
- Left and right curl clips if you want the tail to turn toward the other user
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
3. The FX layer **Tail Seek** uses **Motion Time** on `TailSeek_Curl`, so proximity scrubs your wrap clip from the first frame to the last (VRCFury Depth-style).
4. Six directional receivers from VRLabs Contact Tracker sit at the hip and write `ContactTracker/X+`, `X-`, `Y+`, `Y-`, `Z+`, `Z-`.
5. The same **Tail Seek** layer has **Idle**, **Wrap**, **Left Curl**, and **Right Curl**. Wrap plays when someone is near the hips. Left/Right play when `ContactTracker/X-` or `X+` is high.

Allow Self stays off so your own tail sender cannot curl your own tail. In VRChat, another player's tail sender is what drives you.

The VRLabs prefab's demo **Container / Cube** is deleted on install. It is only a placeholder for parenting objects onto other players and is not used here.

## Usage

1. Open **Tools → Tail Seek → Builder**.
2. Assign **Avatar**, **Tail Root**, **Tail Tip**, and **Tail Wrapping Animation**. Hip and the VRLabs Contact Tracker are filled in automatically, and tracker FX is always merged.
3. Assign **Left Curl Animation** and **Right Curl Animation** under Directional Seeking. Frame 0 should be rest. The last frame should be full curl to that side, including the bone rotation you want.
4. Click **BUILD TAIL SEEK SYSTEM**, or **REPLACE / REBUILD EXISTING TAIL SEEK SYSTEM** if you already built an older version.

### Replacing an older build

**Replace / Rebuild** deletes leftover Tail Seek objects, contacts, old aim/rotation constraints, FX layers, and `TailSeek*` / `ContactTracker/*` parameters, then builds again.

**Remove Existing Tail Seek System** only deletes. It only needs the avatar assigned.

### Editor test object

The builder writes `Assets/TailSeek/Generated/TailSeek Test Object.prefab` and places an instance on the assigned avatar at the hip.

1. Enter **Play Mode**, then select **Gesture Manager**.
2. Move it with **W/A/S/D** and **Q/E** (Shift to go faster), or drag it in the Scene view.
3. Hold it near the **hips**. Curl should respond. `debugCurl` on the test object should rise off 0.
4. Move it to the avatar's left or right. `ContactTracker/X-` or `X+` should rise and the matching clip should turn the tail.

Delete the test object before uploading the avatar.

A scene `VRCContactSender` is not another VRChat player, so Gesture Manager will not treat it as "others" on its own. The test object simulates matching proximity receivers (and writes Gesture Manager playable parameters) so you can test curl in the editor.

## Field reference

### Avatar

| Field | Description |
| --- | --- |
| **Avatar** | The `VRCAvatarDescriptor` to build on. **Hip (auto)** is the first bone parented to `Armature`. **VRLabs Tracker (auto)** is the Contact Tracker prefab plus its FX controller. |

### Tail

| Field | Description |
| --- | --- |
| **Tail Root** | First bone of the tail chain. |
| **Tail Tip** | End of the tail. The TailSeek **sender** is placed here. |
| **Tail Wrapping Animation** | Timed wrap clip. Keyframe rest at frame 0 and full wrap at the last frame. Required when **Create Curl Animator** is on. |

### Directional Seeking

| Field | Default | Description |
| --- | --- | --- |
| **Create Left / Right Curl** | on | Adds **Left Curl** and **Right Curl** states to the **Tail Seek** FX layer. |
| **Left Curl Animation** | | Timed clip used when `ContactTracker/X-` is high. |
| **Right Curl Animation** | | Timed clip used when `ContactTracker/X+` is high. |

Key the tail bone (Z rotation or whatever your rig uses) on these clips. Blendshapes can stay on the wrapping clip if the left/right clips only rotate bones.

If left and right are swapped in-game, swap the two clip fields and rebuild.

### Contact Settings

| Field | Default | Description |
| --- | --- | --- |
| **Collision Tag** | `TailSeek` | Shared tag for senders and receivers. Must match on interacting avatars. |
| **Tracker Receiver Radius** | `2.0` | Radius of the six directional tracker receivers (`X+`, `X-`, `Y+`, `Y-`, `Z+`, `Z-`). Keep this at least as large as Curl Start Distance so left/right have signal while curling. |
| **Tail Sender Radius** | `0.025` | Radius of the sender on the tail tip. |
| **Allow Self** | off | Leave off so your own tail sender cannot curl your own tail. |
| **Allow Others** | on | Other avatars' senders can trigger your hip receivers. |

### Curl

| Field | Default | Description |
| --- | --- | --- |
| **Create Curl Animator** | on | Adds the curl float and the Motion Time FX layer. |
| **Curl Start Distance** | `2.0` | Hip proximity receiver radius. Outside this, the clip stays on frame 0. |
| **Full Curl Distance** | `2.0` | Radius at which the clip reaches its last frame. Default 2 means full curl anywhere inside a 2-unit sphere. Raise Start above Full to ramp the four blendshape sets first. |
| **Curl Parameter** | `TailSeek_Curl` | FX float driven by the hip proximity receiver. |

### Play Mode Test Object

| Field | Description |
| --- | --- |
| **Create Test Prefab** | Writes the WASD test sender prefab and places it on the avatar at the hip. |
| **CREATE / RECREATE TEST PREFAB** | Regenerates that prefab and re-places it on the avatar. |

## What gets generated

On the avatar root (world position at the hip):

- `TailSeek Contact Tracker` — VRLabs tracker, collision tag configured, demo Container/Cube removed
- `TailSeek Tracker Target` — moved out of the tracker, as VRLabs requires
- `TailSeek Test Object` — Play Mode test sender, placed at the hip. Delete before upload.

On the hip:

- `TailSeek Curl Receiver` — proximity `VRCContactReceiver` writing the curl parameter

On the tail tip:

- `TailSeek Sender` — `VRCContactSender` so other avatars can detect this tail

In the avatar FX controller:

- VRLabs layers `Contact Tracker Control` and `Contact Tracker Blend Tree`
- Floats `TailSeek_Curl` and `TailSeek_Curl_Time`
- Layer `Tail Seek`, states **Idle** / **Wrap** / **Left Curl** / **Right Curl**, Motion Time = `TailSeek_Curl`, weight 1

Rebuild removes leftover Unity Aim / Rotation Constraints from older builds.

## Testing

- Test curl by moving the test object to the **hips**, not the tail tip.
- With default Full Curl Distance **2**, anything inside a 2-unit sphere of the hip is full curl. Raise **Curl Start Distance** above 2 if you want the clip to ramp in first.
- `ContactTracker/Size` staying at 0 is normal; it is the VRLabs scale control, not curl.
- Do not turn on **Allow Self** just to test. That makes your own tip sender fight the hip receiver.

## Troubleshooting

| Problem | What to check |
| --- | --- |
| Build button disabled | Avatar, tail root, tail tip, and hip (first Armature child) are required. Tail Wrapping Animation is required when Create Curl Animator is on. Left and Right clips are required when Create Left / Right Curl is on. VRLabs Contact Tracker must be imported so it can be found automatically. |
| Tracker assets not found | Import VRLabs Contact Tracker. The builder looks for assets named `Contact Tracker` and `Contact Tracker FX`. |
| Build error: no Tracker Target | Use the official VRLabs Contact Tracker prefab. |
| Build error: no FX controller | Assign an Animator Controller on the avatar's FX playable layer. |
| Curl parameter exists but is not a Float | Rename it in the builder, or change/remove the existing parameter. |
| `(NO VALID ANIMATIONS)` on new layers | Rebuild with the current builder. Older builds could strip motions when saving layer weight. |
| Test object near the tail does nothing | Receivers are on the **hip**. Move the test object there. Use Play Mode + Gesture Manager. |
| `debugCurl` moves but the tail does not | FX layer **Tail Seek** weight must be 1. The orange state should be **Wrap** when the sphere is on the hips. The wrap clip must animate the same blendshapes as the avatar meshes. |
| Location floats move but the bone does not turn | Assign Left/Right clips that actually key the tail bone. Rebuild so **Tail Seek** has **Left Curl** / **Right Curl**. Move the test object left or right of the hips, not only on the center. |
| Left and right are swapped | Swap the Left/Right clip fields and rebuild. |
| Cube still visible on the tracker | Rebuild; the demo Container/Cube is removed on install. |
| Tails do not interact in VRChat | Same collision tag on both avatars. Allow Others on. Contact Tracker FX merged. Enable `ContactTracker/Control` if the VRLabs menu toggle is off. |
