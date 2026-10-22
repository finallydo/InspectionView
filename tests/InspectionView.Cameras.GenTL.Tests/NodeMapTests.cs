using System;
using System.Collections.Generic;
using System.Text;
using InspectionView.Cameras.GenTL.GenICam;
using InspectionView.Core.Errors;
using Xunit;

namespace InspectionView.Cameras.GenTL.Tests
{
    /// <summary>테스트용 장치 메모리.</summary>
    internal sealed class MemoryPort : IRegisterPort
    {
        public readonly byte[] Memory = new byte[0x20000];
        public readonly List<(long address, int length)> Writes = new List<(long, int)>();

        public int Reads;

        public void Read(long address, byte[] buffer)
        {
            Reads++;
            if (address < 0 || address + buffer.Length > Memory.Length) throw new VisionException(ErrorCode.DeviceError, "out of range");
            Array.Copy(Memory, address, buffer, 0, buffer.Length);
        }

        public void Write(long address, byte[] buffer)
        {
            Writes.Add((address, buffer.Length));
            Array.Copy(buffer, 0, Memory, address, buffer.Length);
        }

        public void PutBE32(long address, uint v)
        {
            Memory[address] = (byte)(v >> 24);
            Memory[address + 1] = (byte)(v >> 16);
            Memory[address + 2] = (byte)(v >> 8);
            Memory[address + 3] = (byte)v;
        }

        public uint GetBE32(long address)
            => (uint)(Memory[address] << 24 | Memory[address + 1] << 16 | Memory[address + 2] << 8 | Memory[address + 3]);

        public void PutLE32(long address, uint v) => BitConverter.GetBytes(v).CopyTo(Memory, address);
        public uint GetLE32(long address) => BitConverter.ToUInt32(Memory, (int)address);
    }

    public class NodeMapTests
    {
        // GigE 장치처럼 BigEndian 레지스터 위주, 일부 LittleEndian
        private const string Xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<RegisterDescription ModelName=""TestCam"" VendorName=""Acme"" xmlns=""http://www.genicam.org/GenApi/Version_1_1"">
  <Category Name=""Root""><pFeature>ImageFormatControl</pFeature></Category>
  <Category Name=""ImageFormatControl""><pFeature>Width</pFeature><pFeature>PixelFormat</pFeature></Category>

  <Integer Name=""TLParamsLocked""><Value>0</Value><Min>0</Min><Max>1</Max></Integer>

  <Integer Name=""Width"">
    <Description>Image width.</Description>
    <pIsLocked>TLParamsLocked</pIsLocked>
    <pValue>WidthReg</pValue>
    <Min>16</Min>
    <pMax>WidthMaxReg</pMax>
    <Inc>8</Inc>
  </Integer>
  <IntReg Name=""WidthReg""><Address>0x100</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort><Sign>Unsigned</Sign><Endianess>BigEndian</Endianess></IntReg>
  <IntReg Name=""WidthMaxReg""><Address>0x104</Address><Length>4</Length><AccessMode>RO</AccessMode><pPort>Device</pPort><Endianess>BigEndian</Endianess></IntReg>

  <Integer Name=""Height""><pValue>HeightReg</pValue></Integer>
  <IntReg Name=""HeightReg""><Address>0x108</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort><Endianess>BigEndian</Endianess></IntReg>

  <IntSwissKnife Name=""PayloadSize"">
    <pVariable Name=""W"">Width</pVariable>
    <pVariable Name=""H"">Height</pVariable>
    <Formula>W * H</Formula>
  </IntSwissKnife>

  <Group Comment=""Trigger"">
    <Enumeration Name=""TriggerMode"">
      <EnumEntry Name=""EnumEntry_TriggerMode_Off""><Value>0</Value></EnumEntry>
      <EnumEntry Name=""On""><Value>1</Value></EnumEntry>
      <pValue>TriggerModeReg</pValue>
    </Enumeration>
    <MaskedIntReg Name=""TriggerModeReg""><Address>0x200</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort><Bit>31</Bit><Endianess>BigEndian</Endianess></MaskedIntReg>
  </Group>

  <Enumeration Name=""PixelFormat"">
    <EnumEntry Name=""Mono8""><Value>0x01080001</Value></EnumEntry>
    <EnumEntry Name=""Mono12""><Value>0x01100005</Value><pIsAvailable>Mono12Avail</pIsAvailable></EnumEntry>
    <pValue>PixelFormatReg</pValue>
  </Enumeration>
  <IntReg Name=""PixelFormatReg""><Address>0x204</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort><Endianess>BigEndian</Endianess></IntReg>
  <Integer Name=""Mono12Avail""><Value>0</Value></Integer>

