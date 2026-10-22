using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using InspectionView.Core.Errors;

namespace InspectionView.Cameras.GenTL.GenICam
{
    /// <summary>
    /// 장치 설명 XML 가져오기. URL 형식:
    /// <c>Local:파일이름.zip;주소(hex);길이(hex)[?SchemaVersion=x]</c> (장치 메모리),
    /// <c>File:///C:/경로/파일.xml</c> (로컬 파일). .zip 이면 첫 .xml 항목을 꺼낸다.
    /// </summary>
    public static class DeviceDescription
    {
        /// <summary>한 번에 읽는 최대 바이트 (GigE READMEM 한계를 넘지 않도록 작게).</summary>
        public const int ReadChunk = 512;

        public static string Load(string url, IRegisterPort port)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new VisionException(ErrorCode.CameraOpenFailed, "The device did not report a GenICam description URL.");

            if (TryParseLocal(url, out string fileName, out long address, out long length))
                return Extract(ReadMemory(port, address, length), fileName);

            string trimmed = StripQuery(url.Trim());
            if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                string path = FilePath(trimmed);
                if (!File.Exists(path))
                    throw new VisionException(ErrorCode.CameraOpenFailed, $"GenICam description file not found: {path}");
                return Extract(File.ReadAllBytes(path), path);
            }
            throw new VisionException(ErrorCode.CameraOpenFailed, $"Unsupported GenICam description URL '{url}' (only Local: and File: are supported).");
        }

        /// <summary><c>Local:name;addr;len</c> 해석 (주소/길이는 16진수, 0x 접두사 허용).</summary>
        public static bool TryParseLocal(string url, out string fileName, out long address, out long length)
        {
            fileName = "";
            address = 0;
            length = 0;
            string s = StripQuery(url.Trim());
            if (!s.StartsWith("local:", StringComparison.OrdinalIgnoreCase)) return false;
            var parts = s.Substring("local:".Length).Split(';');
            if (parts.Length < 3) return false;
            fileName = parts[0].Trim().TrimStart('/');
            address = ParseHex(parts[1]);
            length = ParseHex(parts[2]);
            return length > 0;
        }

        private static long ParseHex(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return long.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static string StripQuery(string s)
        {
            int q = s.IndexOf('?');
            return q >= 0 ? s.Substring(0, q) : s;
        }

        private static string FilePath(string fileUrl)
        {
            string rest = fileUrl.Substring("file:".Length);
            while (rest.StartsWith("/", StringComparison.Ordinal)) rest = rest.Substring(1);
            rest = Uri.UnescapeDataString(rest).Replace('|', ':');
            // "C:/..." 는 그대로, 리눅스 경로("/home/...")는 앞의 / 를 되살린다
            if (rest.Length > 1 && rest[1] == ':') return rest;
            return "/" + rest;
        }

        public static byte[] ReadMemory(IRegisterPort port, long address, long length)
        {
            if (length <= 0 || length > 64L * 1024 * 1024)
                throw new VisionException(ErrorCode.CameraOpenFailed, $"Invalid GenICam description size {length}.");
            var data = new byte[length];
            long done = 0;
            while (done < length)
            {
                int n = (int)Math.Min(ReadChunk, length - done);
                // 4바이트 정렬 길이로 읽고 필요한 만큼만 쓴다 (실패하면 정확한 길이로 재시도)
                int aligned = (n + 3) & ~3;
                var chunk = new byte[aligned];
                try
                {
                    port.Read(address + done, chunk);
                }
                catch when (aligned != n)
                {
                    chunk = new byte[n];
                    port.Read(address + done, chunk);
                }
                Array.Copy(chunk, 0, data, done, n);
                done += n;
            }
            return data;
        }

        /// <summary>zip 이면 첫 .xml 항목, 아니면 그대로 UTF-8 텍스트.</summary>
        public static string Extract(byte[] data, string fileName)
        {
            bool isZip = data.Length >= 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4;
            if (isZip || fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using (var ms = new MemoryStream(data))
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        ?? zip.Entries.FirstOrDefault();
                    if (entry == null)
                        throw new VisionException(ErrorCode.CameraOpenFailed, $"GenICam description archive '{fileName}' is empty.");
                    using (var s = entry.Open())
                    using (var r = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                        return r.ReadToEnd();
                }
            }
            int end = data.Length;
            while (end > 0 && data[end - 1] == 0) end--;
            using (var r = new StreamReader(new MemoryStream(data, 0, end), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                return r.ReadToEnd();
        }
    }
}
