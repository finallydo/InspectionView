using System;
using System.Collections.Generic;
using System.Threading;
using InspectionView.Core.Backends;
using InspectionView.Core.Errors;
using InspectionView.Core.Types;
using InspectionView.Script.Runtime;
using Xunit;

namespace InspectionView.Script.Tests
{
    public class EngineTests
    {
        private static ScriptEngine NewEngine() => new ScriptEngine(settings: new BackendSettings());

        /// <summary>스크립트를 끝까지 실행하고 엔진을 돌려준다.</summary>
        private static ScriptEngine RunToEnd(string src)
        {
            var engine = NewEngine();
            Assert.True(engine.Load(src), string.Join("\n", engine.Diagnostics));
            Assert.True(engine.Run());
            Assert.True(engine.WaitForBreak(10000), "timeout");
            Assert.True(engine.State == EngineState.Finished, $"state={engine.State} error={engine.LastError}");
            return engine;
        }

        private static TupleValue T(ScriptEngine e, string name) => Assert.IsType<TupleValue>(e.Variables.Get(name));

        private static Image MakeTestImage()
        {
            var img = Image.Create(64, 64, PixelType.UInt8, clear: true);
            for (int y = 5; y < 15; y++)
                for (int x = 5; x < 15; x++)
                    img.SetPixel(x, y, 200);
            for (int y = 30; y < 35; y++)
                for (int x = 40; x < 60; x++)
                    img.SetPixel(x, y, 200);
            return img;
        }

