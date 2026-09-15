using System.Runtime.CompilerServices;

// Every interop struct in ThreeNet.Interop is blittable and laid out exactly
// like its Rust counterpart, so the runtime marshaller has nothing to do.
// Disabling it lets the LibraryImport generator pass structs such as Vector3
// straight through, and removes a per-call marshalling stub.
[assembly: DisableRuntimeMarshalling]
