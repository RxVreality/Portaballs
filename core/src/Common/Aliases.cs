// Portaballs core: engine-free maths shared by the s&box game and the headless tests.
//
// These aliases pin the core to System.Numerics types on purpose. Inside s&box the bare names
// Vector2 / Vector3 / Vector4 resolve to s&box's own structs (different member names), and Plane
// is ambiguous with Sandbox.Plane. So core code must only ever write Vec2 / Vec3 / Vec4 / Quat /
// Mat4 / Plane3, and never "using System.Numerics;". The s&box side converts at the boundary.
global using Vec2 = System.Numerics.Vector2;
global using Vec3 = System.Numerics.Vector3;
global using Vec4 = System.Numerics.Vector4;
global using Quat = System.Numerics.Quaternion;
global using Mat4 = System.Numerics.Matrix4x4;
global using Plane3 = System.Numerics.Plane;
