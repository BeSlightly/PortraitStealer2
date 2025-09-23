using System;
using System.IO;

namespace PortraitStealer;

public static unsafe class BinaryWriterExtensions
{
    public static void WriteHalf(this BinaryWriter writer, Half value)
    {
        writer.Write(*(ushort*)&value);
    }
}