  <Float Name=""ExposureTime""><pValue>ExposureConv</pValue><Min>10</Min><Max>1000000</Max><Unit>us</Unit></Float>
  <Converter Name=""ExposureConv"">
    <pVariable Name=""STEP"">ExposureStep</pVariable>
    <FormulaTo>FROM / STEP</FormulaTo>
    <FormulaFrom>TO * STEP</FormulaFrom>
    <pValue>ExposureRaw</pValue>
    <Slope>Increasing</Slope>
  </Converter>
  <Float Name=""ExposureStep""><Value>0.5</Value></Float>
  <IntReg Name=""ExposureRaw""><Address>0x300</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort><Endianess>LittleEndian</Endianess></IntReg>

  <Float Name=""Gain""><pValue>GainReg</pValue></Float>
  <FloatReg Name=""GainReg""><Address>0x400</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort><Endianess>BigEndian</Endianess></FloatReg>

  <Boolean Name=""ReverseX""><pValue>ReverseXReg</pValue><OnValue>1</OnValue><OffValue>0</OffValue></Boolean>
  <MaskedIntReg Name=""ReverseXReg""><Address>0x500</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort><LSB>0</LSB><MSB>0</MSB><Endianess>LittleEndian</Endianess></MaskedIntReg>

  <Command Name=""AcquisitionStart""><pValue>AcqStartReg</pValue><CommandValue>1</CommandValue></Command>
  <IntReg Name=""AcqStartReg""><Address>0x600</Address><Length>4</Length><AccessMode>WO</AccessMode><pPort>Device</pPort><Endianess>BigEndian</Endianess></IntReg>

  <String Name=""DeviceModelName""><pValue>ModelReg</pValue></String>
  <StringReg Name=""ModelReg""><Address>0x700</Address><Length>16</Length><AccessMode>RO</AccessMode><pPort>Device</pPort></StringReg>

  <StructReg Comment=""Status"">
    <Address>0x800</Address><Length>4</Length><AccessMode>RO</AccessMode><pPort>Device</pPort><Endianess>BigEndian</Endianess>
    <StructEntry Name=""StatusLow""><LSB>31</LSB><MSB>24</MSB></StructEntry>
    <StructEntry Name=""StatusSigned""><LSB>7</LSB><MSB>0</MSB><Sign>Signed</Sign></StructEntry>
  </StructReg>

  <Integer Name=""LutValue""><pIndex>LutIndex</pIndex><pValueIndexed Index=""0"">Lut0</pValueIndexed><ValueIndexed Index=""1"">77</ValueIndexed><pValueDefault>LutDefault</pValueDefault></Integer>
  <Integer Name=""LutIndex""><Value>0</Value></Integer>
  <IntReg Name=""Lut0""><Address>0x900</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort><Endianess>BigEndian</Endianess></IntReg>
  <IntReg Name=""LutDefault""><Address>0x904</Address><Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort><Endianess>BigEndian</Endianess></IntReg>

  <IntReg Name=""IndexedReg"">
    <Address>0xA00</Address>
    <pIndex Offset=""4"">LutIndex</pIndex>
    <Length>4</Length><AccessMode>RW</AccessMode><pPort>Device</pPort><Endianess>BigEndian</Endianess>
  </IntReg>

  <Port Name=""Device""/>
</RegisterDescription>";

        private static (NodeMap map, MemoryPort port) Create()
        {
            var port = new MemoryPort();
            port.PutBE32(0x100, 640);
            port.PutBE32(0x104, 4096);
            port.PutBE32(0x108, 480);
            port.PutBE32(0x204, 0x01080001);
            port.PutLE32(0x300, 200);
            Encoding.ASCII.GetBytes("TestCam-1\0").CopyTo(port.Memory, 0x700);
            return (NodeMap.Parse(Xml, port), port);
        }

        [Fact]
        public void ParsesNodesIncludingGroupsAndStructEntries()
        {
            var (map, _) = Create();
            Assert.Equal("TestCam", map.ModelName);
            Assert.Equal("Acme", map.VendorName);
            Assert.IsType<EnumerationNode>(map.Find("TriggerMode"));      // Group 안
            Assert.IsType<MaskedIntRegNode>(map.Find("StatusLow"));       // StructEntry
            Assert.NotNull(map.Find("width"));                            // 대소문자 무시 검색
            Assert.Equal(new[] { "ImageFormatControl" }, map.Require<CategoryNode>("Root").Features);
        }

