using System;
using System.Collections.Generic;
using InspectionView.Cameras.GenTL.GenICam;
using Xunit;

namespace InspectionView.Cameras.GenTL.Tests
{
    public class FormulaTests
    {
        private static Num Eval(string text, bool floatMode = false, Dictionary<string, Num>? vars = null)
            => Formula.Parse(text).Evaluate(n => vars != null && vars.TryGetValue(n, out var v) ? v : (Num?)null, floatMode);

        [Theory]
        [InlineData("1 + 2 * 3", 7)]
        [InlineData("(1 + 2) * 3", 9)]
        [InlineData("7 / 2", 3)]
        [InlineData("7 % 4", 3)]
        [InlineData("2 ** 3 ** 2", 512)]
        [InlineData("0x10 | 0x01", 17)]
        [InlineData("0xFF & 0x0F", 15)]
        [InlineData("1 << 4", 16)]
        [InlineData("256 >> 4", 16)]
        [InlineData("5 ^ 1", 4)]
        [InlineData("~0", -1)]
        [InlineData("-3 + 5", 2)]
        [InlineData("3 = 3", 1)]
        [InlineData("3 <> 3", 0)]
        [InlineData("3 != 4", 1)]
        [InlineData("2 < 3 && 3 < 2", 0)]
        [InlineData("2 < 3 || 3 < 2", 1)]
        [InlineData("1 ? 10 : 20", 10)]
        [InlineData("0 ? 10 : 1 ? 30 : 40", 30)]
        [InlineData("1 + 2 = 3", 1)]
        [InlineData("ABS(-4)", 4)]
        [InlineData("NEG(4)", -4)]
        [InlineData("SGN(-9)", -1)]
        public void IntegerMode(string text, long expected)
        {
            var v = Eval(text);
            Assert.Equal(expected, v.AsLong);
        }

        [Fact]
        public void FloatModeUsesRealDivisionAndFunctions()
        {
            Assert.Equal(3.5, Eval("7 / 2", floatMode: true).AsDouble, 9);
            Assert.Equal(2.0, Eval("SQRT(4)", floatMode: true).AsDouble, 9);
            Assert.Equal(Math.PI, Eval("PI", floatMode: true).AsDouble, 9);
            Assert.Equal(1.5, Eval("1.5e0", floatMode: true).AsDouble, 9);
            Assert.Equal(1.23, Eval("ROUND(1.2345, 2)", floatMode: true).AsDouble, 9);
            Assert.Equal(-2.0, Eval("FLOOR(-1.5)", floatMode: true).AsDouble, 9);
        }

        [Fact]
        public void VariablesAndDottedNames()
        {
            var vars = new Dictionary<string, Num> { ["W"] = Num.Int(640), ["H"] = Num.Int(480), ["SEL.Entry.On"] = Num.Int(1) };
            Assert.Equal(640 * 480, Eval("W * H", vars: vars).AsLong);
            Assert.Equal(1, Eval("SEL.Entry.On", vars: vars).AsLong);
        }

        [Fact]
        public void VariableShadowsConstant()
        {
            var vars = new Dictionary<string, Num> { ["E"] = Num.Int(5) };
            Assert.Equal(6, Eval("E + 1", vars: vars).AsLong);
        }

        [Fact]
        public void UnknownVariableThrows()
        {
            Assert.Throws<InspectionView.Core.Errors.VisionException>(() => Eval("Missing + 1"));
        }

        [Fact]
        public void SyntaxErrorThrows()
        {
            Assert.Throws<InspectionView.Core.Errors.VisionException>(() => Formula.Parse("1 + (2"));
            Assert.Throws<InspectionView.Core.Errors.VisionException>(() => Formula.Parse("1 ? 2"));
        }
    }
}
