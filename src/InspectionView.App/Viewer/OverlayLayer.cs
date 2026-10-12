using System;
using System.Collections.Generic;
using InspectionView.Core.Memory;
using InspectionView.Core.Types;
using OpenTK.Graphics.OpenGL4;

namespace InspectionView.App.Viewer
{
    /// <summary>오버레이 색 (0~1).</summary>
    public readonly struct OverlayColor
    {
        public float R { get; }
        public float G { get; }
        public float B { get; }
        public float A { get; }

        public OverlayColor(float r, float g, float b, float a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public OverlayColor WithAlpha(float a) => new OverlayColor(R, G, B, a);

        /// <summary>Halcon 기본 색 순서와 비슷한 팔레트.</summary>
        public static readonly OverlayColor[] Palette =
        {
            new OverlayColor(0.0f, 1.0f, 0.0f, 1f),
            new OverlayColor(1.0f, 0.0f, 0.0f, 1f),
            new OverlayColor(0.2f, 0.4f, 1.0f, 1f),
            new OverlayColor(0.0f, 1.0f, 1.0f, 1f),
            new OverlayColor(1.0f, 0.0f, 1.0f, 1f),
            new OverlayColor(1.0f, 1.0f, 0.0f, 1f),
            new OverlayColor(1.0f, 0.5f, 0.3f, 1f),
            new OverlayColor(0.5f, 0.4f, 1.0f, 1f),
            new OverlayColor(0.4f, 1.0f, 0.6f, 1f),
            new OverlayColor(1.0f, 0.7f, 0.0f, 1f),
        };

        public static OverlayColor Roi => new OverlayColor(1f, 0.9f, 0.1f, 1f);
        public static OverlayColor Profile => new OverlayColor(0.3f, 0.8f, 1f, 1f);
    }

    /// <summary>
    /// 벡터 오버레이 한 층: Region(채움 사각형들), XLD(폴리라인), ROI(사각형 테두리).
    /// 정점은 생성 시 CPU 에서 만들고, GPU 버퍼는 첫 Draw 에서 올린다 (GL 컨텍스트 필요).
    /// 원본 아이코닉 객체는 참조하지 않으므로 수명 문제가 없다.
    /// </summary>
    public sealed class OverlayLayer : IDisposable
    {
        private float[]? _vertices;
        private int _vao;
        private int _vbo;

        public string Name { get; }
        public OverlayColor Color { get; set; }
        public PrimitiveType Mode { get; }
        public int VertexCount { get; }
        public bool Visible { get; set; } = true;
        public float LineWidth { get; set; } = 1f;

        private OverlayLayer(string name, OverlayColor color, PrimitiveType mode, float[] vertices)
        {
            Name = name;
            Color = color;
            Mode = mode;
            _vertices = vertices;
            VertexCount = vertices.Length / 2;
        }

        /// <summary>Region 을 run 단위 사각형(삼각형 2개)으로 채운다. 반투명.</summary>
        public static OverlayLayer FromRegion(Region region, OverlayColor color, string name = "Region")
        {
            var runs = region.Runs;
            var v = new float[runs.Count * 12];
            int i = 0;
            foreach (var r in runs)
            {
                float x0 = r.ColBegin, x1 = r.ColEnd + 1, y0 = r.Row, y1 = r.Row + 1;
                v[i++] = x0; v[i++] = y0; v[i++] = x1; v[i++] = y0; v[i++] = x1; v[i++] = y1;
                v[i++] = x0; v[i++] = y0; v[i++] = x1; v[i++] = y1; v[i++] = x0; v[i++] = y1;
            }
            return new OverlayLayer(name, color.WithAlpha(0.45f), PrimitiveType.Triangles, v);
        }

        /// <summary>XLD 를 픽셀 중심(+0.5)을 지나는 폴리라인으로 그린다.</summary>
        public static OverlayLayer FromXld(XldContour xld, OverlayColor color, string name = "XLD")
        {
            var v = new float[xld.Count * 2];
            for (int k = 0; k < xld.Count; k++)
            {
                v[2 * k] = (float)xld.X[k] + 0.5f;
                v[2 * k + 1] = (float)xld.Y[k] + 0.5f;
            }
            return new OverlayLayer(name, color, xld.IsClosed ? PrimitiveType.LineLoop : PrimitiveType.LineStrip, v) { LineWidth = 1.5f };
        }

        /// <summary>사각형 테두리 (배타 경계).</summary>
        public static OverlayLayer FromRect(RectI rect, OverlayColor color, string name = "ROI")
        {
            float x0 = rect.X, y0 = rect.Y, x1 = rect.Right, y1 = rect.Bottom;
            var v = new float[] { x0, y0, x1, y0, x1, y1, x0, y1 };
            return new OverlayLayer(name, color, PrimitiveType.LineLoop, v) { LineWidth = 1.5f };
        }

        /// <summary>선분 (프로파일 라인 등). 좌표는 이미지 픽셀 단위.</summary>
        public static OverlayLayer FromLine(PointD a, PointD b, OverlayColor color, string name = "Line")
        {
            var v = new float[] { (float)a.X, (float)a.Y, (float)b.X, (float)b.Y };
            return new OverlayLayer(name, color, PrimitiveType.Lines, v) { LineWidth = 1.5f };
        }

        /// <summary>값(Region / XLD / 객체 배열)을 층 목록으로 바꾼다. 배열은 원소마다 다른 색.</summary>
        public static List<OverlayLayer> FromValue(object value, int colorIndex, string name)
        {
            var layers = new List<OverlayLayer>();
            switch (value)
            {
                case Region r when !r.IsDisposed:
                    layers.Add(FromRegion(r, OverlayColor.Palette[colorIndex % OverlayColor.Palette.Length], name));
                    break;
                case XldContour x when !x.IsDisposed:
                    layers.Add(FromXld(x, OverlayColor.Palette[colorIndex % OverlayColor.Palette.Length], name));
                    break;
                case IObjectArray arr when !(arr is IconicObject io && io.IsDisposed):
                    for (int i = 0; i < arr.Count; i++)
                        layers.AddRange(FromValue(arr.ItemAt(i), colorIndex + i, $"{name}[{i}]"));
                    break;
            }
            return layers;
        }

        public void Draw(int colorUniform)
        {
            if (!Visible || VertexCount == 0)
                return;

            if (_vao == 0 && _vertices != null)
            {
                _vao = GL.GenVertexArray();
                _vbo = GL.GenBuffer();
                GL.BindVertexArray(_vao);
                GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
                GL.BufferData(BufferTarget.ArrayBuffer, _vertices.Length * sizeof(float), _vertices, BufferUsageHint.StaticDraw);
                GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
                GL.EnableVertexAttribArray(0);
                GL.BindVertexArray(0);
                _vertices = null;
            }
            if (_vao == 0)
                return;

            GL.Uniform4(colorUniform, Color.R, Color.G, Color.B, Color.A);
            GL.LineWidth(LineWidth);
            GL.BindVertexArray(_vao);
            GL.DrawArrays(Mode, 0, VertexCount);
            GL.BindVertexArray(0);
        }

        public void Dispose()
        {
            if (_vao != 0)
            {
                GL.DeleteBuffer(_vbo);
                GL.DeleteVertexArray(_vao);
                _vao = 0;
                _vbo = 0;
            }
            _vertices = null;
        }
    }
}