        [Fact]
        public void IntegerReadsBigEndianRegisterWithRange()
        {
            var (map, port) = Create();
            var width = map.Require<IntegerNode>("Width");
            Assert.Equal(640, width.GetInt());
            Assert.Equal(16, width.GetMin().AsLong);
            Assert.Equal(4096, width.GetMax().AsLong);
            Assert.Equal(8, width.GetInc().AsLong);
            Assert.Equal("Image width.", width.Description);

            width.SetValue(Num.Int(1024));
            Assert.Equal(1024u, port.GetBE32(0x100));

            Assert.Throws<VisionException>(() => width.SetValue(Num.Int(8)));      // < Min
            Assert.Throws<VisionException>(() => width.SetValue(Num.Int(5000)));   // > Max
            Assert.Throws<VisionException>(() => width.SetValue(Num.Int(1001)));   // Inc 위반
        }

        [Fact]
        public void LockedFeatureIsNotWritable()
        {
            var (map, _) = Create();
            var width = map.Require<IntegerNode>("Width");
            Assert.True(width.IsWritable);
            map.Require<IntegerNode>("TLParamsLocked").SetValue(Num.Int(1));
            Assert.True(width.IsLocked);
            Assert.False(width.IsWritable);
            Assert.True(width.IsReadable);
            Assert.Throws<VisionException>(() => width.SetValue(Num.Int(512)));
        }

        [Fact]
        public void SwissKnifeUsesVariables()
        {
            var (map, _) = Create();
            Assert.Equal(640L * 480, map.Require<ValueNode>("PayloadSize").GetInt());
            Assert.False(map.Require<ValueNode>("PayloadSize").IsWritable);
        }

        [Fact]
        public void EnumerationOverBigEndianBit()
        {
            var (map, port) = Create();
            var trig = map.Require<EnumerationNode>("TriggerMode");
            Assert.Equal("Off", trig.GetSymbol());                 // EnumEntry_TriggerMode_ 접두사 제거
            trig.SetSymbol("On");
            Assert.Equal(1u, port.GetBE32(0x200));                 // BigEndian Bit 31 = 최하위 비트
            Assert.Equal("On", trig.GetSymbol());
            trig.SetSymbol("off");                                 // 대소문자 무시
            Assert.Equal(0u, port.GetBE32(0x200));
            Assert.Throws<VisionException>(() => trig.SetSymbol("Maybe"));
        }

        [Fact]
        public void EnumEntryAvailability()
        {
            var (map, _) = Create();
            var pf = map.Require<EnumerationNode>("PixelFormat");
            Assert.Equal("Mono8", pf.GetSymbol());
            Assert.Equal(new[] { "Mono8" }, pf.AvailableSymbols);
            Assert.Throws<VisionException>(() => pf.SetSymbol("Mono12"));
            map.Require<IntegerNode>("Mono12Avail").SetValue(Num.Int(1));
            pf.SetSymbol("Mono12");
            Assert.Equal("Mono12", pf.GetSymbol());
        }

        [Fact]
        public void ConverterAppliesFormulas()
        {
            var (map, port) = Create();
            var exp = map.Require<FloatNode>("ExposureTime");
            Assert.Equal(100.0, exp.GetFloat(), 9);                // 200 * 0.5
            exp.SetValue(Num.Real(250));
            Assert.Equal(500u, port.GetLE32(0x300));               // 250 / 0.5
            Assert.Equal(250.0, exp.GetFloat(), 9);
            Assert.Throws<VisionException>(() => exp.SetValue(Num.Real(5)));   // < Min 10
        }

        [Fact]
        public void FloatRegBigEndian()
        {
            var (map, port) = Create();
            var gain = map.Require<FloatNode>("Gain");
            gain.SetValue(Num.Real(6.5));
            var raw = new byte[] { port.Memory[0x403], port.Memory[0x402], port.Memory[0x401], port.Memory[0x400] };
            Assert.Equal(6.5f, BitConverter.ToSingle(raw, 0));
            Assert.Equal(6.5, gain.GetFloat(), 6);
        }

        [Fact]
        public void BooleanOverLittleEndianBitKeepsOtherBits()
        {
            var (map, port) = Create();
            port.PutLE32(0x500, 0xF0);
            var rev = map.Require<BooleanNode>("ReverseX");
            Assert.False(rev.GetBool());
            rev.SetBool(true);
            Assert.Equal(0xF1u, port.GetLE32(0x500));
            Assert.True(rev.GetBool());
        }

