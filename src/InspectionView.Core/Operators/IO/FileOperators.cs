using System;
using System.IO;
using System.Linq;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Registry;
using InspectionView.Core.Types;

namespace InspectionView.Core.Operators.IO
{
    [Operator("ListFiles", Category = "IO", Description = "Lists files in a directory matching a pattern (e.g. '*.png'), sorted by name.")]
    public sealed class ListFiles : OperatorBase
    {
        [Input] public string Directory { get; set; } = "";
        [Input(Default = "*.*")] public string Pattern { get; set; } = "*.*";
        [Input(Description = "Include sub-directories.", Default = false)] public bool Recursive { get; set; }
        [Output(Description = "Full paths.")] public TupleValue? Files { get; private set; }

        protected override void Validate()
        {
            RequireNotEmpty(Directory, nameof(Directory));
            if (!System.IO.Directory.Exists(Directory))
                throw new VisionException(ErrorCode.FileNotFound, $"Directory not found: {Directory}");
        }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
        {
            var files = System.IO.Directory.GetFiles(Directory, string.IsNullOrEmpty(Pattern) ? "*.*" : Pattern,
                    Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Files = files.Length == 0 ? TupleValue.Empty : new TupleValue(files.Cast<object>().ToArray());
        }
    }

    [Operator("FileExists", Category = "IO", Description = "True if the file (or directory) exists.")]
    public sealed class FileExists : OperatorBase
    {
        [Input] public string Path { get; set; } = "";
        [Output] public bool Exists { get; private set; }

        [Backend(BackendKind.Cpu)]
        private void RunCpu(OperatorContext ctx)
            => Exists = !string.IsNullOrEmpty(Path) && (File.Exists(Path) || System.IO.Directory.Exists(Path));
    }
}
