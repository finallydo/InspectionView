using System;
using System.Collections.Generic;
using InspectionView.Core.Types;
using OpenTK.Graphics.OpenGL4;
using GlPixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;
using GlPixelType = OpenTK.Graphics.OpenGL4.PixelType;
using PixelType = InspectionView.Core.Types.PixelType;

namespace InspectionView.App.Viewer
{
    /// <summary>
    /// 이미지 하나를 GPU 텍스처 타일로 올린 것. 텍스처 최대 크기 제한에 맞춰 타일로 나누고,
    /// 각 타일에 밉맵(피라미드)을 만들어 축소 표시 품질을 확보한다.
    /// 모든 메서드는 GL 컨텍스트가 현재인 상태(Render 콜백 안)에서 호출해야 한다.
    /// </summary>
    public sealed unsafe class ImageTextureSet : IDisposable
    {
        private struct Tile
        {
            public int Texture;
            public int Vao;
            public int Vbo;
            public int X, Y, W, H;
        }

        private readonly List<Tile> _tiles = new List<Tile>();

        public int Width { get; }
        public int Height { get; }

        /// <summary>셰이더에 넘기는 채널 수 (1 = gray, 3 = color).</summary>
        public int ShaderChannels { get; }

        /// <summary>표시 범위 매핑: color = (tex - Offset) * Scale.</summary>
        public float Scale { get; private set; } = 1f;
        public float Offset { get; private set; }

        /// <summary>자동 대비(min..max 스트레치)용 매핑.</summary>
        public float AutoScale { get; private set; } = 1f;
        public float AutoOffset { get; private set; }

        public int TileCount => _tiles.Count;

        private readonly PixelType _pixelType;
        private readonly int _srcChannels;
        private readonly int _outChannels;

        private ImageTextureSet(int width, int height, int shaderChannels, PixelType pixelType, int srcChannels, int outChannels)
        {
            Width = width;
            Height = height;
            ShaderChannels = shaderChannels;
            _pixelType = pixelType;
            _srcChannels = srcChannels;
            _outChannels = outChannels;
        }

        /// <summary>이미지를 타일 텍스처로 업로드한다.</summary>
        public static ImageTextureSet Upload(Image image, int maxTileSize)
        {
            int srcCh = image.Channels;
            int outCh = srcCh == 3 || srcCh == 4 ? srcCh : 1;
            var set = new ImageTextureSet(image.Width, image.Height, outCh == 1 ? 1 : 3, image.PixelType, srcCh, outCh);

            set.ComputeRange(image, outCh, 1_000_000);

            int tile = Math.Max(256, maxTileSize);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);

            for (int y = 0; y < image.Height; y += tile)
            {
                int h = Math.Min(tile, image.Height - y);
                for (int x = 0; x < image.Width; x += tile)
                {
                    int w = Math.Min(tile, image.Width - x);
                    set._tiles.Add(set.CreateTile(image, x, y, w, h, outCh));
                }
            }
            return set;
        }

