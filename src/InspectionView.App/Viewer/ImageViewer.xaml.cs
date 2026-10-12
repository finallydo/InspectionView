using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using InspectionView.Core.Types;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Wpf;
using Image = InspectionView.Core.Types.Image;

namespace InspectionView.App.Viewer
{
    /// <summary>
    /// OpenGL 이미지 뷰어 (OpenTK GLWpfControl, D3DImage 기반이라 Airspace 문제 없음).
    /// <list type="bullet">
    /// <item>이미지: 타일 텍스처 + 밉맵 피라미드, 8/16bit/Float, Gray/Color</item>
    /// <item>오버레이: Region(반투명 채움), XLD(폴리라인), ROI 사각형</item>
    /// <item>조작: 휠 줌(커서 기준), 좌/중 드래그 팬, 더블클릭 맞춤, ROI 모드에서 좌 드래그로 사각형</item>
    /// <item>상태줄: 커서의 이미지 좌표와 픽셀값</item>
    /// </list>
    /// 표시 중인 Image 는 뷰어가 AddRef 해 소유한다 (변수가 바뀌어도 픽셀값 조회가 안전하도록).
    /// </summary>
    public partial class ImageViewer : UserControl
    {
        private GlShader? _imageShader;
        private GlShader? _colorShader;
        private int _uImageProj, _uTex, _uChannels, _uScale, _uOffset;
        private int _uColorProj, _uColor;
        private int _maxTextureSize = 4096;
        private bool _glFailed;

        private Image? _image;              // 표시 중인 이미지 (AddRef 됨)
        private Image? _pendingImage;       // 다음 렌더에서 업로드할 이미지 (AddRef 됨)
        private bool _pendingClear;
        private ImageTextureSet? _texture;

        private readonly List<OverlayLayer> _overlays = new List<OverlayLayer>();
        private readonly List<OverlayLayer> _garbage = new List<OverlayLayer>();
        private OverlayLayer? _roiLayer;
        private OverlayLayer? _profileLayer;
        private int _colorIndex;

        // 뷰 변환: 화면(논리 px) = (이미지 좌표 - View) * Zoom
        private double _zoom = 1;
        private double _viewX, _viewY;
        private bool _fitOnNextImage = true;

        private Point _dragStart;
        private double _dragViewX, _dragViewY;
        private bool _panning;
        private bool _roiDragging;
        private bool _profileDragging;
        private PointD _roiAnchor;

        /// <summary>그려진 ROI (이미지 좌표, 배타 경계). 없으면 null.</summary>
        public RectI? Roi { get; private set; }

        public event Action<RectI>? RoiChanged;

        /// <summary>'Insert ROI' 버튼: 스크립트에 넣어 달라는 요청.</summary>
        public event Action<RectI>? InsertRoiRequested;

        /// <summary>프로파일 선분 (이미지 좌표). 없으면 null.</summary>
        public (PointD A, PointD B)? ProfileLine { get; private set; }

        public event Action? ProfileLineChanged;

        /// <summary>표시 이미지가 바뀜 (SetImage 직후, 업로드 전).</summary>
        public event Action? ImageChanged;

        /// <summary>사용자가 이 창을 클릭함 (활성 그래픽 창 선택용).</summary>
        public event Action? Activated;

        /// <summary>표시 중(또는 업로드 대기 중)인 이미지. 뷰어가 AddRef 로 소유하므로 읽기에 안전하다.</summary>
        public Image? CurrentImage => _pendingImage ?? _image;

        public double Zoom => _zoom;
        public bool HasImage => _image != null;

        public ImageViewer()
        {
            InitializeComponent();

            Gl.Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3, RenderContinuously = false });
            Gl.Render += OnRender;
            Gl.SizeChanged += (s, e) => Invalidate();

