using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.UI;

namespace DriveDeck.Effects
{
    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    /// <summary>
    /// The same stamp as <see cref="OverlayVideoEffect"/>, drawn straight into the camera's own NV12 picture: no conversion to RGB and
    /// back, only the few rows under the text are touched.
    /// </summary>
    public sealed class StampVideoEffect : IBasicVideoEffect
    {
        private const string TextKey = "text";
        private const int MaxTextLength = 160;

        private IPropertySet _config;
        private byte[] _mask;
        private int _maskW, _maskH;
        private string _maskText;
        private int _maskFor;

        public bool IsReadOnly { get { return false; } }
        public MediaMemoryTypes SupportedMemoryTypes { get { return MediaMemoryTypes.Cpu; } }
        public bool TimeIndependent { get { return true; } }

        public IReadOnlyList<VideoEncodingProperties> SupportedEncodingProperties
        {
            get
            {
                var p = new VideoEncodingProperties();
                p.Subtype = "NV12";
                return new List<VideoEncodingProperties> { p };
            }
        }

        public void SetProperties(IPropertySet configuration) { _config = configuration; }
        public void SetEncodingProperties(VideoEncodingProperties encodingProperties, IDirect3DDevice device) { _mask = null; _maskText = null; }

        private string CurrentText()
        {
            object o = null;
            if (_config != null && _config.TryGetValue(TextKey, out o))
            {
                var s = o as string;
                if (s != null) return s.Length > MaxTextLength ? s.Substring(0, MaxTextLength) : s;
            }
            return null;
        }

        /// <summary>Coverage of the text, 0-255 per pixel; drawn once per text, not once per frame.</summary>
        private void MakeMask(string text, int width, int height)
        {
            float size = Math.Max(14f, height / 30f);
            var device = CanvasDevice.GetSharedDevice();
            using (var format = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = size, FontWeight = Windows.UI.Text.FontWeights.SemiBold, WordWrapping = CanvasWordWrapping.NoWrap })
            using (var layout = new CanvasTextLayout(device, text, format, width, height))
            {
                var b = layout.LayoutBounds;
                float pad = size * 0.35f;
                int w = Math.Min(width, ((int)(b.Width + pad * 2) + 1) & ~1), h = Math.Min(height, ((int)(b.Height + pad) + 1) & ~1);
                using (var target = new CanvasRenderTarget(device, w, h, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied))
                {
                    using (var ds = target.CreateDrawingSession())
                    {
                        ds.Clear(Colors.Transparent);
                        ds.DrawTextLayout(layout, pad, pad * 0.5f, Colors.White);
                    }
                    var px = target.GetPixelBytes();
                    var m = new byte[w * h];
                    for (int i = 0; i < m.Length; i++) m[i] = px[i * 4 + 3];
                    _mask = m;
                    _maskW = w;
                    _maskH = h;
                }
            }
            _maskText = text;
            _maskFor = height;
        }

        public unsafe void ProcessFrame(ProcessVideoFrameContext context)
        {
            var input = context.InputFrame.SoftwareBitmap;
            var output = context.OutputFrame.SoftwareBitmap;
            if (input == null || output == null) return;
            input.CopyTo(output);
            string text = CurrentText();
            if (string.IsNullOrEmpty(text) || output.BitmapPixelFormat != BitmapPixelFormat.Nv12) return;
            if (_mask == null || _maskText != text || _maskFor != output.PixelHeight) MakeMask(text, output.PixelWidth, output.PixelHeight);

            using (var buffer = output.LockBuffer(BitmapBufferAccessMode.ReadWrite))
            using (var reference = buffer.CreateReference())
            {
                byte* p;
                uint capacity;
                ((IMemoryBufferByteAccess)reference).GetBuffer(out p, out capacity);
                var y = buffer.GetPlaneDescription(0);
                var uv = buffer.GetPlaneDescription(1);
                int margin = (int)(output.PixelHeight / 30f * 0.35f) & ~1;
                int left = margin, top = (y.Height - _maskH - margin) & ~1;
                int w = Math.Min(_maskW, y.Width - left);
                if (top < 0 || w <= 0) return;
                if (y.StartIndex + (long)y.Stride * y.Height > capacity || uv.StartIndex + (long)uv.Stride * uv.Height > capacity) return;
                fixed (byte* mask = _mask)
                {
                    for (int r = 0; r < _maskH; r++)
                    {
                        byte* line = p + y.StartIndex + (top + r) * y.Stride + left;
                        byte* m = mask + r * _maskW;
                        for (int x = 0; x < w; x++)
                        {
                            int dark = (line[x] * 115) >> 8; // a dark box behind the text
                            line[x] = (byte)(dark + (((235 - dark) * m[x]) >> 8));
                        }
                    }
                    for (int r = 0; r < _maskH / 2; r++)
                    {
                        byte* line = p + uv.StartIndex + (top / 2 + r) * uv.Stride + left;
                        byte* m = mask + r * 2 * _maskW;
                        for (int x = 0; x < w; x++) // colour under the letters becomes grey
                        {
                            int a = m[x];
                            line[x] = (byte)(line[x] + (((128 - line[x]) * a) >> 8));
                        }
                    }
                }
            }
        }

        public void Close(MediaEffectClosedReason reason) { _mask = null; }
        public void DiscardQueuedFrames() { }
    }
}
