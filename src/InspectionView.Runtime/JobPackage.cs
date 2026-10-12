using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization.Json;
using System.Text;
using InspectionView.Core.Errors;
using InspectionView.Core.Interop;
using InspectionView.Core.Types;
using InspectionView.Script.Runtime;
using OpenCvSharp;
using ErrorCode = InspectionView.Core.Errors.ErrorCode;

namespace InspectionView.Runtime
{
    /// <summary>
    /// Job 패키지 파일(.ivjob, zip) 입출력.
    /// <code>
    /// manifest.json          JobManifest
    /// script.ivs             스크립트
    /// params/values.json     { "Name": "&lt;literal&gt;" }
    /// roi/&lt;Name&gt;.rgn         Region (RegionIO)
    /// models/&lt;Name&gt;.png|tif  Image (8bit → png, 그 외 → tif)
    /// </code>
    /// </summary>
    public static class JobPackage
    {
        public const string Extension = ".ivjob";
        private const string ManifestEntry = "manifest.json";
        private const string ParamsEntry = "params/values.json";

        public static string ImageExtension(Image image)
            => image.PixelType == PixelType.UInt8 && image.Channels <= 4 ? ".png" : ".tif";

        // ------------------------------------------------------------------
        // 저장
        // ------------------------------------------------------------------

        public static void Save(string path, JobDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                Save(fs, doc);
        }

        public static void Save(Stream stream, JobDocument doc)
        {
            doc.Manifest.ModifiedAt = DateTime.Now.ToString("o");
            doc.Manifest.FormatVersion = JobManifest.CurrentFormatVersion;
            doc.Manifest.ToolVersion = typeof(JobPackage).Assembly.GetName().Version?.ToString() ?? "";
            doc.SyncManifestResources();

            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteText(zip, ManifestEntry, SerializeManifest(doc.Manifest));
                WriteText(zip, doc.Manifest.ScriptFile, doc.Script ?? "");

                if (doc.Parameters.Count > 0)
                {
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var kv in doc.Parameters)
                        map[kv.Key] = ScriptValues.ToLiteral(kv.Value);
                    WriteText(zip, ParamsEntry, SerializeStringMap(map));
                }

                foreach (var kv in doc.Regions)
                {
                    var entry = zip.CreateEntry($"roi/{kv.Key}.rgn", CompressionLevel.Optimal);
                    using (var es = entry.Open())
                        RegionIO.Write(es, kv.Value);
                }

                foreach (var kv in doc.Images)
                {
                    var ext = ImageExtension(kv.Value);
                    var entry = zip.CreateEntry($"models/{kv.Key}{ext}", CompressionLevel.NoCompression);
                    byte[] bytes;
                    using (var mat = MatInterop.AsMat(kv.Value))
                    {
                        if (!Cv2.ImEncode(ext, mat, out bytes))
                            throw new VisionException(ErrorCode.FileWriteFailed, $"Could not encode model image '{kv.Key}'.");
                    }
                    using (var es = entry.Open())
                        es.Write(bytes, 0, bytes.Length);
                }
            }
        }

        // ------------------------------------------------------------------
        // 로드
        // ------------------------------------------------------------------

        public static JobDocument Load(string path)
        {
            if (!File.Exists(path))
                throw VisionException.FileNotFound(path);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                return Load(fs);
        }

        public static JobDocument Load(Stream stream)
        {
            var doc = new JobDocument();
            try
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
                {
                    var manifestEntry = zip.GetEntry(ManifestEntry)
                                        ?? throw new VisionException(ErrorCode.UnsupportedFormat, "Not a job package: manifest.json is missing.");
                    doc.Manifest = DeserializeManifest(ReadText(manifestEntry));
                    if (doc.Manifest.FormatVersion > JobManifest.CurrentFormatVersion)
                        throw new VisionException(ErrorCode.UnsupportedFormat, $"Job format version {doc.Manifest.FormatVersion} is newer than supported ({JobManifest.CurrentFormatVersion}).");

                    var scriptEntry = zip.GetEntry(doc.Manifest.ScriptFile);
                    doc.Script = scriptEntry != null ? ReadText(scriptEntry) : "";

                    var paramsEntry = zip.GetEntry(ParamsEntry);
                    if (paramsEntry != null)
                    {
                        foreach (var kv in DeserializeStringMap(ReadText(paramsEntry)))
                            doc.SetParameter(kv.Key, ScriptValues.ParseLiteral(kv.Value));
                    }

                    foreach (var res in doc.Manifest.Resources)
                    {
                        if (res.Type == TerminalType.Tuple)
                            continue;

                        var entry = zip.GetEntry(res.Path);
                        if (entry == null)
                            throw new VisionException(ErrorCode.FileReadFailed, $"Resource '{res.Name}' is missing from the package ({res.Path}).");

                        if (res.Type == TerminalType.Region)
                        {
                            using (var es = entry.Open())
                            using (var region = RegionIO.Read(es))
                                doc.SetRegion(res.Name, region);
                        }
                        else if (res.Type == TerminalType.Image)
                        {
                            byte[] bytes = ReadBytes(entry);
                            using (var mat = Cv2.ImDecode(bytes, ImreadModes.Unchanged))
                            {
                                if (mat.Empty())
                                    throw new VisionException(ErrorCode.FileReadFailed, $"Could not decode model image '{res.Name}'.");
                                using (var img = MatInterop.ToImage(mat))
                                    doc.SetImage(res.Name, img);
                            }
                        }
                    }
                }
            }
            catch
            {
                doc.Dispose();
                throw;
            }
            return doc;
        }

        // ------------------------------------------------------------------
        // JSON (DataContractJsonSerializer: net48 / net8 공통, 외부 의존 없음)
        // ------------------------------------------------------------------

        public static string SerializeManifest(JobManifest manifest)
        {
            var serializer = new DataContractJsonSerializer(typeof(JobManifest));
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, manifest);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static JobManifest DeserializeManifest(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(JobManifest));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                try
                {
                    return (JobManifest)(serializer.ReadObject(ms) ?? new JobManifest());
                }
                catch (Exception ex)
                {
                    throw new VisionException(ErrorCode.UnsupportedFormat, "Invalid manifest.json: " + ex.Message, ex);
                }
            }
        }

        private static string SerializeStringMap(Dictionary<string, string> map)
        {
            var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, map);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private static Dictionary<string, string> DeserializeStringMap(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (Dictionary<string, string>)(serializer.ReadObject(ms) ?? new Dictionary<string, string>());
        }

        // ------------------------------------------------------------------
        // zip 유틸
        // ------------------------------------------------------------------

        private static void WriteText(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var es = entry.Open())
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                es.Write(bytes, 0, bytes.Length);
            }
        }

        private static string ReadText(ZipArchiveEntry entry)
        {
            using (var es = entry.Open())
            using (var reader = new StreamReader(es, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private static byte[] ReadBytes(ZipArchiveEntry entry)
        {
            using (var es = entry.Open())
            using (var ms = new MemoryStream())
            {
                es.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }
}
