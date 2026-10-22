using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using InspectionView.Cameras.GenTL.GenICam;
using InspectionView.Core.Types;
using Xunit;

namespace InspectionView.Cameras.GenTL.Tests
{
    public class DeviceDescriptionTests
    {
        private const string SmallXml = "<RegisterDescription ModelName=\"Z\" VendorName=\"V\"><Integer Name=\"A\"><Value>3</Value></Integer></RegisterDescription>";

        [Theory]
        [InlineData("Local:Cam.zip;8000;1A3", "Cam.zip", 0x8000, 0x1A3)]
        [InlineData("local:///Cam.xml;0x10000;0x200?SchemaVersion=1.1.0", "Cam.xml", 0x10000, 0x200)]
        public void ParsesLocalUrl(string url, string file, long address, long length)
        {
            Assert.True(DeviceDescription.TryParseLocal(url, out var f, out var a, out var l));
            Assert.Equal(file, f);
            Assert.Equal(address, a);
            Assert.Equal(length, l);
        }

        [Fact]
        public void RejectsNonLocalUrl()
        {
            Assert.False(DeviceDescription.TryParseLocal("File:///C:/x.xml", out _, out _, out _));
        }

        private static byte[] Zip(string entryName, string text)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var e = zip.CreateEntry(entryName);
                    using (var s = e.Open())
                    {
                        var b = Encoding.UTF8.GetBytes(text);
                        s.Write(b, 0, b.Length);
                    }
                }
                return ms.ToArray();
            }
        }

        [Fact]
        public void LoadsZippedDescriptionFromDeviceMemory()
        {
            var port = new MemoryPort();
            var zip = Zip("Cam.xml", SmallXml);
            Array.Copy(zip, 0, port.Memory, 0x1000, zip.Length);    // 길이가 4의 배수가 아니어도 된다
            string url = $"Local:Cam.zip;{0x1000:X};{zip.Length:X}";
            string xml = DeviceDescription.Load(url, port);
            Assert.Equal(SmallXml, xml);
            var map = NodeMap.Parse(xml, port);
            Assert.Equal(3, map.Require<IntegerNode>("A").GetInt());
        }

        [Fact]
        public void LoadsPlainXmlWithTrailingNuls()
        {
            var bytes = new byte[SmallXml.Length + 7];
            Encoding.ASCII.GetBytes(SmallXml).CopyTo(bytes, 0);
            Assert.Equal(SmallXml, DeviceDescription.Extract(bytes, "Cam.xml"));
        }

        [Fact]
        public void LoadsFileUrl()
        {
            string path = Path.Combine(Path.GetTempPath(), $"iv_gentl_{Guid.NewGuid():N}.xml");
            File.WriteAllText(path, SmallXml);
            try
            {
                string url = "File:///" + path.Replace('\\', '/').TrimStart('/') + "?SchemaVersion=1.0.0";
                Assert.Equal(SmallXml, DeviceDescription.Load(url, new MemoryPort()));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    public class PixelFormatTests
    {
        [Fact]
        public void CodesAndBitDepths()
        {
            Assert.Equal("Mono8", PixelFormats.NameOf(0x01080001));
            Assert.Equal(0x01100005, PixelFormats.CodeOf("mono12"));
            Assert.Equal(12, PixelFormats.BitsPerPixel(0x010C0006));
            Assert.Equal(15, PixelFormats.LineBytes(0x010C0047, 10));   // Mono12p: 10 px * 12 bit = 15 바이트
            Assert.Null(PixelFormats.NameOf(0x7FFFFFFF));
        }

        private static Image FromBytes(long code, byte[] data, int width, int height, long xPadding = 0)
        {
            var h = GCHandle.Alloc(data, GCHandleType.Pinned);
            try { return PixelFormats.ToImage(code, h.AddrOfPinnedObject(), width, height, xPadding); }
            finally { h.Free(); }
        }

        [Fact]
        public void Mono8WithPadding()
        {
            var data = new byte[] { 1, 2, 3, 0xEE, 4, 5, 6, 0xEE };
            using (var img = FromBytes(0x01080001, data, 3, 2, xPadding: 1))
            {
                Assert.Equal(PixelType.UInt8, img.PixelType);
                Assert.Equal(new byte[] { 4, 5, 6 }, img.GetRowReadOnly<byte>(1).ToArray());
            }
        }

        [Fact]
        public void Mono12PackedGev()
        {
            // p0 = 0xABC, p1 = 0x123 → b0 = 0xAB, b1 = (p1 하위 3) << 4 | (p0 하위 C) = 0x3C, b2 = 0x12
            var data = new byte[] { 0xAB, 0x3C, 0x12 };
            using (var img = FromBytes(0x010C0006, data, 2, 1))
            {
                Assert.Equal(PixelType.UInt16, img.PixelType);
                Assert.Equal(new ushort[] { 0xABC, 0x123 }, img.GetRowReadOnly<ushort>(0).ToArray());
            }
        }

        [Fact]
        public void Mono10PackedGev()
        {
            // p0 = 0x3FF (b0 = 0xFF, 하위 2비트 3), p1 = 0x001 (b2 = 0x00, 하위 2비트 1 → b1 비트 4-5)
            var data = new byte[] { 0xFF, 0x13, 0x00 };
            using (var img = FromBytes(0x010C0004, data, 2, 1))
                Assert.Equal(new ushort[] { 0x3FF, 0x001 }, img.GetRowReadOnly<ushort>(0).ToArray());
        }

        [Fact]
        public void Mono12pLsbFirst()
        {
            // p0 = 0xABC, p1 = 0x123: 비트 스트림 LSB 부터 → b0 = 0xBC, b1 = 0x3A, b2 = 0x12
            var data = new byte[] { 0xBC, 0x3A, 0x12 };
            using (var img = FromBytes(0x010C0047, data, 2, 1))
                Assert.Equal(new ushort[] { 0xABC, 0x123 }, img.GetRowReadOnly<ushort>(0).ToArray());
        }

        [Fact]
        public void Mono10pLsbFirst()
        {
            // 4픽셀 = 5바이트: 1, 2, 3, 0x3FF
            ulong bits = 1UL | (2UL << 10) | (3UL << 20) | (0x3FFUL << 30);
            var data = new byte[5];
            for (int i = 0; i < 5; i++) data[i] = (byte)(bits >> (8 * i));
            using (var img = FromBytes(0x010A0046, data, 4, 1))
                Assert.Equal(new ushort[] { 1, 2, 3, 0x3FF }, img.GetRowReadOnly<ushort>(0).ToArray());
        }
    }
}
