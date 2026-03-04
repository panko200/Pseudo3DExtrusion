using BitmapToVector;
using BitmapToVector.SkiaSharp;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Imaging;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;
using static System.Windows.Forms.DataFormats;

namespace Pseudo3DExtrusion
{
    public enum TraceTarget { Alpha, Luminance, LuminanceInvert }
    [VideoEffect("疑似3D押し出し", ["加工"], ["Extrude","Extrusion"])]
    internal class ShapeExtrudeEffect : VideoEffectBase
    {
        public override string Label => "疑似3D押し出し"; 
        [Display(GroupName = "テクスチャ", Name = "テクスチャを使用する", Description = "チェックを入れると、前面と背面に元の画像（色や模様）を貼り付けます！側面はベースカラーで塗られます。")]
        [ToggleSlider]
        public bool UseTexture { get => _useTexture; set => Set(ref _useTexture, value); }
        private bool _useTexture = true; // ★デフォルトONにしました！
        [Display(GroupName = "解析設定", Name = "抽出モード")]
        [EnumComboBox]
        public TraceTarget TraceTarget { get => _traceTarget; set => Set(ref _traceTarget, value); }
        private TraceTarget _traceTarget = TraceTarget.Alpha; [Display(GroupName = "解析設定", Name = "閾値")]
        [AnimationSlider("F1", "", 1, 255)]
        public Animation AlphaThreshold { get; } = new Animation(10f, 1f, 255f); [Display(GroupName = "立体化", Name = "押し出し量 (厚み)")]
        [AnimationSlider("F1", "px", 0, 1000)]
        public Animation Depth { get; } = new Animation(50f, 0, 1000f); [Display(GroupName = "背面変形", Name = "Xズレ", Description = "背面を横方向にずらして、斜めの立体を作ります。")]
        [AnimationSlider("F1", "px", -200, 200)]
        public Animation BackOffsetX { get; } = new Animation(0f, -1000f, 1000f); [Display(GroupName = "背面変形", Name = "Yズレ", Description = "背面を縦方向にずらして、斜めの立体を作ります。")]
        [AnimationSlider("F1", "px", -200, 200)]
        public Animation BackOffsetY { get; } = new Animation(0f, -1000f, 1000f); [Display(GroupName = "背面変形", Name = "拡大率", Description = "背面の大きさを変更して、パース（遠近感）を強調します。")]
        [AnimationSlider("F1", "%", 0, 200)]
        public Animation BackScale { get; } = new Animation(100f, 0f, 500f); [Display(GroupName = "立体化", Name = "ベースカラー", Description = "立体の基本となる色です。(テクスチャOFFの時は全体の色になります)")]
        [ColorPicker]
        public System.Windows.Media.Color BaseColor { get => _baseColor; set => Set(ref _baseColor, value); }
        private System.Windows.Media.Color _baseColor = System.Windows.Media.Colors.White; [Display(GroupName = "描画設定", Name = "簡易ライティング", Description = "光を当てて立体感を強調します。")]
        [ToggleSlider]
        public bool EnableLighting { get => _enableLighting; set => Set(ref _enableLighting, value); }
        private bool _enableLighting = true; [Display(GroupName = "表示面", Name = "前面")]
        [ToggleSlider]
        public bool ShowFront { get => _showFront; set => Set(ref _showFront, value); }
        private bool _showFront = true; [Display(GroupName = "表示面", Name = "背面")]
        [ToggleSlider]
        public bool ShowBack { get => _showBack; set => Set(ref _showBack, value); }
        private bool _showBack = true; [Display(GroupName = "表示面", Name = "側面")]
        [ToggleSlider]
        public bool ShowSide { get => _showSide; set => Set(ref _showSide, value); }
        private bool _showSide = true;