        [Fact]
        public void CommandWritesCommandValue()
        {
            var (map, port) = Create();
            var cmd = map.Require<CommandNode>("AcquisitionStart");
            Assert.True(cmd.IsWritable);
            cmd.Execute();
            Assert.Equal(1u, port.GetBE32(0x600));
            Assert.Contains(port.Writes, w => w.address == 0x600 && w.length == 4);
        }

        [Fact]
        public void StringRegReadsUntilNul()
        {
            var (map, _) = Create();
            var s = map.Require<StringNode>("DeviceModelName");
            Assert.Equal("TestCam-1", s.GetString());
            Assert.False(s.IsWritable);
            Assert.Throws<VisionException>(() => s.SetString("x"));
        }

        [Fact]
        public void StructEntriesShareParentRegister()
        {
            var (map, port) = Create();
            port.PutBE32(0x800, 0x0000_80FE);   // 바이트: 00 00 80 FE
            Assert.Equal(0xFE, map.Require<ValueNode>("StatusLow").GetInt());       // BigEndian 비트 31..24 = 최하위 바이트
            Assert.Equal(0, map.Require<ValueNode>("StatusSigned").GetInt());        // 비트 7..0 = 최상위 바이트 (00)
            port.PutBE32(0x800, 0xFF00_0000);
            Assert.Equal(-1, map.Require<ValueNode>("StatusSigned").GetInt());       // 부호 확장
            Assert.False(map.Require<ValueNode>("StatusLow").IsWritable);           // 부모의 AccessMode RO
        }

        [Fact]
        public void IndexedValuesFollowSelector()
        {
            var (map, port) = Create();
            port.PutBE32(0x900, 11);
            port.PutBE32(0x904, 99);
            var lut = map.Require<IntegerNode>("LutValue");
            var index = map.Require<IntegerNode>("LutIndex");
            Assert.Equal(11, lut.GetInt());
            index.SetValue(Num.Int(1));
            Assert.Equal(77, lut.GetInt());          // ValueIndexed 리터럴
            index.SetValue(Num.Int(5));
            Assert.Equal(99, lut.GetInt());          // pValueDefault
        }

        [Fact]
        public void RegisterAddressUsesIndexOffset()
        {
            var (map, port) = Create();
            map.Require<IntegerNode>("LutIndex").SetValue(Num.Int(2));
            map.Require<ValueNode>("IndexedReg").SetValue(Num.Int(42));
            Assert.Equal(42u, port.GetBE32(0xA00 + 2 * 4));
        }

        [Fact]
        public void BatchCachesRegisterReadsUntilWriteOrEnd()
        {
            var (map, port) = Create();
            var width = map.Require<IntegerNode>("Width");

            port.Reads = 0;
            width.GetInt();
            width.GetInt();
            Assert.Equal(2, port.Reads);                 // 배치 밖: 매번 읽는다

            port.Reads = 0;
            using (map.Batch())
            {
                width.GetInt();
                width.GetMax();
                width.GetInt();
                width.GetMax();
                Assert.Equal(2, port.Reads);             // WidthReg, WidthMaxReg 한 번씩
                width.SetValue(Num.Int(512));            // 쓰기는 캐시를 비운다
                Assert.Equal(512, width.GetInt());
            }

            port.PutBE32(0x100, 777);                    // 장치가 스스로 바꾼 값: 배치가 끝났으므로 새로 읽힌다
            Assert.Equal(777, width.GetInt());
        }

        [Fact]
        public void NoCacheRegisterIsAlwaysRead()
        {
            const string xml = @"<RegisterDescription ModelName=""M"" VendorName=""V"">
  <IntReg Name=""Temp""><Address>0x10</Address><Length>4</Length><AccessMode>RO</AccessMode><pPort>Device</pPort><Cachable>NoCache</Cachable><Endianess>BigEndian</Endianess></IntReg>
  <Port Name=""Device""/>
</RegisterDescription>";
            var port = new MemoryPort();
            var map = NodeMap.Parse(xml, port);
            using (map.Batch())
            {
                map.Require<ValueNode>("Temp").GetInt();
                map.Require<ValueNode>("Temp").GetInt();
            }
            Assert.Equal(2, port.Reads);
        }

        [Fact]
        public void UnknownFeatureThrows()
        {
            var (map, _) = Create();
            Assert.Null(map.Find("NoSuchFeature"));
            Assert.Throws<VisionException>(() => map.Require<ValueNode>("NoSuchFeature"));
            Assert.Throws<VisionException>(() => map.Require<ValueNode>("DeviceModelName"));   // String 은 ValueNode 아님
        }
    }
}
