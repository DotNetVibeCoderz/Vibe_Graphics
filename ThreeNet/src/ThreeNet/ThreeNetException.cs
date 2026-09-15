using System.Runtime.CompilerServices;
using System.Text;
using ThreeNet.Interop;

namespace ThreeNet;

/// <summary>Thrown when a call into the native Three.Net core fails.</summary>
public sealed class ThreeNetException : Exception
{
    public ThreeNetException(string message)
        : base(message)
    {
    }

    public ThreeNetException(string message, int status)
        : base(message)
    {
        Status = status;
    }

    /// <summary>Native status code, or <c>0</c> when the failure was managed side.</summary>
    public int Status { get; }
}

/// <summary>Helpers turning native status codes into exceptions.</summary>
internal static unsafe class NativeError
{
    /// <summary>Reads the last error message of the calling thread.</summary>
    public static string GetLastMessage()
    {
        int required = NativeMethods.tn_last_error_message(null, 0);
        if (required <= 1)
        {
            return "unknown native error";
        }

        Span<byte> buffer = required <= 512 ? stackalloc byte[required] : new byte[required];
        fixed (byte* pointer = buffer)
        {
            int written = NativeMethods.tn_last_error_message(pointer, required);
            if (written <= 0)
            {
                return "unknown native error";
            }
        }

        // The native side writes a null terminated string.
        return Encoding.UTF8.GetString(buffer[..(required - 1)]);
    }

    /// <summary>Throws when <paramref name="status"/> is negative.</summary>
    public static void Check(int status, [CallerMemberName] string? operation = null)
    {
        if (status >= NativeStatus.Ok)
        {
            return;
        }

        throw new ThreeNetException($"{operation} failed: {GetLastMessage()} (status {status})", status);
    }

    /// <summary>Throws when a native factory returned the null handle (<c>0</c>).</summary>
    public static uint CheckHandle(uint handle, [CallerMemberName] string? operation = null)
    {
        if (handle == 0)
        {
            throw new ThreeNetException($"{operation} failed: {GetLastMessage()}");
        }

        return handle;
    }

    /// <summary>Reads a string from a native "copy into buffer" function.</summary>
    public static string ReadString(Func<nint, int, int> reader)
    {
        int required = reader(nint.Zero, 0);
        if (required <= 1)
        {
            return string.Empty;
        }

        byte[] buffer = new byte[required];
        fixed (byte* pointer = buffer)
        {
            int written = reader((nint)pointer, required);
            if (written < 0)
            {
                throw new ThreeNetException($"failed to read a native string: {GetLastMessage()}", written);
            }
        }

        return Encoding.UTF8.GetString(buffer, 0, required - 1);
    }
}