        public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription) => [];
        public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices) => new ShapeExtrudeProcessor(devices, this);
        protected override IEnumerable<YukkuriMovieMaker.Commons.IAnimatable> GetAnimatables() => [AlphaThreshold, Depth, BackOffsetX, BackOffsetY, BackScale];
    }

    internal class ShapeExtrudeProcessor : IVideoEffectProcessor
    {
        private readonly IGraphicsDevicesAndContext _devices;
        private readonly ShapeExtrudeEffect _item;
        private ID2D1Image? _input;
        private ID2D1Bitmap1? _cpuReadBitmap;
        private ID2D1Bitmap? _outD2DBitmap;
        private AffineTransform2D? _mapTransformEffect;
        private ID2D1Image? _transformOutput;
        private bool _isEffectReady = false;

        public ShapeExtrudeProcessor(IGraphicsDevicesAndContext devices, ShapeExtrudeEffect item)
        {
            _devices = devices;
            _item = item;
            _mapTransformEffect = new AffineTransform2D(_devices.DeviceContext);
            _transformOutput = _mapTransformEffect.Output;
        }

        public ID2D1Image Output => (_isEffectReady && _transformOutput != null) ? _transformOutput : (_input ?? throw new NullReferenceException("Input is null"));

        public void SetInput(ID2D1Image? input) { _input = input; }

        public DrawDescription Update(EffectDescription desc)
        {
            _isEffectReady = false;
            if (_input == null) return desc.DrawDescription;
            var dc = _devices.DeviceContext;
            var frame = desc.ItemPosition.Frame;

            int threshold = (int)_item.AlphaThreshold.GetValue(frame, desc.ItemDuration.Frame, desc.FPS);
            float depth = (float)_item.Depth.GetValue(frame, desc.ItemDuration.Frame, desc.FPS);

            float backOffsetX = (float)_item.BackOffsetX.GetValue(frame, desc.ItemDuration.Frame, desc.FPS);
            float backOffsetY = (float)_item.BackOffsetY.GetValue(frame, desc.ItemDuration.Frame, desc.FPS);
            float backScale = (float)_item.BackScale.GetValue(frame, desc.ItemDuration.Frame, desc.FPS) / 100f;

            Vortice.RawRectF rawBounds;
            try { rawBounds = dc.GetImageLocalBounds(_input); } catch { return desc.DrawDescription; }
            int width = (int)Math.Ceiling(rawBounds.Right) - (int)Math.Floor(rawBounds.Left);
            int height = (int)Math.Ceiling(rawBounds.Bottom) - (int)Math.Floor(rawBounds.Top);

            if (width <= 0 || height <= 0) return desc.DrawDescription;

            if (_cpuReadBitmap == null || _cpuReadBitmap.PixelSize.Width != width || _cpuReadBitmap.PixelSize.Height != height)
            {
                _cpuReadBitmap?.Dispose();
                _cpuReadBitmap = dc.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0, new BitmapProperties1(new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
            }
            // ★ここから修正：Deviceもちゃんと using で受け取る！
            using (var d2dDevice = _devices.DeviceContext.Device)
            using (var localContext = d2dDevice.CreateDeviceContext(DeviceContextOptions.None))
            using (var gpuBitmap = localContext.CreateBitmap(new SizeI(width, height), new BitmapProperties1(new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target)))
            {
                localContext.Target = gpuBitmap;
                localContext.BeginDraw();
                localContext.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
                localContext.DrawImage(_input, new Vector2(-rawBounds.Left, -rawBounds.Top));
                localContext.EndDraw();
                _cpuReadBitmap.CopyFromBitmap(gpuBitmap);
            }

            var drawDesc = desc.DrawDescription;
            float d2r = (float)Math.PI / 180.0f;

            Matrix4x4 localRotation = Matrix4x4.CreateRotationZ(drawDesc.Rotation.Z * d2r) *
                                       Matrix4x4.CreateRotationY(-drawDesc.Rotation.Y * d2r) *
                                       Matrix4x4.CreateRotationX(-drawDesc.Rotation.X * d2r);

            Matrix4x4 worldTranslation = Matrix4x4.CreateTranslation(drawDesc.Draw.X, drawDesc.Draw.Y, drawDesc.Draw.Z);
            Matrix4x4 perspective = new Matrix4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, -0.001f, 0, 0, 0, 1);
            Matrix4x4 m_fullProjection = localRotation * worldTranslation * drawDesc.Camera * perspective;

            Vector4 projectedCenter = Vector4.Transform(Vector3.Zero, m_fullProjection);
            float tx = 0, ty = 0, tz = 0;
            if (Math.Abs(projectedCenter.W) > 1e-6f)
            {
                tx = projectedCenter.X / projectedCenter.W;
                ty = projectedCenter.Y / projectedCenter.W;
                tz = projectedCenter.W;
            }
            Matrix4x4 m_adjustment = Matrix4x4.CreateTranslation(-tx, -ty, 0);
            Matrix4x4 m_internalDraw = m_fullProjection * m_adjustment;

            if (!Matrix4x4.Invert(drawDesc.Camera, out Matrix4x4 invView)) invView = Matrix4x4.Identity;
            Vector3 worldEye = Vector3.Transform(new Vector3(0, 0, 1000), invView);

            var map = _cpuReadBitmap.Map(MapOptions.Read);
            List<RenderableFace> faces;
            try { faces = ExtractAndBuildPolygons(map, width, height, threshold, depth, rawBounds, backOffsetX, backOffsetY, backScale, m_internalDraw); }
            finally { _cpuReadBitmap.Unmap(); }

            var validFaces = new List<RenderableFace>();
            foreach (var face in faces)
            {
                Vector3 rotatedNormal = Vector3.TransformNormal(face.Normal, localRotation);
                Vector3 worldFaceCenter = Vector3.Transform(face.Center, localRotation * worldTranslation);

                Vector3 viewDir = worldEye - worldFaceCenter;

                if (Vector3.Dot(rotatedNormal, viewDir) > 0)
                {
                    face.DistanceSq = viewDir.LengthSquared();
                    face.CalculateColorAndBrightness(_item.BaseColor, rotatedNormal, _item.EnableLighting);
                    validFaces.Add(face);
                }
            }

            if (validFaces.Count == 0) return desc.DrawDescription;

            validFaces.Sort((a, b) => b.DistanceSq.CompareTo(a.DistanceSq));

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            foreach (var face in validFaces) face.CalcBounds(m_internalDraw, ref minX, ref minY, ref maxX, ref maxY);

            if (minX > maxX || minY > maxY || float.IsInfinity(minX)) return desc.DrawDescription;

            float limit = 4096f;
            minX = Math.Max(minX, -limit);
            maxX = Math.Min(maxX, limit);
            minY = Math.Max(minY, -limit);
            maxY = Math.Min(maxY, limit);

            if (minX >= maxX || minY >= maxY) return desc.DrawDescription;

            int pad = 10;
            int outW = (int)Math.Ceiling(maxX - minX) + pad * 2;
            int outH = (int)Math.Ceiling(maxY - minY) + pad * 2;
            float drawOffsetX = -minX + pad;
            float drawOffsetY = -minY + pad;

            if (outW <= 0 || outH <= 0 || outW > 16384 || outH > 16384) return desc.DrawDescription;

            using var outBitmap = new SKBitmap(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(outBitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(drawOffsetX, drawOffsetY);

            using var paint = new SKPaint { IsAntialias = true };
            foreach (var face in validFaces) face.Draw(canvas, m_internalDraw, paint);

            // ★テクスチャとして保持したリソースを解放
            foreach (var face in validFaces) face.Dispose();

            _outD2DBitmap?.Dispose();
            _outD2DBitmap = dc.CreateBitmap(new SizeI(outW, outH), outBitmap.GetPixels(), outBitmap.RowBytes, new Vortice.Direct2D1.BitmapProperties(new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));

            if (_mapTransformEffect != null && _outD2DBitmap != null)
            {
                _mapTransformEffect.SetInput(0, _outD2DBitmap, true);
                _mapTransformEffect.TransformMatrix = Matrix3x2.CreateTranslation(-drawOffsetX, -drawOffsetY);
                _isEffectReady = true;
            }

            return drawDesc with
            {
                Draw = drawDesc.Draw with { X = tx, Y = ty, Z = -tz },
                Rotation = Vector3.Zero,
                Camera = Matrix4x4.Identity
            };
        }

        private unsafe List<RenderableFace> ExtractAndBuildPolygons(MappedRectangle map, int width, int height, int threshold, float depth, Vortice.RawRectF rawBounds, float backOffsetX, float backOffsetY, float backScale, Matrix4x4 projectionMatrix)
        {
            using var bwBitmap = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);

            // ★テクスチャ用のフルカラー画像を作成
            SKBitmap? textureBitmap = null;
            if (_item.UseTexture) textureBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

            byte* srcPtr = (byte*)map.Bits;
            byte* dstPtr = (byte*)bwBitmap.GetPixels();
            byte* texPtr = textureBitmap != null ? (byte*)textureBitmap.GetPixels() : null;

            for (int y = 0; y < height; y++)
            {
                byte* srcRow = srcPtr + y * map.Pitch;
                byte* dstRow = dstPtr + y * bwBitmap.RowBytes;
                byte* texRow = texPtr != null ? texPtr + y * textureBitmap!.RowBytes : null;

                for (int x = 0; x < width; x++)
                {
                    byte b = srcRow[x * 4 + 0], g = srcRow[x * 4 + 1], r = srcRow[x * 4 + 2], a = srcRow[x * 4 + 3];
                    bool isShape = false;

                    if (_item.TraceTarget == TraceTarget.Alpha) isShape = a > threshold;
                    else if (a > 0)
                    {
                        byte luma = (byte)(0.299f * ((r * 255) / a) + 0.587f * ((g * 255) / a) + 0.114f * ((b * 255) / a));
                        isShape = _item.TraceTarget == TraceTarget.Luminance ? (luma > threshold) : (luma <= threshold);
                    }
                    dstRow[x] = isShape ? (byte)0 : (byte)255;

                    if (texRow != null)
                    {
                        texRow[x * 4 + 0] = b; texRow[x * 4 + 1] = g;
                        texRow[x * 4 + 2] = r; texRow[x * 4 + 3] = a;
                    }
                }
            }

            var paths = PotraceSkiaSharp.Trace(new PotraceParam { AlphaMax = 0.0, OptiCurve = false, OptTolerance = 0.0 }, bwBitmap);
            var faces = new List<RenderableFace>();

            float rawLeft = rawBounds.Left;
            float rawTop = rawBounds.Top;
            float imgCenterX = rawLeft + width / 2.0f;
            float imgCenterY = rawTop + height / 2.0f;

            foreach (var path in paths)
            {
                var subFrontPaths = new List<List<Vector3>>();
                var subBackPaths = new List<List<Vector3>>();

                float sumX = 0, sumY = 0;
                int totalPoints = 0;

                using var iterator = path.CreateIterator(false); SKPathVerb verb; SKPoint[] pts = new SKPoint[4];
                var subPts = new List<SKPoint>();

                void ProcessSubPath()
                {
                    if (subPts.Count < 3) return;
                    if (SKPoint.Distance(subPts[0], subPts[^1]) < 0.5f) subPts.RemoveAt(subPts.Count - 1);

                    var front = new List<Vector3>();
                    var back = new List<Vector3>();

                    foreach (var p in subPts)
                    {
                        float cx = p.X + rawLeft;
                        float cy = p.Y + rawTop;

                        float bx = (cx - imgCenterX) * backScale + imgCenterX + backOffsetX;
                        float by = (cy - imgCenterY) * backScale + imgCenterY + backOffsetY;

                        front.Add(new Vector3(cx, cy, depth / 2.0f));
                        back.Add(new Vector3(bx, by, -depth / 2.0f));

                        sumX += cx; sumY += cy; totalPoints++;
                    }
                    subFrontPaths.Add(front);
                    subBackPaths.Add(back);

                    if (_item.ShowSide)
                    {
                        for (int i = 0; i < subPts.Count; i++)
                        {
                            var pt1 = subPts[i]; var pt2 = subPts[(i + 1) % subPts.Count];

                            Vector3 p1f = new Vector3(pt1.X + rawLeft, pt1.Y + rawTop, depth / 2.0f);
                            Vector3 p2f = new Vector3(pt2.X + rawLeft, pt2.Y + rawTop, depth / 2.0f);

                            float bx1 = (p1f.X - imgCenterX) * backScale + imgCenterX + backOffsetX;
                            float by1 = (p1f.Y - imgCenterY) * backScale + imgCenterY + backOffsetY;
                            Vector3 p1b = new Vector3(bx1, by1, -depth / 2.0f);

                            float bx2 = (p2f.X - imgCenterX) * backScale + imgCenterX + backOffsetX;
                            float by2 = (p2f.Y - imgCenterY) * backScale + imgCenterY + backOffsetY;
                            Vector3 p2b = new Vector3(bx2, by2, -depth / 2.0f);

                            if (p1f == p2f) continue;

                            Vector3 edge1 = p2f - p1f;
                            Vector3 edge2 = p1b - p1f;
                            Vector3 normal = Vector3.Cross(edge2, edge1);

                            if (normal.LengthSquared() < 1e-6f)
                            {
                                float dx = p2f.X - p1f.X; float dy = p2f.Y - p1f.Y;
                                normal = new Vector3(dy, -dx, 0);
                            }
                            normal = Vector3.Normalize(normal);

                            faces.Add(new PolygonFace { Vertices = new List<Vector3> { p1f, p2f, p2b, p1b }, Center = (p1f + p2f + p2b + p1b) / 4.0f, Normal = normal });
                        }
                    }
                    subPts.Clear();
                }

                while ((verb = iterator.Next(pts)) != SKPathVerb.Done)
                {
                    if (verb == SKPathVerb.Move) { ProcessSubPath(); subPts.Add(pts[0]); }
                    else if (verb == SKPathVerb.Line) subPts.Add(pts[1]);
                    else if (verb == SKPathVerb.Close) ProcessSubPath();
                }
                ProcessSubPath();

                if (totalPoints > 0)
                {
                    float centerX = sumX / totalPoints;
                    float centerY = sumY / totalPoints;
                    float backCenterX = (centerX - imgCenterX) * backScale + imgCenterX + backOffsetX;
                    float backCenterY = (centerY - imgCenterY) * backScale + imgCenterY + backOffsetY;

                    if (_item.ShowFront && subFrontPaths.Count > 0)
                        faces.Add(new ComplexFace
                        {
                            SubPaths = subFrontPaths,
                            Center = new Vector3(centerX, centerY, depth / 2.0f),
                            Normal = new Vector3(0, 0, 1),
                            Texture = textureBitmap?.Copy(),
                            TexMatrix = GetTextureMatrix(projectionMatrix, 1.0f, rawLeft, rawTop, depth / 2.0f)
                        });
                    if (_item.ShowBack && subBackPaths.Count > 0)
                        faces.Add(new ComplexFace
                        {
                            SubPaths = subBackPaths,
                            Center = new Vector3(backCenterX, backCenterY, -depth / 2.0f),
                            Normal = new Vector3(0, 0, -1),
                            Texture = textureBitmap?.Copy(),
                            TexMatrix = GetTextureMatrix(projectionMatrix, backScale, rawLeft * backScale - imgCenterX * backScale + imgCenterX + backOffsetX, rawTop * backScale - imgCenterY * backScale + imgCenterY + backOffsetY, -depth / 2.0f)
                        });
                }
            }

            textureBitmap?.Dispose();
            return faces;
        }

        // ★3D行列から2Dのテクスチャ投影行列(パースペクティブ対応)を生成！
        private SKMatrix GetTextureMatrix(Matrix4x4 m, float S, float Tx, float Ty, float Z0)
        {
            return new SKMatrix
            {
                ScaleX = S * m.M11,
                SkewX = S * m.M21,
                TransX = Tx * m.M11 + Ty * m.M21 + Z0 * m.M31 + m.M41,
                SkewY = S * m.M12,
                ScaleY = S * m.M22,
                TransY = Tx * m.M12 + Ty * m.M22 + Z0 * m.M32 + m.M42,
                Persp0 = S * m.M14,
                Persp1 = S * m.M24,
                Persp2 = Tx * m.M14 + Ty * m.M24 + Z0 * m.M34 + m.M44
            };
        }

        public void ClearInput() { _input = null; }
        public void Dispose()
        {
            _cpuReadBitmap?.Dispose();
            _outD2DBitmap?.Dispose();
            _transformOutput?.Dispose();
            _mapTransformEffect?.Dispose();
        }
    }

    internal abstract class RenderableFace : IDisposable
    {
        public Vector3 Center { get; set; }
        public Vector3 Normal { get; set; }
        public float DistanceSq { get; set; }
        public SKColor BaseColor { get; set; }
        public float Brightness { get; set; } = 1.0f;

        public void CalculateColorAndBrightness(System.Windows.Media.Color bCol, Vector3 normal, bool enableLighting)
        {
            if (!enableLighting) { BaseColor = new SKColor(bCol.R, bCol.G, bCol.B, bCol.A); Brightness = 1.0f; return; }
            float intensity = Vector3.Dot(normal, Vector3.Normalize(new Vector3(-1, -1, 1)));
            Brightness = 0.3f + 0.7f * Math.Clamp((intensity + 1f) / 2f, 0f, 1f);
            BaseColor = new SKColor((byte)(bCol.R * Brightness), (byte)(bCol.G * Brightness), (byte)(bCol.B * Brightness), bCol.A);
        }

        public abstract void CalcBounds(Matrix4x4 m, ref float minX, ref float minY, ref float maxX, ref float maxY);
        public abstract void Draw(SKCanvas canvas, Matrix4x4 m, SKPaint paint);
        public virtual void Dispose() { }

        protected Vector2 Project(Vector3 p, Matrix4x4 m)
        {
            Vector4 t = Vector4.Transform(p, m);
            float w = Math.Max(t.W, 0.001f);
            return new Vector2(t.X / w, t.Y / w);
        }
        protected void UpdateBounds(Vector2 p, ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
        }
    }

    internal class PolygonFace : RenderableFace
    {
        public List<Vector3> Vertices = new();
        public override void CalcBounds(Matrix4x4 m, ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            foreach (var v in Vertices) UpdateBounds(Project(v, m), ref minX, ref minY, ref maxX, ref maxY);
        }
        public override void Draw(SKCanvas canvas, Matrix4x4 m, SKPaint paint)
        {
            paint.Color = BaseColor;
            paint.Style = SKPaintStyle.StrokeAndFill;
            paint.StrokeWidth = 0.5f;
            paint.StrokeJoin = SKStrokeJoin.Round;

            using var path = new SKPath();
            var p0 = Project(Vertices[0], m);
            path.MoveTo(p0.X, p0.Y);
            for (int i = 1; i < Vertices.Count; i++)
            {
                var p = Project(Vertices[i], m);
                path.LineTo(p.X, p.Y);
            }
            path.Close();
            canvas.DrawPath(path, paint);
        }
    }

    internal class ComplexFace : RenderableFace
    {
        public List<List<Vector3>> SubPaths = new();
        public SKBitmap? Texture;
        public SKMatrix TexMatrix;

        public override void CalcBounds(Matrix4x4 m, ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            foreach (var sub in SubPaths) foreach (var v in sub) UpdateBounds(Project(v, m), ref minX, ref minY, ref maxX, ref maxY);
        }
        public override void Draw(SKCanvas canvas, Matrix4x4 m, SKPaint paint)
        {
            if (Texture != null)
            {
                // ★パースに合わせてテクスチャを完璧にマッピング！
                paint.Shader = SKShader.CreateBitmap(Texture, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, TexMatrix);
                paint.ColorFilter = SKColorFilter.CreateBlendMode(
                    new SKColor((byte)(255 * Brightness), (byte)(255 * Brightness), (byte)(255 * Brightness)),
                    SKBlendMode.Multiply);
            }
            else
            {
                paint.Color = BaseColor;
                paint.Shader = null;
                paint.ColorFilter = null;
            }

            paint.Style = SKPaintStyle.Fill;
            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            foreach (var sub in SubPaths)
            {
                if (sub.Count < 2) continue;
                var cleanPath = new SKPath();
                var p0 = Project(sub[0], m);
                cleanPath.MoveTo(p0.X, p0.Y);
                for (int i = 1; i < sub.Count; i++)
                {
                    var p = Project(sub[i], m);
                    cleanPath.LineTo(p.X, p.Y);
                }
                cleanPath.Close();
                path.AddPath(cleanPath);
            }
            canvas.DrawPath(path, paint);

            paint.Shader?.Dispose();
            paint.ColorFilter?.Dispose();
            paint.Shader = null;
            paint.ColorFilter = null;
        }

        public override void Dispose() { Texture?.Dispose(); }
    }
}