# Sandbox.Rendering

s&box's side of rendering: what connects the engine to [Borealis](../Borealis/README.md), the renderer. Borealis doesn't reference this assembly; this one references Borealis.

| Folder | Responsibility |
| --- | --- |
| [Bridge](Bridge/) | Mirrors managed and native-authored tool objects into Borealis `RenderWorld`s. Resolves camera, capture, fog and sky settings per view, and runs camera stages on Borealis.Gpu. |

[EngineHooks.cs](EngineHooks.cs) installs it at startup, from the app system. The bridge is described in the
[Borealis reference](../../docs/reference/borealis.md).

Processes without a Borealis.Gpu device do not render scene cameras. Native scene objects created by editor tools are still read by the bridge; they are not a separate renderer.

Anything that knows about `SceneObject`, `SceneWorld`, cameras or native scene views belongs here, not in Borealis.

Procedural models can use their live shader-made materials while Borealis builds the mesh, avoiding a compiled-material
lookup for draws the live material already replaces. Draws still use the normal lookup where a fallback is needed or it
determines eligibility for the depth fast path.
Async prefetch also skips those live draws when the serialized draw rules out the depth fast path. Draws that may still
need their serialized material keep the usual prefetch.

For models with managed builder meshes, the bridge snapshots the existing vertex and index buffers once and combines them
with native draw metadata. It does not write and reread those buffers as VMDL blocks. Non-builder procedural models still
read native mesh blocks, and file-backed models still use their compiled assets.