        private Tile CreateTile(Image image, int x, int y, int w, int h, int outCh)
        {
            var t = new Tile { X = x, Y = y, W = w, H = h };

            t.Texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, t.Texture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            UploadPixels(image, x, y, w, h, outCh, subImage: false);

            var err = GL.GetError();
            if (err != OpenTK.Graphics.OpenGL4.ErrorCode.NoError)
                throw new InvalidOperationException($"Texture upload failed ({err}) for tile {w}x{h} at ({x},{y}), {image.PixelType}x{image.Channels}");

            // 타일 사각형 (이미지 픽셀 좌표 + uv)
            float x0 = x, y0 = y, x1 = x + w, y1 = y + h;
            float[] verts =
            {
                x0, y0, 0, 0,  x1, y0, 1, 0,  x1, y1, 1, 1,
                x0, y0, 0, 0,  x1, y1, 1, 1,  x0, y1, 0, 1,
            };
            t.Vao = GL.GenVertexArray();
            t.Vbo = GL.GenBuffer();
            GL.BindVertexArray(t.Vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, t.Vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
            GL.EnableVertexAttribArray(1);
            GL.BindVertexArray(0);
            return t;
        }

        /// <summary>같은 크기/형식의 이미지인지 (라이브: 텍스처를 다시 만들지 않고 내용만 바꾼다).</summary>
        public bool CanUpdate(Image image)
            => image.Width == Width && image.Height == Height && image.PixelType == _pixelType && image.Channels == _srcChannels;

        /// <summary>기존 타일 텍스처에 새 픽셀을 올린다 (<see cref="CanUpdate"/> 가 true 일 때만).</summary>
        public void Update(Image image)
        {
            ComputeRange(image, _outChannels, 250_000);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            foreach (var t in _tiles)
            {
                GL.BindTexture(TextureTarget.Texture2D, t.Texture);
                UploadPixels(image, t.X, t.Y, t.W, t.H, _outChannels, subImage: true);
            }
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        /// <summary>
        /// 현재 바인딩된 텍스처에 타일 픽셀을 올리고 밉맵을 만든다. 채널 수가 같고 행 간격이 픽셀 크기의 배수면
        /// 이미지 메모리에서 바로 올리고(UnpackRowLength), 아니면 촘촘히 복사한 버퍼로 올린다.
        /// </summary>
        private static void UploadPixels(Image image, int x, int y, int w, int h, int outCh, bool subImage)
        {
            var format = outCh == 1 ? GlPixelFormat.Red : outCh == 3 ? GlPixelFormat.Bgr : GlPixelFormat.Bgra;
            GlPixelType glType;
            PixelInternalFormat internalFormat;
            switch (image.PixelType)
            {
                case PixelType.UInt8:
                    glType = GlPixelType.UnsignedByte;
                    internalFormat = outCh == 1 ? PixelInternalFormat.R8 : outCh == 3 ? PixelInternalFormat.Rgb8 : PixelInternalFormat.Rgba8;
                    break;
                case PixelType.UInt16:
                    glType = GlPixelType.UnsignedShort;
                    internalFormat = outCh == 1 ? PixelInternalFormat.R16 : outCh == 3 ? PixelInternalFormat.Rgb16 : PixelInternalFormat.Rgba16;
                    break;
                default:
                    glType = GlPixelType.Float;
                    internalFormat = outCh == 1 ? PixelInternalFormat.R32f : outCh == 3 ? PixelInternalFormat.Rgb32f : PixelInternalFormat.Rgba32f;
                    break;
            }

            bool direct = image.Channels == outCh
                && (image.PixelType == PixelType.UInt8 || image.PixelType == PixelType.UInt16 || image.PixelType == PixelType.Float32)
                && image.Stride % image.BytesPerPixel == 0;
            if (direct)
            {
                IntPtr src = image.GetRowPointer(y) + x * image.BytesPerPixel;
                GL.PixelStore(PixelStoreParameter.UnpackRowLength, (int)(image.Stride / image.BytesPerPixel));
                try
                {
                    if (subImage) GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w, h, format, glType, src);
                    else GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, w, h, 0, format, glType, src);
                }
                finally
                {
                    GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
                }
            }
            else
            {
                switch (image.PixelType)
                {
                    case PixelType.UInt8:
                    {
                        var buf = Pack<byte>(image, x, y, w, h, outCh);
                        if (subImage) GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w, h, format, glType, buf);
                        else GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, w, h, 0, format, glType, buf);
                        break;
                    }
                    case PixelType.UInt16:
                    {
                        var buf = Pack<ushort>(image, x, y, w, h, outCh);
                        if (subImage) GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w, h, format, glType, buf);
                        else GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, w, h, 0, format, glType, buf);
                        break;
                    }
                    default:
                    {
                        var buf = PackFloat(image, x, y, w, h, outCh);
                        if (subImage) GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w, h, format, glType, buf);
                        else GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, w, h, 0, format, glType, buf);
                        break;
                    }
                }
            }

            try
            {
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            }
            catch (Exception)
            {
                // 드라이버가 지원하지 않으면 밉맵 없이 표시
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            }
        }

        /// <summary>같은 원소 타입으로 타일을 촘촘히 패킹한다 (채널 수가 다르면 첫 채널만).</summary>
        private static T[] Pack<T>(Image image, int x, int y, int w, int h, int outCh) where T : unmanaged
        {
            int srcCh = image.Channels;
            int elem = sizeof(T);
            var buf = new T[(long)w * h * outCh];
            byte* basePtr = (byte*)image.GetHostPointer(forWrite: false);

            fixed (T* dst = buf)
            {
                for (int r = 0; r < h; r++)
                {
                    T* srcRow = (T*)(basePtr + image.Stride * (y + r)) + (long)x * srcCh;
                    T* dstRow = dst + (long)r * w * outCh;
                    if (srcCh == outCh)
                    {
                        System.Buffer.MemoryCopy(srcRow, dstRow, (long)w * outCh * elem, (long)w * outCh * elem);
                    }
                    else
                    {
                        for (int c = 0; c < w; c++)
                            dstRow[c] = srcRow[(long)c * srcCh];
                    }
                }
            }
            return buf;
        }

        /// <summary>정수/실수 타입을 float 로 변환해 패킹한다.</summary>
        private static float[] PackFloat(Image image, int x, int y, int w, int h, int outCh)
        {
            int srcCh = image.Channels;
            var buf = new float[(long)w * h * outCh];
            byte* basePtr = (byte*)image.GetHostPointer(forWrite: false);
            int take = Math.Min(outCh, srcCh);

            for (int r = 0; r < h; r++)
            {
                byte* row = basePtr + image.Stride * (y + r) + (long)x * image.BytesPerPixel;
                long o = (long)r * w * outCh;
                for (int c = 0; c < w; c++)
                {
                    for (int k = 0; k < take; k++)
                    {
                        long si = (long)c * srcCh + k;
                        float v;
                        switch (image.PixelType)
                        {
                            case PixelType.Int16: v = ((short*)row)[si]; break;
                            case PixelType.Int32: v = ((int*)row)[si]; break;
                            case PixelType.Float32: v = ((float*)row)[si]; break;
                            case PixelType.Float64: v = (float)((double*)row)[si]; break;
                            default: v = ((byte*)row)[si]; break;
                        }
                        buf[o + c * outCh + k] = v;
                    }
                }
            }
            return buf;
        }

        /// <summary>표시 범위 계산: 기본은 8bit 0~255 / 그 외 샘플링한 min/max. 자동 대비는 항상 min/max.</summary>
        private void ComputeRange(Image image, int outCh, int maxSamples)
        {
            int step = Math.Max(1, (int)Math.Sqrt((double)image.Width * image.Height / maxSamples));
            double min = double.MaxValue, max = double.MinValue;
            int take = Math.Min(outCh, image.Channels);
            for (int y = 0; y < image.Height; y += step)
            {
                for (int x = 0; x < image.Width; x += step)
                {
                    for (int k = 0; k < take; k++)
                    {
                        double v = image.GetPixel(x, y, k);
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                }
            }
            if (max <= min)
            {
                max = min + 1;
            }

            if (image.PixelType == PixelType.UInt8)
            {
                // R8 텍스처는 0..1 로 정규화되어 샘플링된다
                Scale = 1f;
                Offset = 0f;
                AutoOffset = (float)(min / 255.0);
                AutoScale = (float)(255.0 / (max - min));
            }
            else if (image.PixelType == PixelType.UInt16)
            {
                // R16 텍스처는 0..1 로 정규화되어 샘플링된다
                Offset = (float)(min / 65535.0);
                Scale = (float)(65535.0 / (max - min));
                AutoOffset = Offset;
                AutoScale = Scale;
            }
            else
            {
                Offset = (float)min;
                Scale = (float)(1.0 / (max - min));
                AutoOffset = Offset;
                AutoScale = Scale;
            }
        }

        public void Draw()
        {
            foreach (var t in _tiles)
            {
                GL.BindTexture(TextureTarget.Texture2D, t.Texture);
                GL.BindVertexArray(t.Vao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
            }
            GL.BindVertexArray(0);
        }

        public void Dispose()
        {
            foreach (var t in _tiles)
            {
                GL.DeleteBuffer(t.Vbo);
                GL.DeleteVertexArray(t.Vao);
                GL.DeleteTexture(t.Texture);
            }
            _tiles.Clear();
        }
    }
}