            Surface.MouseWheel += OnMouseWheel;
            Surface.MouseDown += OnMouseDown;
            Surface.MouseMove += OnMouseMove;
            Surface.MouseUp += OnMouseUp;
            Surface.MouseLeave += (s, e) => InfoText.Text = "";
        }

        // ------------------------------------------------------------------
        // 공개 API
        // ------------------------------------------------------------------

        /// <summary>이미지를 표시한다 (null 이면 지움). 오버레이는 유지된다.</summary>
        public void SetImage(Image? image, bool fit = false)
        {
            if (image != null && image.IsDisposed)
                image = null;

            if (_pendingImage != null)
            {
                _pendingImage.Release();
                _pendingImage = null;
            }

            if (image == null)
            {
                _pendingClear = true;
            }
            else
            {
                image.AddRef();
                _pendingImage = image;
                _pendingClear = false;
                if (fit || _image == null || _image.Width != image.Width || _image.Height != image.Height)
                    _fitOnNextImage = true;
            }
            Invalidate();
            ImageChanged?.Invoke();
        }

        /// <summary>Region / XLD / 객체 배열을 오버레이로 추가한다.</summary>
        public void AddOverlay(object value, string name = "")
        {
            var layers = OverlayLayer.FromValue(value, _colorIndex, name);
            if (layers.Count == 0)
                return;
            _colorIndex += layers.Count;
            _overlays.AddRange(layers);
            Invalidate();
        }

        public void ClearOverlays()
        {
            _garbage.AddRange(_overlays);
            _overlays.Clear();
            _colorIndex = 0;
            Invalidate();
        }

        public void ClearRoi()
        {
            if (_roiLayer != null)
            {
                _garbage.Add(_roiLayer);
                _roiLayer = null;
            }
            Roi = null;
            Invalidate();
        }

        public void ClearProfileLine()
        {
            if (_profileLayer != null)
            {
                _garbage.Add(_profileLayer);
                _profileLayer = null;
            }
            ProfileLine = null;
            ProfileLineChanged?.Invoke();
            Invalidate();
        }

        public void SetProfileLine(PointD a, PointD b)
        {
            if (_profileLayer != null)
                _garbage.Add(_profileLayer);
            ProfileLine = (a, b);
            _profileLayer = OverlayLayer.FromLine(a, b, OverlayColor.Profile);
            ProfileLineChanged?.Invoke();
            Invalidate();
        }

        public void SetRoi(RectI rect)
        {
            if (_roiLayer != null)
                _garbage.Add(_roiLayer);
            Roi = rect;
            _roiLayer = OverlayLayer.FromRect(rect, OverlayColor.Roi);
            RoiChanged?.Invoke(rect);
            Invalidate();
        }

        public void FitToWindow()
        {
            if (_image == null && _pendingImage == null)
                return;
            var img = _pendingImage ?? _image!;
            double w = Math.Max(1, Surface.ActualWidth), h = Math.Max(1, Surface.ActualHeight);
            _zoom = Math.Min(w / img.Width, h / img.Height);
            if (_zoom <= 0 || double.IsInfinity(_zoom)) _zoom = 1;
            _viewX = (img.Width - w / _zoom) / 2;
            _viewY = (img.Height - h / _zoom) / 2;
            UpdateZoomText();
            Invalidate();
        }

        public void ActualSize()
        {
            var img = _pendingImage ?? _image;
            double w = Math.Max(1, Surface.ActualWidth), h = Math.Max(1, Surface.ActualHeight);
            _zoom = 1;
            if (img != null)
            {
                _viewX = (img.Width - w) / 2;
                _viewY = (img.Height - h) / 2;
            }
            UpdateZoomText();
            Invalidate();
        }

        public void Invalidate() => Gl.InvalidateVisual();

        // ------------------------------------------------------------------
        // 렌더링 (GL 컨텍스트 안)
        // ------------------------------------------------------------------

        private void OnRender(TimeSpan delta)
        {
            if (_glFailed)
                return;

            try
            {
                EnsureShaders();
                ProcessPending();

                double dpi = DpiScale();
                int vw = Math.Max(1, (int)Math.Round(Surface.ActualWidth * dpi));
                int vh = Math.Max(1, (int)Math.Round(Surface.ActualHeight * dpi));
                GL.Viewport(0, 0, vw, vh);
                GL.ClearColor(0.19f, 0.19f, 0.19f, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                GL.Disable(EnableCap.DepthTest);

                var proj = Projection(Surface.ActualWidth, Surface.ActualHeight);

                if (_texture != null)
                {
                    _imageShader!.Use();
                    GL.UniformMatrix4(_uImageProj, 1, false, proj);
                    GL.ActiveTexture(TextureUnit.Texture0);
                    GL.Uniform1(_uTex, 0);
                    GL.Uniform1(_uChannels, _texture.ShaderChannels);
                    GL.Uniform1(_uScale, _texture.Scale);
                    GL.Uniform1(_uOffset, _texture.Offset);
                    _texture.Draw();
                }

                if (_overlays.Count > 0 || _roiLayer != null || _profileLayer != null)
                {
                    _colorShader!.Use();
                    GL.UniformMatrix4(_uColorProj, 1, false, proj);
                    GL.Enable(EnableCap.Blend);
                    GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    foreach (var layer in _overlays)
                        layer.Draw(_uColor);
                    _roiLayer?.Draw(_uColor);
                    _profileLayer?.Draw(_uColor);
                    GL.Disable(EnableCap.Blend);
                }
            }
            catch (Exception ex)
            {
                _glFailed = true;
                InfoText.Text = "OpenGL error: " + ex.Message;
            }
        }

        private void EnsureShaders()
        {
            if (_imageShader != null)
                return;

            _imageShader = new GlShader(ShaderSources.ImageVertex, ShaderSources.ImageFragment);
            _uImageProj = _imageShader.Uniform("uProj");
            _uTex = _imageShader.Uniform("uTex");
            _uChannels = _imageShader.Uniform("uChannels");
            _uScale = _imageShader.Uniform("uScale");
            _uOffset = _imageShader.Uniform("uOffset");

            _colorShader = new GlShader(ShaderSources.ColorVertex, ShaderSources.ColorFragment);
            _uColorProj = _colorShader.Uniform("uProj");
            _uColor = _colorShader.Uniform("uColor");

            GL.GetInteger(GetPName.MaxTextureSize, out _maxTextureSize);
            _maxTextureSize = Math.Min(Math.Max(1024, _maxTextureSize), 4096);
        }

        private void ProcessPending()
        {
            foreach (var g in _garbage)
                g.Dispose();
            _garbage.Clear();

            if (_pendingClear)
            {
                _pendingClear = false;
                _texture?.Dispose();
                _texture = null;
                _image?.Release();
                _image = null;
            }

            if (_pendingImage != null)
            {
                var img = _pendingImage;
                _pendingImage = null;

                _texture?.Dispose();
                _texture = null;
                _image?.Release();
                _image = img;

                if (!img.IsDisposed)
                {
                    _texture = ImageTextureSet.Upload(img, _maxTextureSize);
                    if (_fitOnNextImage)
                    {
                        _fitOnNextImage = false;
                        FitToWindow();
                    }
                }
            }
        }

        /// <summary>이미지 픽셀 좌표 → NDC 직교 투영 (column-major).</summary>
        private float[] Projection(double width, double height)
        {
            double l = _viewX, r = _viewX + Math.Max(1, width) / _zoom;
            double t = _viewY, b = _viewY + Math.Max(1, height) / _zoom;
            var m = new float[16];
            m[0] = (float)(2.0 / (r - l));
            m[5] = (float)(2.0 / (t - b));
            m[10] = -1f;
            m[12] = (float)(-(r + l) / (r - l));
            m[13] = (float)(-(t + b) / (t - b));
            m[15] = 1f;
            return m;
        }

        private double DpiScale()
        {
            var src = PresentationSource.FromVisual(this);
            return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        }

        // ------------------------------------------------------------------
        // 마우스
        // ------------------------------------------------------------------

        private PointD ScreenToImage(Point p) => new PointD(_viewX + p.X / _zoom, _viewY + p.Y / _zoom);

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var p = e.GetPosition(Surface);
            var before = ScreenToImage(p);
            double factor = e.Delta > 0 ? 1.25 : 1 / 1.25;
            _zoom = Math.Max(0.01, Math.Min(256, _zoom * factor));
            // 커서 아래 이미지 좌표가 고정되도록 보정
            _viewX = before.X - p.X / _zoom;
            _viewY = before.Y - p.Y / _zoom;
            UpdateZoomText();
            Invalidate();
            e.Handled = true;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
            Activated?.Invoke();
            var p = e.GetPosition(Surface);

            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
            {
                FitToWindow();
                return;
            }

            if (e.ChangedButton == MouseButton.Left && RoiButton.IsChecked == true && _image != null)
            {
                _roiDragging = true;
                _roiAnchor = ScreenToImage(p);
                Surface.CaptureMouse();
                return;
            }

            if (e.ChangedButton == MouseButton.Left && ProfileButton.IsChecked == true && _image != null)
            {
                _profileDragging = true;
                _roiAnchor = ScreenToImage(p);
                SetProfileLine(_roiAnchor, _roiAnchor);
                Surface.CaptureMouse();
                return;
            }

            if (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Middle)
            {
                _panning = true;
                _dragStart = p;
                _dragViewX = _viewX;
                _dragViewY = _viewY;
                Surface.CaptureMouse();
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(Surface);

            if (_panning)
            {
                _viewX = _dragViewX - (p.X - _dragStart.X) / _zoom;
                _viewY = _dragViewY - (p.Y - _dragStart.Y) / _zoom;
                Invalidate();
            }
            else if (_roiDragging)
            {
                SetRoi(MakeRect(_roiAnchor, ScreenToImage(p)));
            }
            else if (_profileDragging)
            {
                SetProfileLine(_roiAnchor, ScreenToImage(p));
            }

            UpdateInfo(p);
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_panning || _roiDragging || _profileDragging)
            {
                _panning = false;
                _roiDragging = false;
                _profileDragging = false;
                Surface.ReleaseMouseCapture();
            }
        }

        private RectI MakeRect(PointD a, PointD b)
        {
            int x0 = (int)Math.Floor(Math.Min(a.X, b.X)), y0 = (int)Math.Floor(Math.Min(a.Y, b.Y));
            int x1 = (int)Math.Ceiling(Math.Max(a.X, b.X)), y1 = (int)Math.Ceiling(Math.Max(a.Y, b.Y));
            if (_image != null)
            {
                x0 = Math.Max(0, Math.Min(x0, _image.Width - 1));
                y0 = Math.Max(0, Math.Min(y0, _image.Height - 1));
                x1 = Math.Max(x0 + 1, Math.Min(x1, _image.Width));
                y1 = Math.Max(y0 + 1, Math.Min(y1, _image.Height));
            }
            return new RectI(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
        }

        private void UpdateInfo(Point p)
        {
            var img = _image;
            var ip = ScreenToImage(p);
            int x = (int)Math.Floor(ip.X), y = (int)Math.Floor(ip.Y);

            if (img == null || img.IsDisposed)
            {
                InfoText.Text = $"x: {x}  y: {y}";
                return;
            }

            string value = "";
            if (x >= 0 && y >= 0 && x < img.Width && y < img.Height)
            {
                try
                {
                    if (img.Channels == 1)
                    {
                        value = FormatValue(img.GetPixel(x, y));
                    }
                    else
                    {
                        var parts = new string[Math.Min(img.Channels, 4)];
                        for (int c = 0; c < parts.Length; c++)
                            parts[c] = FormatValue(img.GetPixel(x, y, c));
                        value = "[" + string.Join(", ", parts) + "]";
                    }
                }
                catch (Exception)
                {
                    value = "?";
                }
            }

            var roi = Roi.HasValue ? $"   ROI: row {Roi.Value.Y}..{Roi.Value.Bottom - 1}, col {Roi.Value.X}..{Roi.Value.Right - 1}" : "";
            InfoText.Text = $"x: {x,5}  y: {y,5}   value: {value}{roi}";
        }

        private static string FormatValue(double v)
            => v == Math.Floor(v) ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("F3", CultureInfo.InvariantCulture);

        private void UpdateZoomText() => ZoomText.Text = $"{_zoom * 100:F0}%";

        // ------------------------------------------------------------------
        // 툴바
        // ------------------------------------------------------------------

        private void OnRoiChecked(object sender, RoutedEventArgs e) => ProfileButton.IsChecked = false;
        private void OnProfileChecked(object sender, RoutedEventArgs e) => RoiButton.IsChecked = false;

        private void OnFit(object sender, RoutedEventArgs e) => FitToWindow();
        private void OnActualSize(object sender, RoutedEventArgs e) => ActualSize();
        private void OnClearOverlays(object sender, RoutedEventArgs e) => ClearOverlays();
        private void OnClearRoi(object sender, RoutedEventArgs e) => ClearRoi();

        private void OnInsertRoi(object sender, RoutedEventArgs e)
        {
            if (Roi.HasValue)
                InsertRoiRequested?.Invoke(Roi.Value);
        }
    }
}
