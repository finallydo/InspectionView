using System;
using System.Collections.Generic;
using System.IO;
using InspectionView.Core.Errors;
using InspectionView.Core.Types;

namespace InspectionView.Runtime
{
    /// <summary>
    /// Region 의 바이너리 직렬화 (.rgn): "IVRG" + version(int32) + runCount(int32) + (row, colBegin, colEnd) int32 삼중.
    /// </summary>
    public static class RegionIO
    {
        private const int Magic = 0x47525649;   // "IVRG" (little-endian)
        private const int Version = 1;

        public static void Write(Stream stream, Region region)
        {
            if (region == null) throw VisionException.NullArgument(nameof(region));
            var w = new BinaryWriter(stream);
            w.Write(Magic);
            w.Write(Version);
            w.Write(region.RunCount);
            foreach (var r in region.Runs)
            {
                w.Write(r.Row);
                w.Write(r.ColBegin);
                w.Write(r.ColEnd);
            }
            w.Flush();
        }

        public static Region Read(Stream stream)
        {
            var r = new BinaryReader(stream);
            if (r.ReadInt32() != Magic)
                throw new VisionException(ErrorCode.UnsupportedFormat, "Not a region file (bad magic).");
            int version = r.ReadInt32();
            if (version != Version)
                throw new VisionException(ErrorCode.UnsupportedFormat, $"Unsupported region file version {version}.");
            int count = r.ReadInt32();
            if (count < 0)
                throw new VisionException(ErrorCode.UnsupportedFormat, "Corrupt region file.");

            var runs = new Run[count];
            for (int i = 0; i < count; i++)
                runs[i] = new Run(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            return new Region((IEnumerable<Run>)runs);
        }

        public static byte[] ToBytes(Region region)
        {
            using (var ms = new MemoryStream())
            {
                Write(ms, region);
                return ms.ToArray();
            }
        }

        public static Region FromBytes(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
                return Read(ms);
        }
    }
}