        [Fact]
        public void Arithmetic_And_Tuples()
        {
            using var e = RunToEnd(@"
A = 1 + 2 * 3
B = (1 + 2) * 3
C = 7 / 2
D = 7 % 3
E = -A
F = [1, 2, 3] * 2
G = 'x' + 1 + '-' + 2.5
H = [10, 20, 30]
I = H[1]
J = H[[0, 2]]
K = Len(H) + H.Length
L = 3 > 2 and 1 == 1
M = not L or false
N = Max([4, 9, 2])
O = Sqrt(16)
P = Round(2.5)
Q = Int('42') + Real('0.5')
R = 10 - 0x10
S = true
");
            Assert.Equal(7L, T(e, "A").L());
            Assert.Equal(9L, T(e, "B").L());
            Assert.Equal(3.5, T(e, "C").D());
            Assert.Equal(1L, T(e, "D").L());
            Assert.Equal(-7L, T(e, "E").L());
            Assert.Equal(new long[] { 2, 4, 6 }, T(e, "F").ToLongArray());
            Assert.Equal("x1-2.5", T(e, "G").S());
            Assert.Equal(20L, T(e, "I").L());
            Assert.Equal(new long[] { 10, 30 }, T(e, "J").ToLongArray());
            Assert.Equal(6L, T(e, "K").L());
            Assert.True(T(e, "L").B());
            Assert.False(T(e, "M").B());
            Assert.Equal(9L, T(e, "N").L());
            Assert.Equal(4.0, T(e, "O").D());
            Assert.Equal(3L, T(e, "P").L());
            Assert.Equal(42.5, T(e, "Q").D());
            Assert.Equal(-6L, T(e, "R").L());
            Assert.True(T(e, "S").B());
        }

        [Fact]
        public void ControlFlow_IfForWhileBreakContinue()
        {
            using var e = RunToEnd(@"
Sum = 0
for I = 1 to 10
    if I % 2 == 0
        continue
    endif
    if I > 7
        break
    endif
    Sum = Sum + I
endfor
// 1 + 3 + 5 + 7 = 16
Down = []
for J = 10 to 1 by -3
    Down = [Down, J]
endfor
Real = []
for K = 0.0 to 1.0 by 0.5
    Real = [Real, K]
endfor
Count = 0
while Count < 5
    Count = Count + 1
endwhile
Grade = ''
Score = 85
if Score >= 90
    Grade = 'A'
elseif Score >= 80
    Grade = 'B'
else
    Grade = 'C'
endif
Empty = 0
for X = 5 to 1
    Empty = Empty + 1
endfor
");
            Assert.Equal(16L, T(e, "Sum").L());
            Assert.Equal(new long[] { 10, 7, 4, 1 }, T(e, "Down").ToLongArray());
            Assert.Equal(new[] { 0.0, 0.5, 1.0 }, T(e, "Real").ToDoubleArray());
            Assert.Equal(5L, T(e, "Count").L());
            Assert.Equal("B", T(e, "Grade").S());
            Assert.Equal(0L, T(e, "Empty").L());
            Assert.Equal(9L, T(e, "I").L());          // break 시점의 값
        }

        [Fact]
        public void Operators_RunThroughRegistry_WithReferenceCounting()
        {
            using var engine = NewEngine();
            var img = MakeTestImage();
            engine.Variables.Set("Img", img);
            img.Release();                                   // 이제 변수만 소유
            Assert.Equal(1, img.RefCount);

            Assert.True(engine.Load(@"
Region = Threshold(Img, 128, 255)
Parts = Connection(Region)
N = Len(Parts)
First = Parts[0]
Area0 = First.Area
Big = SelectShape(Parts, 'Area', 150, 1000)
Small = SelectShape(Parts, Feature = 'Area', Max = 150)
W = Img.Width
Part = CropPart(Img, 0, 0, 16, 16)
Region = Threshold(Part, 128, 255)
"), string.Join("\n", engine.Diagnostics));

            Assert.True(engine.Run());
            Assert.True(engine.WaitForBreak(10000));
            Assert.Equal(EngineState.Finished, engine.State);

            var parts = Assert.IsType<ObjectArray<Region>>(engine.Variables.Get("Parts"));
            Assert.Equal(2, parts.Count);
            Assert.Equal(2L, T(engine, "N").L());
            Assert.Equal(100L, T(engine, "Area0").L());
            Assert.Same(parts[0], engine.Variables.Get("First"));
            Assert.Empty(Assert.IsType<ObjectArray<Region>>(engine.Variables.Get("Big")));
            Assert.Equal(2, Assert.IsType<ObjectArray<Region>>(engine.Variables.Get("Small")).Count);
            Assert.Equal(64L, T(engine, "W").L());

            // 참조 카운트: parts[0] 은 Parts, First, Small 이 소유
            Assert.Equal(3, parts[0].RefCount);

            // Region 은 재대입되어 새 객체 (16x16 안의 100 픽셀)
            var region = Assert.IsType<Region>(engine.Variables.Get("Region"));
            Assert.Equal(100, region.Area);
            Assert.Equal(1, region.RefCount);

            // 프로파일러에 연산자별 기록
            Assert.Equal(6, engine.Profiler.Records.Count);
            Assert.All(engine.Profiler.Records, r => Assert.Equal(ErrorCode.Ok, r.Code));

            engine.ClearVariables();
            Assert.True(img.IsDisposed);
            Assert.True(parts.IsDisposed);
            Assert.True(region.IsDisposed);
        }

        [Fact]
        public void Breakpoints_Step_And_Resume()
        {
            using var e = NewEngine();
            var lines = new List<int>();
            e.LineChanged += l => { lock (lines) lines.Add(l); };

            Assert.True(e.Load("A = 1\nB = 2\nC = 3\nD = 4\nE = 5\n"));
            e.AddBreakpoint(3);

            Assert.True(e.Run());
            Assert.True(e.WaitForBreak(10000));
            Assert.Equal(EngineState.Paused, e.State);
            Assert.Equal(3, e.CurrentLine);
            Assert.True(e.Variables.Contains("B"));
            Assert.False(e.Variables.Contains("C"));

            Assert.True(e.Step());
            Assert.True(e.WaitForBreak(10000));
            Assert.Equal(EngineState.Paused, e.State);
            Assert.Equal(4, e.CurrentLine);
            Assert.True(e.Variables.Contains("C"));

            // 브레이크포인트에서 이어서 실행하면 같은 줄에서 다시 멈추지 않는다
            e.AddBreakpoint(4);
            Assert.True(e.Run());
            Assert.True(e.WaitForBreak(10000));
            Assert.Equal(EngineState.Finished, e.State);
            Assert.Equal(-1, e.CurrentLine);
            Assert.True(e.Variables.Contains("E"));

            // Finished 상태에서 Run 하면 처음부터
            Assert.True(e.Run());
            Assert.True(e.WaitForBreak(10000));
            Assert.Equal(EngineState.Paused, e.State);
            Assert.Equal(3, e.CurrentLine);
        }

        [Fact]
        public void Step_InsideLoops_StopsAtEachStatement()
        {
            using var e = NewEngine();
            Assert.True(e.Load("S = 0\nfor I = 1 to 2\n    S = S + I\nendfor\nT = S\n"));

            var visited = new List<int>();
            for (int k = 0; k < 20 && e.State != EngineState.Finished; k++)
            {
                Assert.True(e.Step());
                Assert.True(e.WaitForBreak(10000));
                visited.Add(e.CurrentLine);
            }

            Assert.Equal(EngineState.Finished, e.State);
            // 1 → 2(for) → 3 → 4(endfor) → 3 → 4(endfor) → 5 → 끝
            Assert.Equal(new[] { 2, 3, 4, 3, 4, 5, -1 }, visited);
            Assert.Equal(3L, T(e, "T").L());
        }

        [Fact]
        public void RuntimeError_StopsAtLine_AndCanResume()
        {
            using var e = NewEngine();
            ScriptRuntimeException? reported = null;
            e.ErrorOccurred += ex => reported = ex;

            Assert.True(e.Load("A = 1\nB = A + Missing\nC = B * 2\n"));
            Assert.True(e.Run());
            Assert.True(e.WaitForBreak(10000));

            Assert.Equal(EngineState.Error, e.State);
            Assert.Equal(2, e.CurrentLine);
            Assert.NotNull(reported);
            Assert.Equal(2, reported!.Line);
            Assert.Contains("Missing", reported.Message);

            // 변수를 정의해 주고 다시 실행하면 그 줄부터 재시도
            e.Variables.Set("Missing", new TupleValue(10L));
            Assert.True(e.Run());
            Assert.True(e.WaitForBreak(10000));
            Assert.Equal(EngineState.Finished, e.State);
            Assert.Equal(22L, T(e, "C").L());
        }

        [Fact]
        public void OperatorError_CarriesErrorCode()
        {
            using var e = NewEngine();
            Assert.True(e.Load("Img = ReadImage('Z:/nope/missing.png')\n"));
            e.Run();
            e.WaitForBreak(10000);

            Assert.Equal(EngineState.Error, e.State);
            Assert.Equal(ErrorCode.FileNotFound, e.LastError!.Code);
            Assert.Equal(1, e.LastError.Line);
            Assert.False(e.Variables.Contains("Img"));
        }

        [Fact]
        public void Stop_InterruptsInfiniteLoop()
        {
            using var e = NewEngine();
            Assert.True(e.Load("N = 0\nwhile true\n    N = N + 1\nendwhile\n"));
            Assert.True(e.Run());

            Thread.Sleep(100);
            Assert.Equal(EngineState.Running, e.State);
            e.Stop();
            Assert.True(e.WaitForBreak(10000));

            Assert.Equal(EngineState.Paused, e.State);
            Assert.True(T(e, "N").L() > 0);
            Assert.True(e.CurrentLine >= 2 && e.CurrentLine <= 4);

            e.Reset();
            Assert.Equal(EngineState.Idle, e.State);
            Assert.Equal(1, e.CurrentLine);
        }

        [Fact]
        public void SetNextLine_MovesProgramCounter()
        {
            using var e = NewEngine();
            Assert.True(e.Load("A = 1\nB = 2\nC = 3\n"));
            Assert.True(e.SetNextLine(3));
            Assert.Equal(EngineState.Paused, e.State);

            Assert.True(e.Run());
            Assert.True(e.WaitForBreak(10000));
            Assert.Equal(EngineState.Finished, e.State);
            Assert.False(e.Variables.Contains("A"));
            Assert.True(e.Variables.Contains("C"));
        }

        [Fact]
        public void Print_RaisesOutput_AndFastModeSuppressesEvents()
        {
            using var e = NewEngine();
            var output = new List<string>();
            int lineEvents = 0, varEvents = 0;
            e.Output += s => output.Add(s);
            e.LineChanged += _ => lineEvents++;
            e.Variables.Changed += (_, __) => varEvents++;

            e.Options.SetFastMode(true);
            Assert.True(e.Load("A = 1\nPrint('value', A, [1, 2])\n"));
            e.Run();
            e.WaitForBreak(10000);

            Assert.Equal(EngineState.Finished, e.State);
            Assert.Single(output);
            Assert.Equal("value 1 [1, 2]", output[0]);
            Assert.Equal(0, varEvents);
            Assert.Equal(1, lineEvents);                 // 멈출 때 한 번만
            Assert.Empty(e.Profiler.Records);
        }

        [Fact]
        public void Load_RejectsSyntaxErrors_AndReportsLines()
        {
            using var e = NewEngine();
            Assert.False(e.Load("A = 1\nB = \n"));
            Assert.True(e.HasErrors);
            Assert.Equal(2, e.Diagnostics[0].Line);
            Assert.False(e.Run());
        }
    }
}
