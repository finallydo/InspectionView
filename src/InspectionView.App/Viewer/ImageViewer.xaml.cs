using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using InspectionView.Core.Types;
using OpenTK.Graphics.OpenGL4;
using ErrorCode = OpenTK.Graphics.OpenGL4.ErrorCode;
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
        private GLWpfControl? _gl;
        private int _contextGeneration;
        private long _frameCount;
        private GlShader? _imageShader;
        private GlShader? _colorShader;
        private int _uImageProj, _uTex, _uChannels, _uScale, _uOffset;
        private int _uColorProj, _uColor;
        private int _maxTextureSize = 4096;
        private bool _glFailed;
        private bool _autoContrast;
        private OverlayLayer? _boundsLayer;
        private string _glInfo = "";

        /// <summary>OpenGL 단계에서 예외가 났을 때 (메시지). 출력 창 기록용.</summary>
        public event Action<string>? RenderError;

        /// <summary>진단 로그 (GL 컨텍스트 생성 등).</summary>
        public event Action<string>? Log;

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

            // AvalonDock 은 레이아웃을 만들 때 내용을 떼었다 붙이므로(Unloaded → Loaded) GLWpfControl 의 렌더러가
            // 죽을 수 있다. Loaded 마다 GL 컨트롤을 새로 만들고 GL 자원은 새 컨텍스트에 다시 올린다.
            Loaded += (s, e) => CreateGlControl();
            Unloaded += (s, e) => DestroyGlControl();

            Surface.MouseWheel += OnMouseWheel;
            Surface.MouseDown += OnMouseDown;
            Surface.MouseMove += OnMouseMove;
            Surface.MouseUp += OnMouseUp;
            Surface.MouseLeave += (s, e) => InfoText.Text = "";
        }

        // ------------------------------------------------------------------
        // GL 컨트롤 수명
        // ------------------------------------------------------------------

        private void CreateGlControl()
        {
            if (_gl != null)
                return;

            // 이전 컨텍스트의 자원은 모두 무효 (GL 호출 없이 참조만 버린다)
            _imageShader = null;
            _colorShader = null;
            _texture = null;
            _garbage.Clear();
            foreach (var layer in _overlays) layer.ResetGl();
            _roiLayer?.ResetGl();
            _profileLayer?.ResetGl();
            _boundsLayer?.ResetGl();
            _glFailed = false;
            _contextGeneration++;

            // 표시 중이던 이미지는 다시 업로드
            if (_image != null && _pendingImage == null && !_image.IsDisposed)
            {
                _image.AddRef();
                _pendingImage = _image;
            }

            var gl = new GLWpfControl();
            Surface.Children.Insert(0, gl);
            try
            {
                // RenderContinuously = false 로 두면 InvalidateVisual() 로 다시 그려지지 않는 경우가 있어 매 프레임 그린다.
                gl.Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3, RenderContinuously = true });
            }
            catch (Exception ex)
            {
                Surface.Children.Remove(gl);
                ShowError("OpenGL start failed: " + ex.Message);
                return;
            }
            gl.Render += OnRender;
            _gl = gl;
            Log?.Invoke($"GL control created (context #{_contextGeneration})");
        }

        private void DestroyGlControl()
        {
            var gl = _gl;
            if (gl == null)
                return;
            gl.Render -= OnRender;
            Surface.Children.Remove(gl);
            _gl = null;
            Log?.Invoke($"GL control released (context #{_contextGeneration}, {_frameCount} frames)");
        }

        private void ShowError(string message)
        {
            _glFailed = true;
            ErrorText.Text = message;
            ErrorPanel.Visibility = Visibility.Visible;
            RenderError?.Invoke(message);
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
            _glFailed = false;                      // 새 이미지로 다시 시도
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

        public void Invalidate() => _gl?.InvalidateVisual();

        // ------------------------------------------------------------------
        // 렌더링 (GL 컨텍스트 안)
        // ------------------------------------------------------------------

        private void OnRender(TimeSpan delta)
        {
            if (_glFailed)
                return;

            string phase = "init";
            try
            {
                bool first = _imageShader == null;
                EnsureShaders();
                if (first)
                    Log?.Invoke($"GL context #{_contextGeneration} ready: {_glInfo}");
                _frameCount++;
                phase = "upload";
                ProcessPending();

                phase = "draw";
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
                    GL.Uniform1(_uScale, _autoContrast ? _texture.AutoScale : _texture.Scale);
                    GL.Uniform1(_uOffset, _autoContrast ? _texture.AutoOffset : _texture.Offset);
                    _texture.Draw();
                }

                if (_boundsLayer != null || _overlays.Count > 0 || _roiLayer != null || _profileLayer != null)
                {
                    _colorShader!.Use();
                    GL.UniformMatrix4(_uColorProj, 1, false, proj);
                    GL.Enable(EnableCap.Blend);
                    GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    _boundsLayer?.Draw(_uColor);
                    foreach (var layer in _overlays)
                        layer.Draw(_uColor);
                    _roiLayer?.Draw(_uColor);
                    _profileLayer?.Draw(_uColor);
                    GL.Disable(EnableCap.Blend);
                }

                var err = GL.GetError();
                if (err != ErrorCode.NoError)
                    throw new InvalidOperationException($"GL error {err} after {phase}");

                if (ErrorPanel.Visibility == Visibility.Visible)
                    ErrorPanel.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                ShowError($"OpenGL {phase} failed: {ex.GetType().Name}: {ex.Message}\n{_glInfo}");
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

            try
            {
                _glInfo = $"GL {GL.GetString(StringName.Version)} | {GL.GetString(StringName.Renderer)} | GLSL {GL.GetString(StringName.ShadingLanguageVersion)}";
            }
            catch (Exception)
            {
                _glInfo = "";
            }
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

                _boundsLayer?.Dispose();
                _boundsLayer = null;

                if (!img.IsDisposed)
                {
                    _texture = ImageTextureSet.Upload(img, _maxTextureSize);
                    _boundsLayer = OverlayLayer.FromRect(new RectI(0, 0, img.Width, img.Height), new OverlayColor(0.6f, 0.6f, 0.6f, 1f), "Bounds");
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
            try
            {
                return System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            }
            catch (Exception)
            {
                var src = PresentationSource.FromVisual(this);
                return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            }
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
            InfoText.Text = $"{img.Width}x{img.Height} {img.PixelType.ShortName()}{(img.Channels > 1 ? "x" + img.Channels : "")}   x: {x,5}  y: {y,5}   value: {value}{roi}";
        }

        private static string FormatValue(double v)
            => v == Math.Floor(v) ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("F3", CultureInfo.InvariantCulture);

        private void UpdateZoomText() => ZoomText.Text = $"{_zoom * 100:F0}%";

        // ------------------------------------------------------------------
        // 툴바
        // ------------------------------------------------------------------

        private void OnAutoContrastChanged(object sender, RoutedEventArgs e)
        {
            _autoContrast = AutoContrastButton.IsChecked == true;
            Invalidate();
        }

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
