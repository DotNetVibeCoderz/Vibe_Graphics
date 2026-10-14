using System.Runtime.InteropServices;
using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>
/// A compiled compute kernel: a WGSL module with a <c>@compute</c> entry point.
/// </summary>
/// <remarks>
/// The storage buffers a dispatch is given are bound in order, at
/// <c>@group(0) @binding(0)</c> upwards, each as
/// <c>var&lt;storage, read_write&gt;</c>.
/// </remarks>
public sealed class ComputeKernel(Scene scene, uint id)
{
    internal Scene Scene { get; } = scene;

    internal uint Id { get; } = id;

    /// <summary>Releases the kernel. Pipelines built from it go with it.</summary>
    public void Destroy() => NativeError.Check(NativeMethods.tn_compute_kernel_destroy(Scene.Handle, Id));
}

/// <summary>
/// A block of GPU memory a compute kernel can read and write.
/// </summary>
/// <remarks>
/// Writes are staged on the CPU and uploaded before the next dispatch that uses
/// the buffer, so filling one costs nothing until it is actually needed.
/// </remarks>
public sealed class StorageBuffer(Scene scene, uint id)
{
    internal Scene Scene { get; } = scene;

    internal uint Id { get; } = id;

    /// <summary>Size in bytes, fixed when the buffer was created.</summary>
    public long Size
    {
        get
        {
            long size = NativeMethods.tn_storage_buffer_size(Scene.Handle, Id);
            if (size < 0)
            {
                throw new ThreeNetException("no such storage buffer");
            }

            return size;
        }
    }

    /// <summary>Stages <paramref name="data"/> at <paramref name="offset"/> bytes.</summary>
    public unsafe void Write<T>(ReadOnlySpan<T> data, long offset = 0)
        where T : unmanaged
    {
        fixed (T* pointer = data)
        {
            NativeError.Check(NativeMethods.tn_storage_buffer_write(
                Scene.Handle, Id, (ulong)offset, (byte*)pointer, (ulong)(data.Length * sizeof(T))));
        }
    }

    /// <summary>Releases the buffer.</summary>
    public void Destroy() => NativeError.Check(NativeMethods.tn_storage_buffer_destroy(Scene.Handle, Id));
}
