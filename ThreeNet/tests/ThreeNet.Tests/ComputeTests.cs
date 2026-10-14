using Xunit;

namespace ThreeNet.Tests;

/// <summary>
/// Compute pipelines from the managed side. Needs a GPU that can run compute;
/// skips without one.
/// </summary>
public class ComputeTests
{
    private const string Double = """
        @group(0) @binding(0) var<storage, read_write> values: array<u32>;

        @compute @workgroup_size(64)
        fn main(@builtin(global_invocation_id) id: vec3<u32>) {
            if (id.x >= arrayLength(&values)) {
                return;
            }
            values[id.x] = values[id.x] * 2u;
        }
        """;

    private static Renderer? TryCreateRenderer()
    {
        try
        {
            Renderer renderer = Renderer.CreateOffscreen(RendererOptions.Default with
            {
                Width = 16,
                Height = 16,
                MsaaSamples = 1,
            });
            if (!renderer.SupportsCompute)
            {
                renderer.Dispose();
                return null;
            }

            return renderer;
        }
        catch (ThreeNetException)
        {
            return null;
        }
    }

    [Fact]
    public void AKernelReadsAndWritesABuffer()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        const int count = 256;
        StorageBuffer buffer = scene.CreateStorageBuffer(count * sizeof(uint));

        uint[] input = new uint[count];
        for (int i = 0; i < count; i++)
        {
            input[i] = (uint)i;
        }

        buffer.Write<uint>(input);

        ComputeKernel kernel = scene.CreateComputeKernel(Double);
        // Workgroups, not threads: 64 invocations each.
        renderer.Dispatch(scene, kernel, new[] { buffer }, (count + 63) / 64);

        uint[] output = renderer.ReadStorageBuffer<uint>(scene, buffer);
        Assert.Equal(count, output.Length);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal((uint)(i * 2), output[i]);
        }
    }

    [Fact]
    public void BuffersBindInTheOrderTheyAreGiven()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        const string copy = """
            @group(0) @binding(0) var<storage, read_write> source: array<u32>;
            @group(0) @binding(1) var<storage, read_write> destination: array<u32>;

            @compute @workgroup_size(64)
            fn main(@builtin(global_invocation_id) id: vec3<u32>) {
                if (id.x >= arrayLength(&source)) {
                    return;
                }
                destination[id.x] = source[id.x] + 100u;
            }
            """;

        using Scene scene = new();
        StorageBuffer source = scene.CreateStorageBuffer(64 * sizeof(uint));
        StorageBuffer destination = scene.CreateStorageBuffer(64 * sizeof(uint));
        uint[] input = new uint[64];
        for (int i = 0; i < 64; i++)
        {
            input[i] = (uint)i;
        }

        source.Write<uint>(input);

        ComputeKernel kernel = scene.CreateComputeKernel(copy);
        renderer.Dispatch(scene, kernel, new[] { source, destination }, 1);

        Assert.Equal(163u, renderer.ReadStorageBuffer<uint>(scene, destination)[63]);
        // The source untouched is what says the order was not reversed.
        Assert.Equal(63u, renderer.ReadStorageBuffer<uint>(scene, source)[63]);
    }

    [Fact]
    public void DispatchesSeeWhatTheOneBeforeWrote()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        StorageBuffer buffer = scene.CreateStorageBuffer(64 * sizeof(uint));
        buffer.Write<uint>(Enumerable.Repeat(1u, 64).ToArray());

        ComputeKernel kernel = scene.CreateComputeKernel(Double);
        for (int i = 0; i < 3; i++)
        {
            renderer.Dispatch(scene, kernel, new[] { buffer }, 1);
        }

        // One doubled three times, which only holds if the queue ordered them.
        Assert.Equal(8u, renderer.ReadStorageBuffer<uint>(scene, buffer)[0]);
    }

    [Fact]
    public void AWritePastTheEndIsRefused()
    {
        using Scene scene = new();
        StorageBuffer buffer = scene.CreateStorageBuffer(8);
        Assert.Equal(8, buffer.Size);

        buffer.Write<byte>(new byte[8]);
        Assert.Throws<ThreeNetException>(() => buffer.Write<byte>(new byte[8], offset: 4));
    }

    [Fact]
    public void ABadKernelReportsRatherThanDispatchingNothing()
    {
        using Scene scene = new();
        Assert.Throws<ArgumentException>(() => scene.CreateComputeKernel(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => scene.CreateStorageBuffer(0));
    }

    [Fact]
    public void ADispatchOfNoWorkgroupsIsRefused()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        StorageBuffer buffer = scene.CreateStorageBuffer(16);
        ComputeKernel kernel = scene.CreateComputeKernel(Double);

        // Dispatching nothing is a caller bug far more often than an intention.
        Assert.Throws<ThreeNetException>(() => renderer.Dispatch(scene, kernel, new[] { buffer }, 0));
        Assert.Throws<ThreeNetException>(
            () => renderer.Dispatch(scene, kernel, Array.Empty<StorageBuffer>(), 1));
    }

    [Fact]
    public void AStagedBufferReadsBackBeforeAnyDispatch()
    {
        using Renderer? renderer = TryCreateRenderer();
        if (renderer is null)
        {
            return;
        }

        using Scene scene = new();
        StorageBuffer buffer = scene.CreateStorageBuffer(16);
        buffer.Write<uint>(new uint[] { 7, 8, 9, 10 });

        uint[] read = renderer.ReadStorageBuffer<uint>(scene, buffer);
        Assert.Equal(7u, read[0]);
        Assert.Equal(10u, read[3]);
    }
}
