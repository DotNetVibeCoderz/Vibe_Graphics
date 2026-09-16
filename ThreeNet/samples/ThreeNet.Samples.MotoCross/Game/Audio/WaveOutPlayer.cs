using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MotoCross.Game.Audio;

/// <summary>
/// Minimal streaming audio sink on top of the Windows <c>waveOut</c> API: a few
/// small buffers are kept queued and refilled from a background thread by the
/// callback supplied by the caller. No audio package is needed, and everything
/// the game plays is synthesised sample by sample.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WaveOutPlayer : IDisposable
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
    private const int FramesPerBuffer = 1024;
    private const int BufferCount = 4;
    private const int WhdrDone = 0x00000001;

    private readonly Action<float[], int> _fill;
    private readonly float[] _mixBuffer = new float[FramesPerBuffer * Channels];
    private readonly byte[][] _buffers = new byte[BufferCount][];
    private readonly GCHandle[] _bufferHandles = new GCHandle[BufferCount];
    private readonly WaveHeader[] _headers = new WaveHeader[BufferCount];
    // The device keeps pointers to the headers, so the array stays pinned for
    // the whole life of the player.
    private GCHandle _headersHandle;
    private readonly Thread _thread;
    private nint _device;
    private volatile bool _running;

    public WaveOutPlayer(Action<float[], int> fill)
    {
        _fill = fill;
        WaveFormat format = new()
        {
            FormatTag = 1, // PCM
            Channels = Channels,
            SamplesPerSecond = SampleRate,
            BitsPerSample = 16,
            BlockAlign = (short)(Channels * 2),
            AverageBytesPerSecond = SampleRate * Channels * 2,
            Size = 0,
        };

        int result = waveOutOpen(out _device, unchecked((uint)-1), ref format, nint.Zero, nint.Zero, 0);
        if (result != 0)
        {
            throw new InvalidOperationException($"waveOutOpen failed with {result}");
        }

        _headersHandle = GCHandle.Alloc(_headers, GCHandleType.Pinned);
        for (int i = 0; i < BufferCount; i++)
        {
            _buffers[i] = new byte[FramesPerBuffer * Channels * 2];
            _bufferHandles[i] = GCHandle.Alloc(_buffers[i], GCHandleType.Pinned);
            _headers[i] = new WaveHeader
            {
                Data = _bufferHandles[i].AddrOfPinnedObject(),
                BufferLength = (uint)_buffers[i].Length,
                Flags = 0,
            };
        }

        _running = true;
        _thread = new Thread(Pump) { IsBackground = true, Name = "motocross-audio", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Pump()
    {
        // Prime every buffer, then recycle them as the device finishes each one.
        for (int i = 0; i < BufferCount && _running; i++)
        {
            Submit(i);
        }

        while (_running)
        {
            bool idle = true;
            for (int i = 0; i < BufferCount; i++)
            {
                ref WaveHeader header = ref _headers[i];
                if ((header.Flags & WhdrDone) != 0)
                {
                    waveOutUnprepareHeader(_device, ref header, (uint)Marshal.SizeOf<WaveHeader>());
                    Submit(i);
                    idle = false;
                }
            }

            if (idle)
            {
                Thread.Sleep(2);
            }
        }
    }

    private void Submit(int index)
    {
        Array.Clear(_mixBuffer);
        try
        {
            _fill(_mixBuffer, FramesPerBuffer);
        }
        catch
        {
            // A broken voice must never take the audio thread down.
            Array.Clear(_mixBuffer);
        }

        byte[] target = _buffers[index];
        for (int i = 0; i < _mixBuffer.Length; i++)
        {
            short value = (short)(Math.Clamp(_mixBuffer[i], -1f, 1f) * 32000f);
            target[i * 2] = (byte)(value & 0xFF);
            target[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        ref WaveHeader header = ref _headers[index];
        header.Flags = 0;
        header.BufferLength = (uint)target.Length;
        header.BytesRecorded = 0;
        uint size = (uint)Marshal.SizeOf<WaveHeader>();
        if (waveOutPrepareHeader(_device, ref header, size) == 0)
        {
            waveOutWrite(_device, ref header, size);
        }
    }

    public void Dispose()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _thread.Join(200);
        waveOutReset(_device);
        for (int i = 0; i < BufferCount; i++)
        {
            waveOutUnprepareHeader(_device, ref _headers[i], (uint)Marshal.SizeOf<WaveHeader>());
            if (_bufferHandles[i].IsAllocated)
            {
                _bufferHandles[i].Free();
            }
        }

        waveOutClose(_device);
        if (_headersHandle.IsAllocated)
        {
            _headersHandle.Free();
        }

        _device = nint.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSecond;
        public int AverageBytesPerSecond;
        public short BlockAlign;
        public short BitsPerSample;
        public short Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nint Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out nint device, uint deviceId, ref WaveFormat format, nint callback, nint instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(nint device, ref WaveHeader header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(nint device, ref WaveHeader header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(nint device, ref WaveHeader header, uint size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(nint device);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(nint device);
}
